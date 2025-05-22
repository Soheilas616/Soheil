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
    // جلوگیری از خواب رفتن سیستم
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
    // وضعیت ربات (برای ری‌استارت)
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
    // لایه دسترسی به API کوکوین
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
    // منطق گرید تریدینگ
    //───────────────────────────────────────────────────────────────────────────────
    class GridTrader
    {
        private readonly KucoinApi _api;
        private readonly string _symbol;
        private readonly int _levels;
        private readonly decimal _profitPct, _stopLossPct, _riskUsd;
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
            while (true)
            {
                try
                {
                    await ExecuteCycleAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] {ex.Message}, restarting in 10s...");
                    await Task.Delay(10_000);
                }
            }
        }

        private async Task ExecuteCycleAsync()
        {
            SleepManager.PreventSleep();
            LoadState();

            // یک‌بار گرفتن مشخصات نماد
            var spec = await _api.FetchInstrumentInfoAsync(_symbol);
            decimal tick = spec.TickSize;
            int prec = spec.LotPrecision;
            string fmt = "F" + prec; // برای چاپ عدد با دقت متغیر

            if (!_state.BuysPlaced)
            {
                var t0 = await _api.FetchTickerAsync(_symbol);
                decimal mid = (t0.Bid + t0.Ask) / 2m;

                // محاسبه قیمت‌های گرید
                _state.BuyPrices = Enumerable.Range(0, _levels)
                    .Select(i =>
                        Math.Round(
                            mid * (1 - _profitPct + 2 * _profitPct * i / (_levels - 1))
                            / tick,
                            MidpointRounding.AwayFromZero
                        ) * tick
                    ).ToList();

                // حجم هر سفارش
                decimal rawQ = (_riskUsd / _levels) / mid;
                _state.Qty = Math.Max(
                                  Math.Round(rawQ, prec, MidpointRounding.AwayFromZero),
                                  spec.MinSize
                                );

                // پاک‌کردن سفارش‌های قبلی
                await _api.CancelAllAsync(_symbol);

                // ثبت سطح‌های خرید
                for (int i = 0; i < _levels; i++)
                {
                    decimal price = _state.BuyPrices[i];
                    string oid = await _api.PlaceLimitAsync(
                                         _symbol, "buy", price, _state.Qty);
                    _state.BuyOrderIds.Add(oid);
                    Console.WriteLine(
                        $"[GRID BUY {i + 1}/{_levels}] @ {price.ToString(fmt)}");
                }

                _state.StartTime = DateTime.UtcNow;
                _state.BuysPlaced = true;
                SaveState();
            }

            // حلقهٔ مانیتورینگ
            while (true)
            {
                var t = await _api.FetchTickerAsync(_symbol);

                // ری‌ست پس از ۱ ساعت
                if (DateTime.UtcNow - _state.StartTime > TimeSpan.FromHours(1))
                {
                    Console.WriteLine("⏱ 1h elapsed → reset");
                    await _api.CancelAllAsync(_symbol);
                    ResetState();
                    return;
                }

                // چک تک‌تک سفارش‌ها
                for (int i = 0; i < _state.BuyOrderIds.Count; i++)
                {
                    var oid = _state.BuyOrderIds[i];
                    if (string.IsNullOrEmpty(oid)) continue;

                    var os = await _api.GetOrderStatusAsync(oid);
                    if (os.Status == "done")
                    {
                        decimal buyPrice = _state.BuyPrices[i];
                        decimal unrounded = buyPrice * (1 + _profitPct);
                        decimal sellPrice = Math.Round(
                            unrounded / tick,
                            MidpointRounding.AwayFromZero
                        ) * tick;

                        Console.WriteLine(
                            $"🟢 FILLED BUY @ {buyPrice.ToString(fmt)}, placing SELL @ {sellPrice.ToString(fmt)}");
                        await _api.PlaceLimitAsync(_symbol, "sell", sellPrice, _state.Qty);

                        // علامت‌گذاری به‌عنوان انجام‌شده
                        _state.BuyOrderIds[i] = "";
                        SaveState();
                    }
                }

                // حد ضرر
                decimal worstBuy = _state.BuyPrices.Min();
                if (t.Bid <= worstBuy * (1 - _stopLossPct))
                {
                    Console.WriteLine($"🔴 STOP-LOSS @ {t.Bid.ToString(fmt)}");
                    await _api.CancelAllAsync(_symbol);
                    ResetState();
                    return;
                }

                await Task.Delay(500);
            }
        }

        private void LoadState()
        {
            if (File.Exists(_stateFile))
                _state = JsonSerializer.Deserialize<BotState>(
                             File.ReadAllText(_stateFile))!;
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
    // نقطهٔ ورود برنامه
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
                totalRiskUsd: 5.0m,     // $5
                stopLossPct: 0.015m    // 1.5%
            );

            Console.WriteLine("Starting SHORT-TERM GRID TRADER…");
            await bot.RunForeverAsync();
        }
    }
}
