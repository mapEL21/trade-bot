using System.Globalization;
using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// A coin the screener looks at: its price step (tickSize from the exchange rules), 24-hour volume, last price and 24-hour
// range (high - low) / last price, %.
public sealed record ScreenerMarket(string Symbol, decimal TickSize, decimal QuoteVolume, decimal LastPrice, decimal RangePct = 100);
// One order book snapshot: price and quantity per level, best first.
public sealed record DepthBook((decimal Price, decimal Quantity)[] Bids, (decimal Price, decimal Quantity)[] Asks);

public interface IDepthSource
{
    Task<ScreenerMarket[]> MarketsAsync(CancellationToken ct);
    Task<DepthBook> DepthAsync(string symbol, CancellationToken ct);
    // The last hour of 1-minute candles of a coin as a history event payload ({"symbol", "interval", "klines"}), the form
    // the data recording keeps: a live bot needs the coin's typical candle from its first minute.
    Task<string> KlinesAsync(string symbol, CancellationToken ct);
    // Request weight used in the current minute as the exchange reports it; the screener slows down near the limit.
    int UsedWeight { get; }
}

// A wall seen by the screener. Side: +1 bid (below the price), -1 ask (above). Ratio: size ÷ the median level near the price.
// Minutes: how many minutes of the coin's average trading (24-hour volume ÷ 1440) it would take to eat the wall.
// Ticks: how many price steps from the best price on its side to the wall (0 — the wall is the best price). Status: stands,
// near (within 0.3% of the price), eating (lost 30% of its largest size), and for finished walls eaten (the price went
// through) or pulled (it vanished with the price on this side).
public sealed record ScreenerWall(string Symbol, int Side, decimal Price, decimal Quote, decimal Peak, decimal Ratio, decimal DistancePct,
    long Ticks, decimal TickSize, long FirstMs, long SeenMs, string Status, decimal Minutes = 0);
public sealed record ScreenerDay(int Found, int Eaten, int Pulled, decimal? MedianMinutes);
public sealed record ScreenerSnapshot(string State, string Explanation, int Markets, int Rounds, long? RoundMs, double? RoundSeconds,
    ScreenerWall[] Walls, ScreenerWall[] Finished, ScreenerDay Day);

