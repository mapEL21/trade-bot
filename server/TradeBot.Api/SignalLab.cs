using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// One detected event and the best prices after it: at entry, 250 ms after the event, and at each horizon. A long buys at the
// ask and sells at the bid, a short the other way round, so both trading with the event and against it pay the spread.
// A horizon is null when the book was stale then or the recording ended first.
public sealed record LabEvent(string Symbol, string Type, int Direction, long AtMs, decimal Price, string Detail, decimal? EntryBid = null,
    decimal? EntryAsk = null, decimal?[]? ExitBid = null, decimal?[]? ExitAsk = null)
{
    // Percent result of a long or a short through horizon i, before fees; null when unknown.
    public decimal? Move(int i, bool withEvent)
    {
        if (EntryBid is not { } bid || EntryAsk is not { } ask || ExitBid?[i] is not { } exitBid || ExitAsk?[i] is not { } exitAsk) return null;
        var longSide = (Direction > 0) == withEvent;
        return longSide ? (exitBid / ask - 1) * 100 : (bid / exitAsk - 1) * 100;
    }
}
public sealed record LabRecording(int Version, string Id, long FirstMs, long LastMs, int Symbols, LabEvent[] Events);

// Finds events in a scan recording using only data up to each moment, then measures what price did next. The thresholds are
// fixed (version 1): tuning them on the same data would find patterns that are not there.
public sealed class SignalDetector
{
    public const int Version = 1;
    public static readonly int[] Horizons = [5, 15, 60, 300];
    public static readonly string[] Types = ["Всплеск объёма", "Перекос агрессора", "Каскад ликвидаций", "Вынос цены", "Перекос стакана", "Пробой уровня"];
    private const long Window = 10_000, Baseline = 15 * 60_000, Cooldown = 60_000, Latency = 250, BookAge = 2_000;

    private sealed class Pending(LabEvent e)
    {
        public LabEvent Event = e; public decimal? Ask, Bid;
        public decimal?[] ExitBid = new decimal?[Horizons.Length], ExitAsk = new decimal?[Horizons.Length];
        public bool[] Done = new bool[Horizons.Length];
    }
    private sealed class State
    {
        // Rolling windows kept as running sums: the last 10 s, the 15 min before them, and prices of the last 30 s.
        public readonly Queue<(long Ms, decimal Price, decimal Quote, bool Buy)> Recent = new(), Older = new();
        public readonly Queue<(long Ms, decimal Price)> Prices = new();
        public decimal RecentQuote, RecentBuy, OlderQuote, LastPrice;
        public decimal? BeforeWindow, Before30;
        public readonly Queue<(long Ms, bool Buy)> Liquidations = new();
        public readonly List<(long Minute, decimal High, decimal Low)> Minutes = [];
        public decimal Bid, Ask; public long BookMs = long.MinValue; public bool BookHeavy;
        public long FirstTradeMs = long.MaxValue;
        public readonly Dictionary<string, long> Last = [];
        public readonly List<Pending> Pending = [];
    }

    private readonly Dictionary<string, State> states;
    private readonly LevelScanner levels;
    private readonly List<Pending> all = [];
    private long firstMs = long.MaxValue, lastMs;

    public SignalDetector(IReadOnlyDictionary<string, decimal> quoteVolumes, bool liquidations)
    {
        states = quoteVolumes.Keys.ToDictionary(x => x, _ => new State(), StringComparer.Ordinal);
        levels = new LevelScanner(new ScanSettings(), quoteVolumes, liquidations);
    }

    public void Process(CaptureEvent item)
    {
        var known = levels.Candidates.Count;
        levels.Process(item);
        if (item.Kind == "history") { History(item.Payload); return; }
        if (item.Kind != "market") return;
        using var doc = JsonDocument.Parse(item.Payload);
        var data = doc.RootElement.GetProperty("data");
        var symbol = (data.TryGetProperty("s", out var s) ? s : data.GetProperty("o").GetProperty("s")).GetString()!;
        if (!states.TryGetValue(symbol, out var state)) return;
        long ms;
        switch (data.GetProperty("e").GetString())
        {
            case "aggTrade":
                ms = data.GetProperty("T").GetInt64();
                Trade(symbol, state, ms, Dec(data, "p"), Dec(data, "q"), !data.GetProperty("m").GetBoolean());
                break;
            case "depthUpdate":
                ms = data.GetProperty("E").GetInt64();
                Book(symbol, state, ms, data.GetProperty("b"), data.GetProperty("a"));
                break;
            case "forceOrder":
                var order = data.GetProperty("o");
                ms = order.GetProperty("T").GetInt64();
                Liquidation(symbol, state, ms, order.GetProperty("S").GetString() == "BUY");
                break;
            default: return;
        }
        Advance(state, ms);
        // A breakout the level scanner found on this very event is measured the same way as every other event.
        foreach (var c in levels.Candidates.Skip(known))
            Emit(states[c.Symbol], new(c.Symbol, "Пробой уровня", c.Side == "long" ? 1 : -1, c.AtMs, c.Price, $"Уровень {c.Level}, оценка {c.Score}"), cooldown: false);
    }

