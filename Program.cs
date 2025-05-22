using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace KucoinGridBot
{
    //───────────────────────────────────────────────────────────────────────────────
    // جلوگیری از خواب رفتن ویندوز
    //───────────────────────────────────────────────────────────────────────────────
    static class SleepManager
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_AWAYMODE_REQUIRED = 0x00000040;
        public static void PreventSleep() =>
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
    }

    //───────────────────────────────────────────────────────────────────────────────
    // وضعیت ربات برای پایداری ری‌استارت
    //───────────────────────────────────────────────────────────────────────────────
    class BotState
    {
        public bool BuysPlaced { get; set; }
        public List<string> BuyOrderIds { get; set; } = new();
        public List<decimal> BuyPrices { get; set; } = new();
        public decimal Qty { get; set; }
        public DateTime StartTime { get; set; }
    }

    class OrderStatus { public string Status = ""; public decimal DealSize; }
    class TickerData { public decimal Bid, Ask; }
    class InstrumentSpec { public decimal TickSize; public int LotPrecision; public decimal MinSize; }

    //───────────────────────────────────────────────────────────────────────────────
    // توابع اندیکاتور EMA و RSI
    //───────────────────────────────────────────────────────────────────────────────
    static class Indicators
    {
        public static List<decimal> EMA(decimal[] values, int period)
        {
            var ema = new List<decimal>();
            if (values.Length < period) return ema;
            decimal sum = 0m;
            for (int i = 0; i < period; i++) sum += values[i];
            decimal prev = sum / period;
            ema.Add(prev);
            decimal mult = 2m / (period + 1);
            for (int i = period; i < values.Length; i++)
            {
                prev = (values[i] - prev) * mult + prev;
                ema.Add(prev);
            }
            return ema;
        }

        public static List<decimal> RSI(decimal[] values, int period)
        {
            var rsis = new List<decimal>();
            if (values.Length <= period) return rsis;
            decimal gain = 0m, loss = 0m;
            for (int i = 1; i <= period; i++)
            {
                var change = values[i] - values[i - 1];
                if (change > 0) gain += change;
                else loss -= change;
            }
            decimal avgGain = gain / period;
            decimal avgLoss = loss / period;
            rsis.Add(avgLoss == 0 ? 100m : 100m - (100m / (1m + avgGain / avgLoss)));

            for (int i = period + 1; i < values.Length; i++)
            {
                var change = values[i] - values[i - 1];
                decimal g = change > 0 ? change : 0;
                decimal l = change < 0 ? -change : 0;
                avgGain = ((avgGain * (period - 1)) + g) / period;
                avgLoss = ((avgLoss * (period - 1)) + l) / period;
                rsis.Add(avgLoss == 0 ? 100m : 100m - (100m / (1m + avgGain / avgLoss)));
            }
            return rsis;
        }
    }

    //───────────────────────────────────────────────────────────────────────────────
    // لایه‌ی دسترسی به API کوکوین
    //───────────────────────────────────────────────────────────────────────────────
    class KucoinApi : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _key, _secret, _passphrase;
        private const int RETRY_MS = 5000;

        public KucoinApi(string key, string secret, string pass)
        {
            _key = key;
            _secret = secret;
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            _passphrase = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(pass)));
            _http = new HttpClient
            {
                BaseAddress = new Uri("https://api.kucoin.com"),
                Timeout = TimeSpan.FromSeconds(10)
            };
        }
        public void Dispose() => _http.Dispose();

        private static decimal ParseDecimal(JsonElement e) =>
            e.ValueKind == JsonValueKind.Number
              ? e.GetDecimal()
              : decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);

        private string Sign(string data)
        {
            using var sha = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(data)));
        }

        private HttpRequestMessage CreateRequest(HttpMethod m, string path, string body = "")
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var pre = ts + m.Method + path + body;
            var sig = Sign(pre);

            var req = new HttpRequestMessage(m, path);
            req.Headers.Add("KC-API-KEY", _key);
            req.Headers.Add("KC-API-SIGN", sig);
            req.Headers.Add("KC-API-TIMESTAMP", ts);
            req.Headers.Add("KC-API-PASSPHRASE", _passphrase);
            req.Headers.Add("KC-API-KEY-VERSION", "2");
            if (!string.IsNullOrEmpty(body) && m != HttpMethod.Get)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return req;
        }

        private async Task<JsonElement> SendAsync(Func<HttpRequestMessage> rf)
        {
            while (true)
            {
                HttpResponseMessage res;
                try { using var req = rf(); res = await _http.SendAsync(req); }
                catch { await Task.Delay(RETRY_MS); continue; }

                var txt = await res.Content.ReadAsStringAsync();
                var root = JsonDocument.Parse(txt).RootElement;
                var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
                if (code != "200000")
                    throw new Exception($"KuCoin API error: {root}");
                if (!root.TryGetProperty("data", out var d))
                    throw new Exception($"No data: {root}");
                return d;
            }
        }

        public async Task<TickerData> FetchTickerAsync(string sym)
        {
            var d = await SendAsync(() =>
                CreateRequest(HttpMethod.Get,
                  $"/api/v1/market/orderbook/level1?symbol={sym}"));
            return new TickerData
            {
                Bid = ParseDecimal(d.GetProperty("bestBid")),
                Ask = ParseDecimal(d.GetProperty("bestAsk"))
            };
        }

        public async Task<InstrumentSpec> FetchInstrumentInfoAsync(string sym)
        {
            var arr = (await SendAsync(() =>
                CreateRequest(HttpMethod.Get, "/api/v1/symbols"))).EnumerateArray();
            foreach (var e in arr)
                if (e.GetProperty("symbol").GetString() == sym)
                    return new InstrumentSpec
                    {
                        TickSize = ParseDecimal(e.GetProperty("priceIncrement")),
                        LotPrecision = e.GetProperty("baseIncrement")
                                         .GetString()!
                                         .Split('.')[1].Length,
                        MinSize = ParseDecimal(e.GetProperty("baseMinSize"))
                    };
            throw new Exception("Symbol not found");
        }

        public async Task<decimal[]> FetchClosesAsync(string sym, int limit = 100)
        {
            var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var start = end - limit * 60;
            var path = $"/api/v1/market/candles?type=1min&symbol={sym}"
                      + $"&startAt={start}&endAt={end}";
            var arr = (await SendAsync(() => CreateRequest(HttpMethod.Get, path)))
                      .EnumerateArray();
            return arr.Select(e => ParseDecimal(e[2])).ToArray();
        }

        public async Task<string> PlaceLimitAsync(string sym, string side, decimal price, decimal size)
        {
            var body = JsonSerializer.Serialize(new
            {
                clientOid = Guid.NewGuid().ToString("N"),
                side = side.ToLower(),
                symbol = sym,
                type = "limit",
                price = price.ToString(CultureInfo.InvariantCulture),
                size = size.ToString(CultureInfo.InvariantCulture),
                timeInForce = "GTC"
            });
            var d = await SendAsync(() => CreateRequest(HttpMethod.Post, "/api/v1/orders", body));
            if (d.TryGetProperty("orderId", out var o1)) return o1.GetString()!;
            if (d.TryGetProperty("order_id", out var o2)) return o2.GetString()!;
            throw new Exception("PlaceLimit returned no orderId");
        }

        public async Task<OrderStatus> GetOrderStatusAsync(string id)
        {
            var d = await SendAsync(() =>
                CreateRequest(HttpMethod.Get, $"/api/v1/orders/{id}"));
            var filled = d.TryGetProperty("dealSize", out var ds) ? ds
                       : d.TryGetProperty("filledSize", out var fs) ? fs
                       : default;
            decimal deal = filled.ValueKind != JsonValueKind.Undefined
                         ? ParseDecimal(filled)
                         : 0m;

            string status;
            if (d.TryGetProperty("status", out var s))
                status = s.GetString()!;
            else if (d.TryGetProperty("isActive", out var a))
            {
                bool ia = a.GetBoolean();
                if (!ia && d.TryGetProperty("cancelExist", out var cxl) && cxl.GetBoolean())
                    status = "cancelled";
                else
                    status = ia ? "active" : "done";
            }
            else
                status = "unknown";

            return new OrderStatus { Status = status, DealSize = deal };
        }

        public async Task CancelAllAsync(string sym)
        {
            var data = await SendAsync(() =>
                CreateRequest(HttpMethod.Get,
                  $"/api/v1/orders?symbol={sym}&status=active"));
            JsonElement items = data.ValueKind == JsonValueKind.Object
                                  && data.TryGetProperty("items", out var arr) ? arr
                                : data.ValueKind == JsonValueKind.Array ? data
                                : default;
            if (items.ValueKind == JsonValueKind.Array)
                foreach (var o in items.EnumerateArray())
                    if (o.TryGetProperty("id", out var oid))
                        await SendAsync(() =>
                            CreateRequest(HttpMethod.Delete,
                              $"/api/v1/orders/{oid.GetString()!}"));
        }
    }

    //───────────────────────────────────────────────────────────────────────────────
    // منطق گرید تریدینگ با فیلتر EMA/RSI
    //───────────────────────────────────────────────────────────────────────────────
    class GridTrader
    {
        private readonly KucoinApi _api;
        private readonly string _symbol;
        private readonly int _levels;
        private readonly decimal _profitPct, _stopLossPct, _riskUsd;
        private readonly TimeSpan _maxIdle = TimeSpan.FromHours(2);
        private readonly string _stateFile = "bot_state.json";
        private BotState _state = new();

        public GridTrader(
            KucoinApi api, string symbol,
            int levels, decimal profitPct,
            decimal totalRiskUsd, decimal stopLossPct)
        {
            _api = api;
            _symbol = symbol;
            _levels = levels;
            _profitPct = profitPct;
            _riskUsd = totalRiskUsd;
            _stopLossPct = stopLossPct;
        }

        public async Task RunForeverAsync()
        {
            Console.WriteLine("Starting ENHANCED GRID TRADER…");
            while (true)
            {
                try
                {
                    await ExecuteCycleAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] {ex.Message}, resetting state and restarting in 10s...");
                    ResetState();
                    await Task.Delay(10_000);
                }
            }
        }

        private async Task ExecuteCycleAsync()
        {
            SleepManager.PreventSleep();
            LoadState();

            // ────────────────────────────────────────────────────────────────────
            // 1) مشخصات نماد
            var spec = await _api.FetchInstrumentInfoAsync(_symbol);
            decimal tick = spec.TickSize;
            int prec = spec.LotPrecision;
            string fmt = "F" + prec;

            // 2) قیمت وسط بازار
            var t0 = await _api.FetchTickerAsync(_symbol);
            decimal mid = (t0.Bid + t0.Ask) / 2m;

            // 3) فیلتر روند: EMA10>EMA30 & RSI14<70
            var closes = await _api.FetchClosesAsync(_symbol, 100);
            var ema10 = Indicators.EMA(closes, 10);
            var ema30 = Indicators.EMA(closes, 30);
            var rsi14 = Indicators.RSI(closes, 14);
            var lastEma10 = ema10.Any() ? ema10.Last() : mid;
            var lastEma30 = ema30.Any() ? ema30.Last() : mid;
            var lastRsi = rsi14.Any() ? rsi14.Last() : 50m;
            if (!(lastEma10 > lastEma30 && lastRsi < 70m))
            {
                Console.WriteLine(
                  $"🔶 Trend not OK (EMA10={lastEma10.ToString(fmt)}, EMA30={lastEma30.ToString(fmt)}, RSI={lastRsi:F0}), skipping.");
                await Task.Delay(500);
                return;
            }

            // 4) Idle reset: اگر برای maxIdle اتفاقی نیفتاده باشه
            if (_state.BuysPlaced &&
                DateTime.UtcNow - _state.StartTime > _maxIdle)
            {
                Console.WriteLine($"⏱ {_maxIdle.TotalHours}h idle → reset");
                await _api.CancelAllAsync(_symbol);
                ResetState();
                return;
            }

            // 5) ثبت اولیه گرید Buy
            if (!_state.BuysPlaced)
            {
                if (_levels < 2)
                    throw new Exception("levels must be >= 2");

                // محاسبه قیمت‌های گرید
                _state.BuyPrices = Enumerable.Range(0, _levels)
                    .Select(i =>
                        Math.Round(
                            mid * (1 - _profitPct + 2m * _profitPct * i / (_levels - 1))
                            / tick,
                            MidpointRounding.AwayFromZero
                        ) * tick
                    ).ToList();

                // محاسبه حجم هر سفارش
                decimal rawQ = (_riskUsd / _levels) / mid;
                _state.Qty = Math.Max(
                                  Math.Round(rawQ, prec, MidpointRounding.AwayFromZero),
                                  spec.MinSize
                                );

                // کنسل سفارش‌های قبلی و ثبت گرید
                await _api.CancelAllAsync(_symbol);
                for (int i = 0; i < _levels; i++)
                {
                    var price = _state.BuyPrices[i];
                    var oid = await _api.PlaceLimitAsync(
                                    _symbol, "buy", price, _state.Qty);
                    _state.BuyOrderIds.Add(oid);
                    Console.WriteLine($"[GRID BUY {i + 1}/{_levels}] @ {price.ToString(fmt)}");
                }

                _state.StartTime = DateTime.UtcNow;
                _state.BuysPlaced = true;
                SaveState();
            }

            // 6) مانیتور پر شدن BUY و ثبت SELL
            foreach (var idx in Enumerable.Range(0, _state.BuyOrderIds.Count))
            {
                var oid = _state.BuyOrderIds[idx];
                if (string.IsNullOrEmpty(oid)) continue;
                var os = await _api.GetOrderStatusAsync(oid);
                if (os.Status == "done")
                {
                    decimal buyPrice = _state.BuyPrices[idx];
                    decimal sellPrice = Math.Round(
                        buyPrice * (1 + _profitPct) / tick,
                        MidpointRounding.AwayFromZero
                    ) * tick;

                    Console.WriteLine(
                        $"🟢 FILLED BUY @ {buyPrice.ToString(fmt)}, placing SELL @ {sellPrice.ToString(fmt)}"
                    );
                    await _api.PlaceLimitAsync(_symbol, "sell", sellPrice, _state.Qty);

                    // حذف از لیست تا دوباره بررسی نشود
                    _state.BuyOrderIds[idx] = "";
                    SaveState();
                }
            }

            // 7) Stop‐Loss کلی
            var worstBuy = _state.BuyPrices.Min();
            if (t0.Bid <= worstBuy * (1 - _stopLossPct))
            {
                Console.WriteLine($"🔴 STOP‐LOSS @ {t0.Bid.ToString(fmt)}");
                await _api.CancelAllAsync(_symbol);
                ResetState();
                return;
            }

            await Task.Delay(500);
        }

        private void LoadState()
        {
            if (File.Exists(_stateFile))
            {
                try
                {
                    _state = JsonSerializer.Deserialize<BotState>(
                        File.ReadAllText(_stateFile))!;
                }
                catch { /* ignore on parse fail */ }
            }
        }

        private void SaveState()
        {
            File.WriteAllText(_stateFile,
                JsonSerializer.Serialize(_state,
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        private void ResetState()
        {
            _state = new BotState();
            if (File.Exists(_stateFile))
                File.Delete(_stateFile);
        }
    }

    //───────────────────────────────────────────────────────────────────────────────
    // نقطه‌ٔ ورود برنامه
    //───────────────────────────────────────────────────────────────────────────────
    static class Program
    {
        public static async Task Main()
        {
            const string key = "6822123d61d41900017228b5";
            const string secret = "8442d315-2148-4712-9890-6fae76c984c4";
            const string pass = "0018097677";
            const string symbol = "BTC-USDT";

            using var api = new KucoinApi(key, secret, pass);
            var bot = new GridTrader(
                api,
                symbol,
                levels: 5,
                profitPct: 0.005m,   // 0.5%
                totalRiskUsd: 5.0m,     // $5 risk total
                stopLossPct: 0.015m    // 1.5% stop-loss
            );

            await bot.RunForeverAsync();
        }
    }
}