// The density screener: goes round the order books of every Binance USDT perpetual (500 levels each), finds walls — levels
// many times larger than the levels around them — and follows what happens to them: they stand, the price comes to them,
// they are eaten or pulled. It keeps only the walls (in data/screener, one file a day), not the books. Bots and the lab read
// it; it never trades. Thresholds are fixed here, see docs/DENSITY_SCREENER.md.
public sealed class DensityScreener(IDepthSource source, IConfiguration configuration, IHostEnvironment environment, ILogger<DensityScreener> logger)
    : IHostedService, IDisposable
{
    // A wall is the largest level of its side within 3% of the price, at least 3 times the next one, 10 times the median
    // level there and 10 minutes of the coin's average trading, on a coin that moves at least 2% a day; it counts once it
    // has stood two rounds in a row. The first live run with looser rules (ratio and 3 minutes only) found over 2 000
    // "walls" on 300 coins, mostly market-maker ladders, stablecoins and gold, and hundreds of them flickered each round.
    public const decimal MinRatio = 10, MinVolumeMinutes = 10, MinOverNext = 3, MinQuote = 10_000, RangePct = 3, MinCoinRangePct = 2,
        NearPct = .3m, EatingShare = .7m;
    // A request of 500 levels weighs 10; at most about half of Binance's 2400 a minute, slower when the exchange reports more.
    public static TimeSpan Pause = TimeSpan.FromMilliseconds(500);
    public const int WeightLimit = 1600;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Dictionary<string, List<ScreenerWall>> walls = new(StringComparer.Ordinal);
    // Seen once, waiting for the next round to be confirmed.
    private readonly Dictionary<string, List<ScreenerWall>> candidates = new(StringComparer.Ordinal);
    private readonly List<ScreenerWall> finished = [];
    private readonly List<long> found = [];
    private CancellationTokenSource? stop;
    private Task? task;
    private string state = "idle", explanation = "Скринер не запущен.";
    private int markets, rounds;
    private Dictionary<string, ScreenerMarket> known = new(StringComparer.Ordinal);
    private long? roundMs, lastMs;
    private double? roundSeconds;

    private string Root => configuration["Screener:Path"] ?? Path.Combine(environment.ContentRootPath, "data", "screener");

    public ScreenerSnapshot Snapshot()
    {
        lock (gate)
        {
            // The day before the latest snapshot (the screener's own clock, so a replayed day counts the same way).
            var dayAgo = (lastMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - 86_400_000;
            var day = finished.Where(w => w.SeenMs >= dayAgo).ToList();
            var lives = day.Select(w => (w.SeenMs - w.FirstMs) / 60_000m).Order().ToList();
            return new(state, explanation, markets, rounds, roundMs, roundSeconds,
                walls.Values.SelectMany(x => x).OrderBy(w => w.DistancePct).ToArray(),
                finished.OrderByDescending(w => w.SeenMs).Take(50).ToArray(),
                new(found.Count(ms => ms >= dayAgo), day.Count(w => w.Status == "eaten"), day.Count(w => w.Status == "pulled"),
                    lives.Count == 0 ? null : Math.Round(lives[lives.Count / 2], 1)));
        }
    }

    // A coin's volume and price step from the latest list of markets.
    public ScreenerMarket? Market(string symbol) { lock (gate) return known.GetValueOrDefault(symbol); }

    public bool Start()
    {
        lock (gate)
        {
            if (task is { IsCompleted: false }) return false;
            stop = new CancellationTokenSource();
            state = "starting"; explanation = "Загружаем список фьючерсов Binance.";
            var token = stop.Token;
            task = Task.Run(() => Loop(token));
            return true;
        }
    }

    public bool Stop()
    {
        lock (gate)
        {
            if (task is not { IsCompleted: false }) return false;
            stop!.Cancel();
            return true;
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        Load();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var list = await source.MarketsAsync(ct);
                lock (gate)
                {
                    markets = list.Length; state = "running"; explanation = $"Обходим стаканы {list.Length} фьючерсов.";
                    known = list.GroupBy(m => m.Symbol).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                }
                var started = DateTimeOffset.UtcNow;
                foreach (var market in list.OrderByDescending(m => m.QuoteVolume))
                {
                    ct.ThrowIfCancellationRequested();
                    try { Scan(market, await source.DepthAsync(market.Symbol, ct), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
                    catch (Exception e) when (e is HttpRequestException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
                    {
                        logger.LogDebug(e, "Screener skipped {Symbol}", market.Symbol);
                    }
                    await Task.Delay(source.UsedWeight > WeightLimit ? TimeSpan.FromSeconds(10) : Pause, ct);
                }
                lock (gate) { rounds++; roundMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); roundSeconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is HttpRequestException or ArgumentException or JsonException or TaskCanceledException)
            {
                logger.LogWarning(e, "Screener round failed");
                lock (gate) { state = "failed"; explanation = e is ArgumentException ? e.Message : "Binance недоступен. Повторим через минуту."; }
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
            }
        }
        lock (gate) { state = "stopped"; explanation = "Скринер остановлен."; }
    }

    // Walls of one book (see the thresholds above): at most one a side.
    public static List<ScreenerWall> Find(ScreenerMarket market, DepthBook book, long ms)
    {
        var result = new List<ScreenerWall>();
        if (book.Bids.Length == 0 || book.Asks.Length == 0 || market.RangePct < MinCoinRangePct) return result;
        var bestBid = book.Bids[0].Price; var bestAsk = book.Asks[0].Price;
        var mid = (bestBid + bestAsk) / 2;
        if (mid <= 0) return result;
        var levels = book.Bids.Select(l => (Side: 1, l.Price, Quote: l.Price * l.Quantity))
            .Concat(book.Asks.Select(l => (Side: -1, l.Price, Quote: l.Price * l.Quantity)))
            .Where(l => Math.Abs(l.Price / mid - 1) * 100 <= RangePct).ToList();
        if (levels.Count < 10) return result;
        var sorted = levels.Select(l => l.Quote).Order().ToList();
        var median = sorted[sorted.Count / 2];
        var perMinute = market.QuoteVolume / 1440;
        var minimum = Math.Max(MinQuote, MinVolumeMinutes * perMinute);
        foreach (var side in new[] { 1, -1 })
        {
            var own = levels.Where(l => l.Side == side).OrderByDescending(l => l.Quote).Take(2).ToList();
            if (own.Count == 0) continue;
            var l = own[0];
            var next = own.Count > 1 ? own[1].Quote : 0;
            if (median <= 0 || l.Quote < MinRatio * median || l.Quote < minimum || l.Quote < MinOverNext * next) continue;
            var best = side == 1 ? bestBid : bestAsk;
            var ticks = market.TickSize > 0 ? (long)Math.Round(Math.Abs(best - l.Price) / market.TickSize) : 0;
            var distance = Math.Round(Math.Abs(l.Price / mid - 1) * 100, 3);
            result.Add(new(market.Symbol, side, l.Price, Math.Round(l.Quote), Math.Round(l.Quote), Math.Round(l.Quote / median, 1), distance,
                ticks, market.TickSize, ms, ms, distance <= NearPct ? "near" : "stands", perMinute > 0 ? Math.Round(l.Quote / perMinute, 1) : 0));
        }
        return result;
    }

    // The size a level must keep to stay a wall once found: 10 minutes of the coin's average trading and 10 000 USDT.
    private static decimal Minimum(ScreenerMarket market) => Math.Max(MinQuote, MinVolumeMinutes * market.QuoteVolume / 1440);

    // A new snapshot of one coin against the walls seen before. A wall already followed stays a wall while its level keeps
    // the minimum size (a bigger level appearing elsewhere does not end it); a new one counts after two rounds in a row. A
    // wall that is gone is finished: eaten if the price is now beyond it, pulled if not. Trades happen only at the best
    // price, so a wall away from it that shrank was cancelled; one at the best price that shrank may have been eaten in
    // part and is not counted. A wall farther than 3% from the price is no longer followed.
    public void Scan(ScreenerMarket market, DepthBook book, long ms)
    {
        var tops = Find(market, book, ms);
        var bestBid = book.Bids.Length > 0 ? book.Bids[0].Price : 0; var bestAsk = book.Asks.Length > 0 ? book.Asks[0].Price : 0;
        var mid = (bestBid + bestAsk) / 2;
        var sizes = new Dictionary<(int, decimal), decimal>();
        foreach (var l in book.Bids) sizes[(1, l.Price)] = l.Price * l.Quantity;
        foreach (var l in book.Asks) sizes[(-1, l.Price)] = l.Price * l.Quantity;
        var minimum = Minimum(market);
        var perMinute = market.QuoteVolume / 1440;
        lock (gate)
        {
            known[market.Symbol] = market;
            var before = walls.GetValueOrDefault(market.Symbol) ?? [];
            var waiting = candidates.GetValueOrDefault(market.Symbol) ?? [];
            var next = new List<ScreenerWall>();
            foreach (var old in before)
            {
                var distance = mid > 0 ? Math.Round(Math.Abs(old.Price / mid - 1) * 100, 3) : 0;
                if (sizes.TryGetValue((old.Side, old.Price), out var quote) && quote >= minimum && distance <= RangePct)
                {
                    var best = old.Side == 1 ? bestBid : bestAsk;
                    var peak = Math.Max(old.Peak, Math.Round(quote));
                    next.Add(old with
                    {
                        Quote = Math.Round(quote), Peak = peak, DistancePct = distance, SeenMs = ms,
                        Ticks = market.TickSize > 0 ? (long)Math.Round(Math.Abs(best - old.Price) / market.TickSize) : 0,
                        Minutes = perMinute > 0 ? Math.Round(quote / perMinute, 1) : 0,
                        Status = quote < EatingShare * peak ? "eating" : distance <= NearPct ? "near" : "stands",
                    });
                    continue;
                }
                var through = old.Side == 1 ? bestBid < old.Price : bestAsk > old.Price;
                if (!through && (distance > RangePct || old.Ticks == 0 && sizes.ContainsKey((old.Side, old.Price)))) continue;
                var done = old with { SeenMs = ms, Status = through ? "eaten" : "pulled" };
                finished.Add(done); Append(done);
            }
            var fresh = new List<ScreenerWall>();
            foreach (var w in tops.Where(w => !next.Any(n => n.Side == w.Side && n.Price == w.Price)))
            {
                if (waiting.FirstOrDefault(b => b.Side == w.Side && b.Price == w.Price) is { } seen)
                {
                    // Stood two rounds in a row: a wall from its first sighting.
                    var confirmed = w with { FirstMs = seen.FirstMs, Peak = Math.Max(seen.Peak, w.Quote) };
                    next.Add(confirmed); found.Add(ms); Append(confirmed with { Status = "found" });
                }
                else fresh.Add(w);
            }
            candidates[market.Symbol] = fresh;
            walls[market.Symbol] = next;
            lastMs = Math.Max(lastMs ?? ms, ms);
            finished.RemoveAll(w => w.SeenMs < ms - 86_400_000);
            found.RemoveAll(x => x < ms - 86_400_000);
        }
    }

    private void Append(ScreenerWall wall)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var day = DateTimeOffset.FromUnixTimeMilliseconds(wall.SeenMs).UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            File.AppendAllText(Path.Combine(Root, $"walls-{day}.ndjson"), JsonSerializer.Serialize(wall, Json) + "\n");
        }
        catch (IOException e) { logger.LogWarning(e, "Screener journal not written"); }
    }

    // The last day of finished walls and found counts survive a restart; walls standing now are found again in the first round.
    private void Load()
    {
        var since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 86_400_000;
        lock (gate) { found.Clear(); finished.Clear(); walls.Clear(); candidates.Clear(); }
        if (!Directory.Exists(Root)) return;
        foreach (var file in Directory.EnumerateFiles(Root, "walls-*.ndjson").Order().TakeLast(2))
            foreach (var line in File.ReadLines(file))
            {
                ScreenerWall? w;
                try { w = JsonSerializer.Deserialize<ScreenerWall>(line, Json); } catch (JsonException) { continue; }
                if (w is null || w.SeenMs < since) continue;
                lock (gate)
                {
                    if (w.Status == "found") found.Add(w.SeenMs);
                    else if (w.Status is "eaten" or "pulled") finished.Add(w);
                }
            }
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (configuration.GetValue<bool>("Screener:AutoStart")) Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task? running;
        lock (gate) { Cancellation.Cancel(stop); running = task; }
        if (running is not null) try { await running.WaitAsync(ct); } catch (OperationCanceledException) { }
    }

    public void Dispose() { lock (gate) stop?.Dispose(); }
}

// Binance public REST: the list of USDT perpetuals with their price step and volume, and order books of 500 levels.
public sealed class BinanceDepthSource : IDepthSource, IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private int usedWeight;
    public int UsedWeight => usedWeight;

    private async Task<string> Get(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.Headers.TryGetValues("X-MBX-USED-WEIGHT-1M", out var values) && int.TryParse(values.FirstOrDefault(), out var w))
            usedWeight = w;
        if (!response.IsSuccessStatusCode) throw new ArgumentException(ScanRecorder.Explain(response.StatusCode));
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<ScreenerMarket[]> MarketsAsync(CancellationToken ct)
    {
        var info = await Get("https://fapi.binance.com/fapi/v1/exchangeInfo", ct);
        var tickers = await Get("https://fapi.binance.com/fapi/v1/ticker/24hr", ct);
        return Markets(info, tickers);
    }

    public static ScreenerMarket[] Markets(string exchangeInfo, string tickers)
    {
        using var info = JsonDocument.Parse(exchangeInfo);
        var ticks = info.RootElement.GetProperty("symbols").EnumerateArray()
            .Where(s => s.GetProperty("quoteAsset").GetString() == "USDT" && s.GetProperty("contractType").GetString() == "PERPETUAL"
                && s.GetProperty("status").GetString() == "TRADING")
            .ToDictionary(s => s.GetProperty("symbol").GetString()!, s => s.GetProperty("filters").EnumerateArray()
                .Where(f => f.GetProperty("filterType").GetString() == "PRICE_FILTER").Select(f => Dec(f.GetProperty("tickSize"))).FirstOrDefault(),
                StringComparer.Ordinal);
        using var t = JsonDocument.Parse(tickers);
        return t.RootElement.EnumerateArray()
            .Select(x => (Symbol: x.GetProperty("symbol").GetString()!, Volume: Dec(x.GetProperty("quoteVolume")), Last: Dec(x.GetProperty("lastPrice")),
                Range: x.TryGetProperty("highPrice", out var h) && x.TryGetProperty("lowPrice", out var l) ? Dec(h) - Dec(l) : 0))
            .Where(x => ticks.ContainsKey(x.Symbol) && x.Last > 0)
            .Select(x => new ScreenerMarket(x.Symbol, ticks[x.Symbol], x.Volume, x.Last, Math.Round(x.Range / x.Last * 100, 2))).ToArray();
    }

    public async Task<string> KlinesAsync(string symbol, CancellationToken ct)
    {
        var klines = await Get($"https://fapi.binance.com/fapi/v1/klines?symbol={Uri.EscapeDataString(symbol)}&interval=1m&limit=62", ct);
        return JsonSerializer.Serialize(new { symbol, interval = "1m", klines = JsonSerializer.Deserialize<JsonElement>(klines) });
    }

    public async Task<DepthBook> DepthAsync(string symbol, CancellationToken ct) =>
        Book(await Get($"https://fapi.binance.com/fapi/v1/depth?symbol={Uri.EscapeDataString(symbol)}&limit=500", ct));

    public static DepthBook Book(string json)
    {
        using var doc = JsonDocument.Parse(json);
        (decimal, decimal)[] Side(string name) => doc.RootElement.GetProperty(name).EnumerateArray().Select(l => (Dec(l[0]), Dec(l[1]))).ToArray();
        return new(Side("bids"), Side("asks"));
    }

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDecimal()
        : decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    public void Dispose() => http.Dispose();
}