    private void History(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!states.TryGetValue(doc.RootElement.GetProperty("symbol").GetString()!, out var state)) return;
        foreach (var k in doc.RootElement.GetProperty("klines").EnumerateArray().SkipLast(1))
            state.Minutes.Add((k[0].GetInt64(), Dec(k[2]), Dec(k[3])));
    }

    private void Trade(string symbol, State state, long ms, decimal price, decimal quantity, bool buy)
    {
        state.FirstTradeMs = Math.Min(state.FirstTradeMs, ms);
        var minute = ms / 60_000 * 60_000;
        if (state.Minutes.Count > 0 && state.Minutes[^1].Minute == minute)
            state.Minutes[^1] = (minute, Math.Max(state.Minutes[^1].High, price), Math.Min(state.Minutes[^1].Low, price));
        else if (state.Minutes.Count == 0 || state.Minutes[^1].Minute < minute) state.Minutes.Add((minute, price, price));
        if (state.Minutes.Count > 120) state.Minutes.RemoveRange(0, state.Minutes.Count - 120);
        var quote = price * quantity;
        state.Recent.Enqueue((ms, price, quote, buy)); state.RecentQuote += quote; if (buy) state.RecentBuy += quote;
        while (state.Recent.TryPeek(out var r) && r.Ms <= ms - Window)
        {
            state.Recent.Dequeue(); state.RecentQuote -= r.Quote; if (r.Buy) state.RecentBuy -= r.Quote;
            state.Older.Enqueue(r); state.OlderQuote += r.Quote; state.BeforeWindow = r.Price;
        }
        while (state.Older.TryPeek(out var o) && o.Ms < ms - Baseline) { state.Older.Dequeue(); state.OlderQuote -= o.Quote; }
        state.Prices.Enqueue((ms, price));
        while (state.Prices.TryPeek(out var q) && q.Ms <= ms - 30_000) { state.Prices.Dequeue(); state.Before30 = q.Price; }
        state.LastPrice = price;
        // Signals need five minutes of live trades to know what is normal for the coin.
        if (ms - state.FirstTradeMs < 5 * 60_000) return;

        var olderSeconds = Math.Max(1, (ms - Window - Math.Max(state.FirstTradeMs, ms - Baseline)) / 1000m);
        var normal = state.OlderQuote / olderSeconds * (Window / 1000m);
        if (normal <= 0) return;
        var ratio = state.RecentQuote / normal;
        if (ratio >= 5 && state.BeforeWindow is { } p && price != p)
            Emit(state, new(symbol, "Всплеск объёма", Math.Sign(price - p), ms, price, $"Объём за 10 с в {ratio:0.#}× выше среднего"));
        var share = state.RecentQuote > 0 ? state.RecentBuy / state.RecentQuote : .5m;
        if (ratio >= 2 && (share >= .8m || share <= .2m))
            Emit(state, new(symbol, "Перекос агрессора", share >= .8m ? 1 : -1, ms, price, $"{share * 100:0}% покупок агрессором при объёме {ratio:0.#}×"));
        var before30 = state.Before30;
        var ranges = state.Minutes.TakeLast(61).SkipLast(1).Select(m => (m.High - m.Low) / m.Low).ToList();
        if (ranges.Count >= 30 && before30 is { } p30)
        {
            var move = (price / p30 - 1);
            var typical = ranges.Average();
            if (typical > 0 && Math.Abs(move) >= 3 * typical)
                Emit(state, new(symbol, "Вынос цены", Math.Sign(move), ms, price, $"Ход за 30 с {move * 100:0.##}% — {Math.Abs(move) / typical:0.#}× обычной минутной свечи"));
        }
    }

    private void Liquidation(string symbol, State state, long ms, bool buy)
    {
        state.Liquidations.Enqueue((ms, buy));
        while (state.Liquidations.TryPeek(out var old) && old.Ms < ms - Window) state.Liquidations.Dequeue();
        var same = state.Liquidations.Count(l => l.Buy == buy);
        var price = state.LastPrice;
        if (same >= 3 && price > 0)
            Emit(state, new(symbol, "Каскад ликвидаций", buy ? 1 : -1, ms, price, $"{same} ликвидации {(buy ? "шортов" : "лонгов")} за 10 с"));
    }

    private void Book(string symbol, State state, long ms, JsonElement bids, JsonElement asks)
    {
        if (bids.GetArrayLength() == 0 || asks.GetArrayLength() == 0) return;
        state.Bid = Dec(bids[0][0]); state.Ask = Dec(asks[0][0]); state.BookMs = ms;
        var mid = (state.Bid + state.Ask) / 2;
        decimal Near(JsonElement side) => side.EnumerateArray().Select(l => (Price: Dec(l[0]), Size: Dec(l[1])))
            .Where(l => Math.Abs(l.Price / mid - 1) <= .003m).Sum(l => l.Price * l.Size);
        var b = Near(bids); var a = Near(asks);
        var imbalance = b + a > 0 ? (b - a) / (b + a) : 0;
        var heavy = Math.Abs(imbalance) >= .6m;
        // Only the moment the book tips over counts, not every snapshot while it stays one-sided.
        if (heavy && !state.BookHeavy && ms - state.FirstTradeMs >= 5 * 60_000)
            Emit(state, new(symbol, "Перекос стакана", Math.Sign(imbalance), ms, mid, $"Заявок {(imbalance > 0 ? "на покупку" : "на продажу")} на {Math.Abs(imbalance) * 100:0}% больше в пределах 0,3%"));
        state.BookHeavy = heavy;
    }

    private void Emit(State state, LabEvent e, bool cooldown = true)
    {
        if (cooldown && state.Last.TryGetValue(e.Type, out var last) && e.AtMs - last < Cooldown) return;
        state.Last[e.Type] = e.AtMs;
        var pending = new Pending(e);
        state.Pending.Add(pending); all.Add(pending);
        firstMs = Math.Min(firstMs, e.AtMs);
    }

    // Entry and exits are read from the book as the coin's own clock passes them; a stale book leaves the value unknown.
    private void Advance(State state, long ms)
    {
        lastMs = Math.Max(lastMs, ms);
        foreach (var p in state.Pending.ToArray())
        {
            var at = p.Event.AtMs;
            var fresh = ms - state.BookMs <= BookAge;
            if (p.Ask is null && ms >= at + Latency)
            {
                if (!fresh) { state.Pending.Remove(p); continue; }
                p.Ask = state.Ask; p.Bid = state.Bid;
            }
            if (p.Ask is null) continue;
            for (var i = 0; i < Horizons.Length; i++)
            {
                if (p.Done[i] || ms < at + Latency + Horizons[i] * 1000L) continue;
                // Decided once, at the first update past the horizon: a stale book leaves this exit unknown for good.
                p.Done[i] = true;
                if (!fresh) continue;
                p.ExitBid[i] = state.Bid; p.ExitAsk[i] = state.Ask;
            }
            if (ms >= at + Latency + Horizons[^1] * 1000L) state.Pending.Remove(p);
        }
    }

    public LabRecording Result(string id) => new(Version, id, firstMs == long.MaxValue ? 0 : firstMs, lastMs, states.Count,
        all.Select(p => p.Event with { EntryAsk = p.Ask, EntryBid = p.Bid, ExitBid = p.ExitBid, ExitAsk = p.ExitAsk }).ToArray());

    private static decimal Dec(JsonElement e, string name) => Dec(e.GetProperty(name));
    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDecimal()
        : decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    // Runs a whole finished scan recording, plain or compressed.
    public static LabRecording Run(string directory, CancellationToken ct, Action<long>? progress = null)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var volumes = manifest.RootElement.GetProperty("symbols").EnumerateArray()
            .ToDictionary(x => x.GetProperty("Symbol").GetString()!, x => x.GetProperty("QuoteVolume").GetDecimal(), StringComparer.Ordinal);
        var liquidations = manifest.RootElement.TryGetProperty("liquidations", out var flag) && flag.GetBoolean();
        var detector = new SignalDetector(volumes, liquidations);
        var plain = Path.Combine(directory, StorageHousekeeper.Events);
        using var stream = File.Exists(plain) ? File.OpenRead(plain)
            : (Stream)new GZipStream(File.OpenRead(Path.Combine(directory, StorageHousekeeper.Compressed)), CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        long lines = 0;
        while (reader.ReadLine() is { } line)
        {
            if (++lines % 50_000 == 0) { ct.ThrowIfCancellationRequested(); progress?.Invoke(lines); }
            CaptureEvent? item;
            try { item = JsonSerializer.Deserialize<CaptureEvent>(line); }
            catch (JsonException) { continue; }
            if (item is null) continue;
            try { detector.Process(item); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException) { }
        }
        progress?.Invoke(lines);
        return detector.Result(Path.GetFileName(directory));
    }
}
