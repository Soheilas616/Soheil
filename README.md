using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KucoinGridTrader
{
    //───────────────────────────────────────────────────────────────────────────────
    // DTOs
    //───────────────────────────────────────────────────────────────────────────────
    public class TickerData
    {
        public decimal Bid;
        public decimal Ask;
    }

    public class InstrumentSpec
    {
        public decimal TickSize;
        public int LotPrecision;
    }

    public class OpenOrder
    {
        public string OrderId = "";
        public decimal Price;
        public string Side = "";
    }

    //───────────────────────────────────────────────────────────────────────────────
    // KuCoin API wrapper
    //───────────────────────────────────────────────────────────────────────────────
    public class KucoinApi : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _key;
        private readonly string _secret;
        private readonly string _passphrase;

        public KucoinApi(string apiKey, string apiSecret, string rawPassphrase)
        {
            if (string.IsNullOrWhiteSpace(apiKey) ||
                string.IsNullOrWhiteSpace(apiSecret) ||
                string.IsNullOrWhiteSpace(rawPassphrase))
                throw new ArgumentException("API key, secret and passphrase are all required.");

            _key = apiKey;
            _secret = apiSecret;

            // KC-API-PASSPHRASE = Base64(HMACSHA256(secret, rawPassphrase))
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

        private static string GetTimestamp()
            => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

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

        private static decimal ParseDecimal(JsonElement e)
            => e.ValueKind == JsonValueKind.Number
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

        public async Task<TickerData> FetchTickerAsync(string symbol)
        {
            var path = $"/api/v1/market/orderbook/level1?symbol={symbol}";
            var req = CreateRequest(HttpMethod.Get, path);
            var res = await _http.SendAsync(req);
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            EnsureSuccess(root);

            var d = root.GetProperty("data");
            return new TickerData
            {
                Bid = ParseDecimal(d.GetProperty("bestBid")),
                Ask = ParseDecimal(d.GetProperty("bestAsk"))
            };
        }

        public async Task<List<decimal>> FetchKlinesAsync(string symbol, string interval, int limit)
        {
            var path = $"/api/v1/market/candles?symbol={symbol}&type={interval}&limit={limit}";
            var req = CreateRequest(HttpMethod.Get, path);
            var res = await _http.SendAsync(req);
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            EnsureSuccess(root);

            var list = new List<decimal>(limit);
            foreach (var arr in root.GetProperty("data").EnumerateArray())
                list.Add(decimal.Parse(arr[4].GetString()!, CultureInfo.InvariantCulture));
            return list;
        }

        public async Task<InstrumentSpec> FetchInstrumentInfoAsync(string symbol)
        {
            var path = "/api/v1/symbols";
            var req = CreateRequest(HttpMethod.Get, path);
            var res = await _http.SendAsync(req);
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            EnsureSuccess(root);

            foreach (var e in root.GetProperty("data").EnumerateArray())
            {
                if (e.GetProperty("symbol").GetString() == symbol)
                {
                    var tick = decimal.Parse(
                        e.GetProperty("priceIncrement").GetString()!,
                        CultureInfo.InvariantCulture
                    );

                    var baseInc = e.GetProperty("baseIncrement").GetString()!;
                    int prec = baseInc.Contains('.')
                        ? baseInc.Split('.')[1].TrimEnd('0').Length
                        : 0;

                    return new InstrumentSpec
                    {
                        TickSize = tick,
                        LotPrecision = prec
                    };
                }
            }

            throw new Exception($"Symbol “{symbol}” not found in exchange’s symbol list.");
        }

        public async Task<List<OpenOrder>> GetOpenOrdersAsync(string symbol)
        {
            var path = $"/api/v1/orders?symbol={symbol}&status=active";
            var req = CreateRequest(HttpMethod.Get, path);
            var res = await _http.SendAsync(req);
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            EnsureSuccess(root);

            var data = root.GetProperty("data");
            var items = data.TryGetProperty("items", out var arr) ? arr : data;
            var list = new List<OpenOrder>();

            foreach (var o in items.EnumerateArray())
            {
                list.Add(new OpenOrder
                {
                    OrderId = o.GetProperty("id").GetString()!,
                    Price = decimal.Parse(o.GetProperty("price").GetString()!, CultureInfo.InvariantCulture),
                    Side = o.GetProperty("side").GetString()!
                });
            }

            return list;
        }

        public async Task CancelOrderAsync(string orderId)
        {
            var path = $"/api/v1/orders/{orderId}";
            var req = CreateRequest(HttpMethod.Delete, path);
            var res = await _http.SendAsync(req);
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            EnsureSuccess(root);
        }

        public async Task CancelAllAsync(string symbol)
        {
            var open = await GetOpenOrdersAsync(symbol);
            foreach (var o in open)
                await CancelOrderAsync(o.OrderId);
        }

        public async Task<string> PlaceLimitAsync(string symbol, string side, decimal price, decimal size)
        {
            var path = "/api/v1/orders";
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
            var req = CreateRequest(HttpMethod.Post, path, body);
            var res = await _http.SendAsync(req);
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            EnsureSuccess(root);

            return root.GetProperty("data").GetProperty("orderId").GetString()!;
        }
    }

    //───────────────────────────────────────────────────────────────────────────────
    // Grid‐Trader logic
    //───────────────────────────────────────────────────────────────────────────────
    public class GridTrader
    {
        private static readonly HashSet<string> AllowedIntervals = new()
        {
            "1min","3min","5min","15min","30min","1hour","2hour",
            "4hour","6hour","12hour","1day","1week"
        };

        private readonly KucoinApi _api;
        private readonly string _sym;
        private readonly int _levels;
        private readonly decimal _qty;
        private readonly decimal _rangePct;
        private readonly int _maPeriod;
        private readonly int _bbPeriod;
        private readonly decimal _bbStdDev;
        private readonly decimal _stopLossPct;
        private readonly string _bbInterval;

        private decimal _tick, _initialPrice, _lower, _upper, _step;
        private int _qtyPrec;
        private Dictionary<decimal, string> _gridMap = new();

        public GridTrader(
            KucoinApi api,
            string symbol,
            int levels,
            decimal qty,
            decimal rangePct,
            int maPeriod,
            int bbPeriod,
            decimal bbStdDev,
            decimal stopLossPct,
            string bbInterval = "15min")
        {
            if (!AllowedIntervals.Contains(bbInterval))
                throw new ArgumentException(
                    $"BB interval must be one of {string.Join(", ", AllowedIntervals)}"
                );

            _api = api;
            _sym = symbol;
            _levels = levels;
            _qty = qty;
            _rangePct = rangePct;
            _maPeriod = maPeriod;
            _bbPeriod = bbPeriod;
            _bbStdDev = bbStdDev;
            _stopLossPct = stopLossPct;
            _bbInterval = bbInterval;
        }

        public async Task StartAsync()
        {
            await SetupGridAsync();
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm}] Trader started on {_sym} | " +
                $"Levels={_levels}, Qty={_qty}, Range={_rangePct:P}"
            );

            while (true)
            {
                try
                {
                    var t = await _api.FetchTickerAsync(_sym);
                    int needed = Math.Max(_maPeriod, _bbPeriod) + 1;
                    var closes = await _api.FetchKlinesAsync(_sym, _bbInterval, needed);

                    decimal sma = closes.Take(_maPeriod).Average();
                    var (mid, low, high) = ComputeBollinger(closes.Take(_bbPeriod), _bbStdDev);

                    Console.WriteLine(
                        $"[{DateTime.Now:HH:mm}] Price={t.Bid:F8}, " +
                        $"SMA={sma:F8}, BB=[{low:F8}|{mid:F8}|{high:F8}]"
                    );

                    if (t.Bid <= _initialPrice * (1 - _stopLossPct))
                    {
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm}] 🚨 Stop‐loss hit; cancelling all."
                        );
                        await _api.CancelAllAsync(_sym);
                        break;
                    }

                    await EvaluateGridAsync(t.Bid, t.Ask, low, high);
                    Thread.Sleep(10_000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR {DateTime.Now:HH:mm}] {ex.Message}");
                    Thread.Sleep(5_000);
                }
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm}] Trader stopped.");
        }

        private async Task SetupGridAsync()
        {
            await _api.CancelAllAsync(_sym);

            var spec = await _api.FetchInstrumentInfoAsync(_sym);
            _tick = spec.TickSize;
            _qtyPrec = spec.LotPrecision;
            var t = await _api.FetchTickerAsync(_sym);
            _initialPrice = (t.Bid + t.Ask) / 2m;

            _lower = _initialPrice * (1 - _rangePct);
            _upper = _initialPrice * (1 + _rangePct);
            _step = (_upper - _lower) / _levels;

            _gridMap.Clear();
            for (int i = 0; i <= _levels; i++)
            {
                var price = RoundPrice(_lower + _step * i);
                _gridMap[price] = price < _initialPrice ? "buy" : "sell";
            }
        }

        private async Task EvaluateGridAsync(
            decimal bid, decimal ask, decimal bbLow, decimal bbHigh)
        {
            var toRemove = new List<decimal>();
            var toAdd = new Dictionary<decimal, string>();

            foreach (var kv in _gridMap)
            {
                var price = kv.Key;
                var side = kv.Value;

                if (side == "buy" && bid <= price && bid >= bbLow)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm}] ▶ BUY @ {price}");
                    await _api.PlaceLimitAsync(_sym, "buy", price, RoundQty(_qty));
                    toRemove.Add(price);
                    toAdd[RoundPrice(price + _step)] = "sell";
                }
                else if (side == "sell" && ask >= price && ask <= bbHigh)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm}] ▶ SELL @ {price}");
                    await _api.PlaceLimitAsync(_sym, "sell", price, RoundQty(_qty));
                    toRemove.Add(price);
                    toAdd[RoundPrice(price - _step)] = "buy";
                }
            }

            foreach (var r in toRemove) _gridMap.Remove(r);
            foreach (var kv in toAdd) _gridMap[kv.Key] = kv.Value;
        }

        private static (decimal mid, decimal low, decimal high) ComputeBollinger(
            IEnumerable<decimal> vals, decimal mult)
        {
            var arr = vals.ToArray();
            var mean = arr.Average();
            var varm = arr.Select(v => (v - mean) * (v - mean)).Sum() / arr.Length;
            var std = (decimal)Math.Sqrt((double)varm);
            return (mean, mean - mult * std, mean + mult * std);
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
        public static async Task Main(string[] args)
        {
            string apiKey, apiSecret, passphrase, symbol;
            int levels, maPeriod, bbPeriod;
            decimal qty, rangePct, bbStdDev, stopLossPct;
            string bbInterval;

            if (args.Length >= 7)
            {
                apiKey = args[0];
                apiSecret = args[1];
                passphrase = args[2];
                symbol = args[3];
                if (!int.TryParse(args[4], out levels)) Fail("levels");
                if (!decimal.TryParse(args[5], NumberStyles.Any, CultureInfo.InvariantCulture, out qty)) Fail("qty");
                if (!decimal.TryParse(args[6], NumberStyles.Any, CultureInfo.InvariantCulture, out rangePct)) Fail("rangePct");

                maPeriod = args.Length > 7 && int.TryParse(args[7], out var m) ? m : 20;
                bbPeriod = args.Length > 8 && int.TryParse(args[8], out var b) ? b : 20;
                bbStdDev = args.Length > 9 && decimal.TryParse(args[9], NumberStyles.Any, CultureInfo.InvariantCulture, out var sd) ? sd : 2m;
                stopLossPct = args.Length > 10 && decimal.TryParse(args[10], NumberStyles.Any, CultureInfo.InvariantCulture, out var sl) ? sl : 0.10m;
                bbInterval = args.Length > 11 ? args[11] : "15min";
            }
            else
            {
                Console.WriteLine("=== KuCoin GridTrader Setup ===");
                apiKey = Prompt("API Key");
                apiSecret = Prompt("API Secret");
                passphrase = Prompt("API Passphrase");
                symbol = Prompt("Symbol (e.g. BTC-USDT)");
                levels = int.Parse(Prompt("Grid levels (e.g. 10)"));
                qty = decimal.Parse(Prompt("Size per order (e.g. 0.001)"), CultureInfo.InvariantCulture);
                rangePct = decimal.Parse(Prompt("Range percent (0.10 = ±10%)"), CultureInfo.InvariantCulture);
                maPeriod = int.Parse(Prompt("SMA period", "20"));
                bbPeriod = int.Parse(Prompt("BB period", "20"));
                bbStdDev = decimal.Parse(Prompt("BB stddev multiplier", "2"), CultureInfo.InvariantCulture);
                stopLossPct = decimal.Parse(Prompt("Stop‐loss %", "0.10"), CultureInfo.InvariantCulture);
                bbInterval = Prompt("BB interval (e.g. 15min)", "15min");
            }

            using var api = new KucoinApi(apiKey, apiSecret, passphrase);
            var trader = new GridTrader(
                api, symbol, levels, qty, rangePct,
                maPeriod, bbPeriod, bbStdDev, stopLossPct, bbInterval
            );

            Console.WriteLine("\nPress any key to start...");
            Console.ReadKey(true);
            Console.WriteLine();

            await trader.StartAsync();
        }

        private static void Fail(string name)
        {
            Console.WriteLine($"Invalid or missing parameter: {name}");
            Environment.Exit(1);
        }

        private static string Prompt(string label, string? def = null)
        {
            if (def == null) Console.Write($"{label}: ");
            else Console.Write($"{label} [{def}]: ");

            var inp = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(inp))
            {
                if (def != null) return def;
                Console.WriteLine("Cannot be empty.");
                return Prompt(label, def);
            }

            return inp.Trim();
        }
    }
}
