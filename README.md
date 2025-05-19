using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace KucoinGridTrader
{
    //───────────────────────────────────────────────────────────────────────────────
    // DTOs
    //───────────────────────────────────────────────────────────────────────────────
    public class TickerData { public decimal Bid; public decimal Ask; }
    public class InstrumentSpec
    {
        public decimal TickSize;
        public int LotPrecision;
        public decimal MinSize;
    }
    public class OpenOrder { public string OrderId = ""; public decimal Price; public string Side = ""; }
    public class OrderStatus
    {
        public string Id = "";
        public string Status = "";
        public decimal DealSize;
        public decimal Price;
    }

    //───────────────────────────────────────────────────────────────────────────────
    // Persistence model
    //───────────────────────────────────────────────────────────────────────────────
    public class GridState
    {
        public Dictionary<decimal, string> GridMap { get; set; } = new();
        public decimal TotalPnl { get; set; }
        public decimal TotalFees { get; set; }
    }

    //───────────────────────────────────────────────────────────────────────────────
    // KuCoin API wrapper with retry & no reuse of HttpRequestMessage
    //───────────────────────────────────────────────────────────────────────────────
    public class KucoinApi : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _key, _secret, _passphrase;
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 1000;

        public KucoinApi(string apiKey, string apiSecret, string rawPassphrase)
        {
            if (string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(apiSecret) ||
                string.IsNullOrWhiteSpace(rawPassphrase))
                throw new ArgumentException("API key, secret and passphrase are required.");

            _key = apiKey;
            _secret = apiSecret;
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            _passphrase = Convert.ToBase64String(
                h.ComputeHash(Encoding.UTF8.GetBytes(rawPassphrase))
            );

            _http = new HttpClient
            {
                BaseAddress = new Uri("https://api.kucoin.com"),
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        public void Dispose() => _http.Dispose();

        private static string GetTimestamp() =>
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        private string Sign(string data)
        {
            using var sha = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            return Convert.ToBase64String(
                sha.ComputeHash(Encoding.UTF8.GetBytes(data))
            );
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path, string body = "")
        {
            var ts = GetTimestamp();
            var pre = ts + method.Method.ToUpperInvariant() + path + body;
            var sig = Sign(pre);

            var req = new HttpRequestMessage(method, path);
            req.Headers.Add("KC-API-KEY", _key);
            req.Headers.Add("KC-API-SIGN", sig);
            req.Headers.Add("KC-API-TIMESTAMP", ts);
            req.Headers.Add("KC-API-PASSPHRASE", _passphrase);
            req.Headers.Add("KC-API-KEY-VERSION", "2");

            if (!string.IsNullOrEmpty(body) && method != HttpMethod.Get)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            return req;
        }

        private static decimal ParseDecimal(JsonElement e) =>
            e.ValueKind == JsonValueKind.Number
            ? e.GetDecimal()
            : decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);

        private void EnsureSuccess(JsonElement root)
        {
            if (!root.TryGetProperty("code", out var c) || c.GetString() != "200000")
            {
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : root.ToString();
                throw new Exception($"KuCoin API error: {msg}");
            }
        }

        // now accepts a factory so we get a fresh HttpRequestMessage each attempt
        private async Task<JsonElement> SendAsyncWithRetry(Func<HttpRequestMessage> requestFactory)
        {
            for (int i = 0; i < MaxRetries; i++)
            {
                using var req = requestFactory();
                try
                {
                    var res = await _http.SendAsync(req);
                    var text = await res.Content.ReadAsStringAsync();
                    var root = JsonDocument.Parse(text).RootElement;
                    EnsureSuccess(root);
                    return root.GetProperty("data");
                }
                catch when (i < MaxRetries - 1)
                {
                    await Task.Delay(RetryDelayMs);
                }
            }
            throw new Exception("API request failed after retries.");
        }

        public async Task<TickerData> FetchTickerAsync(string symbol)
        {
            string path = $"/api/v1/market/orderbook/level1?symbol={symbol}";
            var data = await SendAsyncWithRetry(() => CreateRequest(HttpMethod.Get, path));
            return new TickerData
            {
                Bid = ParseDecimal(data.GetProperty("bestBid")),
                Ask = ParseDecimal(data.GetProperty("bestAsk"))
            };
        }

        public async Task<InstrumentSpec> FetchInstrumentInfoAsync(string symbol)
        {
            string path = "/api/v1/symbols";
            var arr = (await SendAsyncWithRetry(() => CreateRequest(HttpMethod.Get, path)))
                      .EnumerateArray();
            foreach (var e in arr)
            {
                if (e.GetProperty("symbol").GetString() == symbol)
                {
                    var tick = ParseDecimal(e.GetProperty("priceIncrement"));
                    var baseInc = e.GetProperty("baseIncrement").GetString()!;
                    var minSz = ParseDecimal(e.GetProperty("baseMinSize"));
                    int prec = baseInc.Contains('.')
                        ? baseInc.Split('.')[1].TrimEnd('0').Length
                        : 0;
                    return new InstrumentSpec
                    {
                        TickSize = tick,
                        LotPrecision = prec,
                        MinSize = minSz
                    };
                }
            }
            throw new Exception($"Symbol '{symbol}' not found.");
        }

        public async Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol)
        {
            string path = $"/api/v1/orders?symbol={symbol}&status=active";
            var data = await SendAsyncWithRetry(() => CreateRequest(HttpMethod.Get, path));

            var items = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var arr)
                ? arr.EnumerateArray()
                : data.EnumerateArray();

            return items.Select(o => new OpenOrder
            {
                OrderId = o.GetProperty("id").GetString()!,
                Price = ParseDecimal(o.GetProperty("price")),
                Side = o.GetProperty("side").GetString()!
            }).ToList();
        }

        public async Task<OrderStatus> GetOrderStatusAsync(string orderId)
        {
            string path = $"/api/v1/orders/{orderId}";
            var data = await SendAsyncWithRetry(() => CreateRequest(HttpMethod.Get, path));
            return new OrderStatus
            {
                Id = data.GetProperty("id").GetString()!,
                Status = data.GetProperty("status").GetString()!,
                DealSize = ParseDecimal(data.GetProperty("dealSize")),
                Price = ParseDecimal(data.GetProperty("price"))
            };
        }

        public async Task<string> PlaceLimitAsync(string symbol, string side, decimal price, decimal size)
        {
            var bodyObj = new
            {
                clientOid = Guid.NewGuid().ToString("N"),
                side = side.ToLower(),
                symbol,
                type = "limit",
                price = price.ToString(CultureInfo.InvariantCulture),
                size = size.ToString(CultureInfo.InvariantCulture),
                timeInForce = "GTC"
            };
            var body = JsonSerializer.Serialize(bodyObj);
            var data = await SendAsyncWithRetry(() => CreateRequest(HttpMethod.Post, "/api/v1/orders", body));
            return data.GetProperty("orderId").GetString()!;
        }

        public async Task CancelOrderAsync(string orderId)
            => await SendAsyncWithRetry(() => CreateRequest(HttpMethod.Delete, $"/api/v1/orders/{orderId}"));

        public async Task CancelAllAsync(string symbol)
        {
            var open = await GetOpenOrdersAsync(symbol);
            foreach (var o in open)
                await CancelOrderAsync(o.OrderId);
        }
    }

    //───────────────────────────────────────────────────────────────────────────────
    // GridTrader without Bollinger, pure grid
    //───────────────────────────────────────────────────────────────────────────────
    public class GridTrader
    {
        private readonly KucoinApi _api;
        private readonly string _symbol;
        private readonly int _levels;
        private readonly decimal _qty;
        private readonly decimal _rangePct;
        private readonly string _stateFile = "grid_state.json";
        private readonly string _openOrdersFile = "open_orders.json";

        private readonly decimal _feeRate = 0.001m; // 0.1% per side
        private readonly TimeSpan _orderCooldown = TimeSpan.FromSeconds(5);
        private DateTime _lastTradeTime = DateTime.MinValue;

        private decimal _tick, _initialPrice, _lower, _upper, _step;
        private int _qtyPrec;
        private decimal _minQty;
        private decimal _lastBuyPrice = 0m;
        private GridState _state = new();

        public GridTrader(KucoinApi api, string symbol, int levels, decimal qty, decimal rangePct)
        {
            _api = api;
            _symbol = symbol;
            _levels = levels;
            _qty = qty;
            _rangePct = rangePct;
        }

        public async Task StartAsync()
        {
            await LoadStateOrInitializeAsync();

            if (_qty < _minQty)
                throw new Exception($"Configured qty ({_qty}) is less than exchange min size ({_minQty}).");

            Console.WriteLine($"[{DateTime.Now:HH:mm}] Started {_symbol} | Levels={_levels}, Qty={_qty}, Range={_rangePct:P}, PnL={_state.TotalPnl:F2}, Fees={_state.TotalFees:F2}");

            while (true)
            {
                try
                {
                    var t = await _api.FetchTickerAsync(_symbol);
                    Console.WriteLine($"[{DateTime.Now:HH:mm}] Price={t.Bid:F2}");

                    await EvaluateGridAsync(t.Bid, t.Ask);
                    await Task.Delay(10_000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR {DateTime.Now:HH:mm}] {ex.Message}");
                    await Task.Delay(5_000);
                }
            }
        }

        private async Task LoadStateOrInitializeAsync()
        {
            var spec = await _api.FetchInstrumentInfoAsync(_symbol);
            _tick = spec.TickSize;
            _qtyPrec = spec.LotPrecision;
            _minQty = spec.MinSize;
            var t = await _api.FetchTickerAsync(_symbol);
            _initialPrice = (t.Bid + t.Ask) / 2m;

            bool needsSetup = true;
            if (File.Exists(_stateFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_stateFile);
                    _state = JsonSerializer.Deserialize<GridState>(json,
                                  new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                    needsSetup = !_state.GridMap.Any();
                    Console.WriteLine($"Loaded state from {_stateFile}");
                }
                catch
                {
                    Console.WriteLine($"[WARN] Failed to load {_stateFile}");
                }
            }

            if (needsSetup)
            {
                Console.WriteLine("[INFO] Initializing new grid...");
                _state = new GridState();
                await SetupGridAsync();
            }

            await SaveStateAsync();
        }

        private async Task SetupGridAsync()
        {
            await _api.CancelAllAsync(_symbol);
            _lower = _initialPrice * (1 - _rangePct);
            _upper = _initialPrice * (1 + _rangePct);
            _step = (_upper - _lower) / _levels;

            _state.GridMap.Clear();
            for (int i = 0; i <= _levels; i++)
            {
                var price = RoundPrice(_lower + _step * i);
                _state.GridMap[price] = price < _initialPrice ? "buy" : "sell";
            }
        }

        private async Task EvaluateGridAsync(decimal bid, decimal ask)
        {
            var open = await _api.GetOpenOrdersAsync(_symbol);
            var toRemove = new List<decimal>();
            var toAdd = new Dictionary<decimal, string>();

            foreach (var kv in _state.GridMap)
            {
                var price = kv.Key;
                var side = kv.Value;
                bool hasOpen = open.Any(o => o.Price == price && o.Side == side);

                bool shouldAct =
                    !hasOpen ||
                    (side == "buy" && bid <= price) ||
                    (side == "sell" && ask >= price);

                if (!shouldAct) continue;
                if (DateTime.UtcNow - _lastTradeTime < _orderCooldown) continue;

                Console.WriteLine(hasOpen
                    ? $"▶ WAIT FILL {side.ToUpper()} @ {price:F2}"
                    : $"▶ PLACE     {side.ToUpper()} @ {price:F2}");

                var orderQty = Math.Max(RoundQty(_qty), _minQty);
                Console.WriteLine($"    Qty={orderQty} (min={_minQty}, prec={_qtyPrec})");

                string orderId = hasOpen
                    ? open.First(o => o.Price == price && o.Side == side).OrderId
                    : await _api.PlaceLimitAsync(_symbol, side, price, orderQty);

                await WaitForFillAsync(orderId);

                var fee = orderQty * price * _feeRate;
                _state.TotalFees += fee;

                if (side == "buy")
                {
                    _lastBuyPrice = price;
                    Console.WriteLine($"🟢 Bought {orderQty} @ {price:F2} (fee {fee:F6})");
                }
                else
                {
                    var gross = (price - _lastBuyPrice) * orderQty;
                    var net = gross - (fee + _lastBuyPrice * orderQty * _feeRate);
                    _state.TotalPnl += net;
                    var tag = net >= 0 ? "🔵 Profit" : "🔴 Loss";
                    Console.WriteLine($"{tag}: net {net:F2} | TotalPnL {_state.TotalPnl:F2} | fee {fee:F6}");
                }

                Console.WriteLine($"❌ Removing {side.ToUpper()} @ {price:F2}");
                var nextPrice = side == "buy"
                    ? RoundPrice(price + _step)
                    : RoundPrice(price - _step);
                var nextSide = side == "buy" ? "sell" : "buy";
                Console.WriteLine($"➕ Adding {nextSide.ToUpper()} @ {nextPrice:F2}");

                toRemove.Add(price);
                toAdd[nextPrice] = nextSide;
                _lastTradeTime = DateTime.UtcNow;
            }

            foreach (var r in toRemove) _state.GridMap.Remove(r);
            foreach (var kvp in toAdd) _state.GridMap[kvp.Key] = kvp.Value;

            await SaveStateAsync();
        }

        private async Task WaitForFillAsync(string orderId)
        {
            while (true)
            {
                var status = await _api.GetOrderStatusAsync(orderId);
                if (status.Status == "done") break;
                if (status.Status == "canceled" || status.Status == "failed")
                    throw new Exception($"Order {orderId} status {status.Status}");
                await Task.Delay(1_000);
            }
        }

        private async Task SaveStateAsync()
        {
            var json = JsonSerializer.Serialize(_state,
                       new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_stateFile, json);

            var open = await _api.GetOpenOrdersAsync(_symbol);
            var openJson = JsonSerializer.Serialize(open,
                       new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_openOrdersFile, openJson);
        }

        private decimal RoundPrice(decimal p)
            => Math.Round(p / _tick, MidpointRounding.AwayFromZero) * _tick;

        private decimal RoundQty(decimal q)
            => Math.Round(q, _qtyPrec, MidpointRounding.AwayFromZero);
    }

    //───────────────────────────────────────────────────────────────────────────────
    // Program entry
    //───────────────────────────────────────────────────────────────────────────────
    public static class Program
    {
        public static async Task Main()
        {
            string apiKey = "";
            string apiSecret = "";
            string passphrase = "";
            string symbol = "BTC-USDT";
            int levels = 7;
            decimal qty = 0.001m;
            decimal rangePct = 0.05m;

            using var api = new KucoinApi(apiKey, apiSecret, passphrase);
            var trader = new GridTrader(api, symbol, levels, qty, rangePct);

            Console.WriteLine("Press any key to start grid trading...");
            Console.ReadKey(true);
            Console.WriteLine();

            await trader.StartAsync();
        }
    }
}
