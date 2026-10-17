using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// One detected event and the best prices after it: at entry, 250 ms after the event, and at each horizon. A long buys at the
// ask and sells at the bid, a short the other way round, so both trading with the event and against it pay the spread.
// A horizon is null when the book was stale then or the recording ended first. TypicalPct: the coin's average 1-minute range
// (high - low) / low over the hour before the event, %, null with less than 30 minutes known. Origin: where a price spike
// started (the price 30 s before it); the bot cancels its thesis when the price is back at the middle of the spike.
public sealed record LabEvent(string Symbol, string Type, int Direction, long AtMs, decimal Price, string Detail, decimal? EntryBid = null,
    decimal? EntryAsk = null, decimal?[]? ExitBid = null, decimal?[]? ExitAsk = null, decimal? TypicalPct = null, decimal? Origin = null)
{
    // Percent result of a long or a short through horizon i, before fees; null when unknown.
    public decimal? Move(int i, bool withEvent)
    {
        if (EntryBid is not { } bid || EntryAsk is not { } ask || ExitBid?[i] is not { } exitBid || ExitAsk?[i] is not { } exitAsk) return null;
        // A price of zero means no book was known; it is never a real price.
        if (bid <= 0 || ask <= 0 || exitBid <= 0 || exitAsk <= 0) return null;
        var longSide = (Direction > 0) == withEvent;
        return longSide ? (exitBid / ask - 1) * 100 : (bid / exitAsk - 1) * 100;
    }
}
// Spreads: per coin, how wide the best bid/ask spread was over the recording's book snapshots, for the scalping check.
public sealed record LabRecording(int Version, string Id, long FirstMs, long LastMs, int Symbols, LabEvent[] Events, LabSpread[]? Spreads = null);
// Snapshots counted, mean spread in bp, the share of snapshots with a spread of at least 4 bp (two maker fees of 0.02%) and 10 bp.
public sealed record LabSpread(string Symbol, long Snapshots, decimal SumBps, long Above4, long Above10);

// What the bot sees of one coin at a moment: best prices, the coin's typical minute range and its walls. Remaining and EatRate
// count the aggressor trades at the wall's price since the last book snapshot (the book comes every 500 ms, trades in real
// time), so the bot sees a wall being eaten between snapshots. Departures: how far the price went from this wall before
// coming back to it (or running away), %; CoinDepartures: the same for every wall of the coin over the last hour.
// Ticks: price steps from the best price on the wall's side to the wall; Front: the quote resting before it on that side.
public sealed record WallView(int Side, decimal Price, decimal Quote, decimal Peak, long FirstMs, decimal Remaining, decimal EatRate,
    int Level, int Touches, decimal[] Departures, long Ticks = 0, decimal Front = 0);
// LastTrade: the latest trade price and its time; a trade after the last book snapshot shows the price moved since.
public sealed record CoinView(string Symbol, long BookMs, decimal Bid, decimal Ask, decimal? TypicalPct, decimal MedianLevel, decimal Volume,
    WallView[] Walls, decimal[] CoinDepartures, decimal LastTrade = 0, long LastTradeMs = 0);

// Finds events in a scan recording using only data up to each moment, then measures what price did next. The thresholds are
// fixed (version 1): tuning them on the same data would find patterns that are not there.
public sealed class SignalDetector
{
    // 2: an event before the coin's first book snapshot no longer gets a zero entry price.
    // 3: the coin's typical minute range is kept with each event (a price path per event was kept too, until the bot moved to
    // the raw stream); results are the same.
    // 4: the recording's span starts at its first market data, not its first event; a recording without events got 0
    // and the report counted hundreds of thousands of hours.
    // 5: order book walls (approach, eaten, pulled, moved) and spread statistics.
    public const int Version = 5;
    public static readonly int[] Horizons = [5, 15, 60, 300];
    public static readonly string[] Types = ["Всплеск объёма", "Перекос агрессора", "Каскад ликвидаций", "Вынос цены", "Перекос стакана", "Пробой уровня",
        "Подход к плотности", "Проедание плотности", "Снятие плотности", "Переставление плотности"];
    // A wall: a level of the visible book worth at least 5 median levels and 0.02% of the coin's 24-hour volume. It counts once
    // it has stood for 30 s; it stays a wall while the level keeps at least half that size.
    private const decimal WallLevels = 5, WallVolumeShare = .0002m, WallKeep = .5m, WallNear = .001m, WallPulledNear = .0015m;
    private const long WallAge = 30_000;
    private const long Window = 10_000, Baseline = 15 * 60_000, Cooldown = 60_000, Latency = 250, BookAge = 2_000;

    private sealed class Pending(LabEvent e)
    {
        public LabEvent Event = e; public decimal? Ask, Bid;
        public decimal?[] ExitBid = new decimal?[Horizons.Length], ExitAsk = new decimal?[Horizons.Length];
        public bool[] Done = new bool[Horizons.Length];
    }
    private sealed class Wall(int side, decimal price, decimal quote, long firstMs)
    {
        // Side: +1 a bid wall below the price, -1 an ask wall above it.
        public readonly int Side = side; public decimal Price = price, Quote = quote; public long FirstMs = firstMs; public bool Approached;
        // Largest size seen; quote eaten at its price since the last snapshot and over the last 3 s.
        public decimal Peak = quote, Eaten;
        public readonly Queue<(long Ms, decimal Quote)> Eats = new();
        // Bounce statistics: the price touches the wall (within 0.1%), goes away and comes back; MaxAway is how far it went.
        public bool Touching, Touched; public decimal MaxAway; public int Touches;
        public readonly List<decimal> Departures = [];
        public int Level;
        public long Ticks; public decimal Front;
    }
    private sealed class State(decimal volume)
    {
        public readonly decimal Volume = volume;
        public readonly List<Wall> Walls = [];
        public readonly Queue<(long Ms, decimal Pct)> Departures = new();
        public decimal MedianLevel;
        // The coin's price step when known (from the exchange rules), otherwise estimated from the book.
        public decimal TickSize;
        public long Snapshots, Above4, Above10; public decimal SpreadSum;
        // Rolling windows kept as running sums: the last 10 s, the 15 min before them, and prices of the last 30 s.
        public readonly Queue<(long Ms, decimal Price, decimal Quote, bool Buy)> Recent = new(), Older = new();
        public readonly Queue<(long Ms, decimal Price)> Prices = new();
        public decimal RecentQuote, RecentBuy, OlderQuote, LastPrice; public long LastPriceMs;
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
    // The coin and moment of the last processed market event, and every event as it is found (the bot listens to both).
    public string? LastSymbol { get; private set; }
    public long LastMs { get; private set; }
    public Action<LabEvent>? Detected { get; set; }

    public SignalDetector(IReadOnlyDictionary<string, decimal> quoteVolumes, bool liquidations)
    {
        states = quoteVolumes.ToDictionary(x => x.Key, x => new State(x.Value), StringComparer.Ordinal);
        levels = new LevelScanner(new ScanSettings(), quoteVolumes, liquidations);
    }

    // A coin added while the stream runs (the live bot watches coins the screener points to): a "watch" event with its
    // 24-hour volume and price step. The level scanner does not follow such coins.
    public void Watch(string symbol, decimal volume, decimal tickSize)
    {
        if (!states.TryGetValue(symbol, out var state)) states[symbol] = state = new State(volume);
        if (tickSize > 0) state.TickSize = tickSize;
    }

    public void Process(CaptureEvent item)
    {
        LastSymbol = null;
        var known = levels.Candidates.Count;
        levels.Process(item);
        if (item.Kind == "history") { History(item.Payload); return; }
        if (item.Kind == "watch")
        {
            using var watch = JsonDocument.Parse(item.Payload);
            Watch(watch.RootElement.GetProperty("symbol").GetString()!, watch.RootElement.GetProperty("volume").GetDecimal(),
                watch.RootElement.TryGetProperty("tickSize", out var t) ? t.GetDecimal() : 0);
            return;
        }
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
        LastSymbol = symbol; LastMs = ms;
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
        // Sells eat a bid wall, buys an ask wall; a trade beyond the wall means it is gone.
        foreach (var wall in state.Walls)
        {
            if (wall.Side == 1 ? buy || price > wall.Price : !buy || price < wall.Price) continue;
            var eaten = price == wall.Price ? price * quantity : wall.Quote;
            wall.Eaten += eaten;
            wall.Eats.Enqueue((ms, eaten));
            while (wall.Eats.TryPeek(out var old) && old.Ms < ms - 3_000) wall.Eats.Dequeue();
        }
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
        state.LastPrice = price; state.LastPriceMs = ms;
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
                Emit(state, new(symbol, "Вынос цены", Math.Sign(move), ms, price, $"Ход за 30 с {move * 100:0.##}% — {Math.Abs(move) / typical:0.#}× обычной минутной свечи", Origin: p30));
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
        if (mid > 0 && state.Ask >= state.Bid)
        {
            var spread = (state.Ask - state.Bid) / mid * 10_000;
            state.Snapshots++; state.SpreadSum += spread;
            if (spread >= 4) state.Above4++;
            if (spread >= 10) state.Above10++;
        }
        Walls(symbol, state, ms, mid, bids, asks);
    }

    // Large resting orders ("плотности") and what happens to them. Approach: the price came within 0.1% of a wall that has
    // stood 30 s (with the event: through the wall; against: a bounce). Eaten: the price went through a standing wall.
    // Pulled: a standing wall vanished while the price was within 0.15% of it, the way a supporting order is pulled before
    // it is hit (direction: towards where it stood). Moved: a standing wall vanished and a wall of similar size appeared on
    // the same side at another price in the same snapshot, the way an order is moved after the price (direction: where it
    // moved). Only the visible 20 levels are known: a wall that left the visible range is dropped without an event.
    private void Walls(string symbol, State state, long ms, decimal mid, JsonElement bids, JsonElement asks)
    {
        if (mid <= 0) return;
        var levels = bids.EnumerateArray().Select(l => (Side: 1, Price: Dec(l[0]), Quote: Dec(l[0]) * Dec(l[1])))
            .Concat(asks.EnumerateArray().Select(l => (Side: -1, Price: Dec(l[0]), Quote: Dec(l[0]) * Dec(l[1])))).ToList();
        if (levels.Count < 10) return;
        var sorted = levels.Select(l => l.Quote).Order().ToList();
        state.MedianLevel = sorted[sorted.Count / 2];
        var threshold = Math.Max(WallLevels * state.MedianLevel, WallVolumeShare * state.Volume);
        var ready = ms - state.FirstTradeMs >= 5 * 60_000;
        var lowestBid = levels.Where(l => l.Side == 1).Min(l => l.Price);
        var highestAsk = levels.Where(l => l.Side == -1).Max(l => l.Price);
        var big = levels.Where(l => l.Quote >= threshold).ToList();
        var gone = new List<Wall>();
        foreach (var wall in state.Walls)
        {
            var level = levels.FirstOrDefault(l => l.Side == wall.Side && l.Price == wall.Price);
            if (level.Quote >= WallKeep * threshold)
            {
                // The snapshot already contains every trade before it: counting restarts from here.
                wall.Quote = level.Quote; wall.Peak = Math.Max(wall.Peak, level.Quote); wall.Eaten = 0;
                wall.Level = levels.Where(l => l.Side == wall.Side).Count(l => wall.Side == 1 ? l.Price > wall.Price : l.Price < wall.Price);
                continue;
            }
            gone.Add(wall);
        }
        foreach (var wall in gone)
        {
            state.Walls.Remove(wall);
            var standing = ms - wall.FirstMs >= WallAge;
            // Out of the visible range: nothing is known about it.
            if (wall.Side == 1 ? wall.Price < lowestBid : wall.Price > highestAsk) continue;
            var through = wall.Side == 1 ? state.Bid < wall.Price : state.Ask > wall.Price;
            // The price left the wall and never came back: that run counts as a departure too.
            if (!through && wall.Touched && !wall.Touching && wall.MaxAway > 0) Depart(state, wall, ms);
            var moved = big.Where(l => l.Side == wall.Side && l.Price != wall.Price && state.Walls.All(w => w.Price != l.Price || w.Side != l.Side)
                    && l.Quote >= wall.Quote * .7m && l.Quote <= wall.Quote * 1.3m)
                .OrderBy(l => Math.Abs(l.Price - wall.Price)).Select(l => (decimal?)l.Price).FirstOrDefault();
            var distance = Math.Abs(wall.Price / mid - 1);
            var size = $"{wall.Quote / 1000:0.#} тыс. $";
            if (!standing || !ready) { if (moved is { } p) state.Walls.Add(new(wall.Side, p, wall.Quote, wall.FirstMs)); continue; }
            if (through)
                Emit(state, new(symbol, "Проедание плотности", -wall.Side, ms, mid, $"Плотность {size} на {wall.Price} пройдена"));
            else if (moved is { } to)
            {
                Emit(state, new(symbol, "Переставление плотности", Math.Sign(to - wall.Price), ms, mid,
                    $"Плотность {size} {(wall.Side == 1 ? "на покупку" : "на продажу")} переставлена с {wall.Price} на {to}"));
                // The moved order is the same wall: its age carries over, so the next move counts as well.
                state.Walls.Add(new(wall.Side, to, wall.Quote, wall.FirstMs));
            }
            else if (distance <= WallPulledNear)
                Emit(state, new(symbol, "Снятие плотности", -wall.Side, ms, mid, $"Плотность {size} на {wall.Price} снята в {distance * 100:0.##}% от цены"));
        }
        foreach (var l in big.Where(l => state.Walls.All(w => w.Side != l.Side || w.Price != l.Price)))
            state.Walls.Add(new(l.Side, l.Price, l.Quote, ms)
            {
                Level = levels.Where(x => x.Side == l.Side).Count(x => l.Side == 1 ? x.Price > l.Price : x.Price < l.Price)
            });
        // Steps and the queue before each wall: what the price has to go through to reach it.
        var tick = state.TickSize > 0 ? state.TickSize : levels.Select(l => l.Price).Distinct().Order().Zip(levels.Select(l => l.Price).Distinct().Order().Skip(1))
            .Select(x => x.Second - x.First).Where(d => d > 0).DefaultIfEmpty(0).Min();
        foreach (var wall in state.Walls)
        {
            var best = wall.Side == 1 ? state.Bid : state.Ask;
            wall.Ticks = tick > 0 ? (long)Math.Round(Math.Abs(best - wall.Price) / tick) : 0;
            wall.Front = levels.Where(l => l.Side == wall.Side && (wall.Side == 1 ? l.Price > wall.Price : l.Price < wall.Price)).Sum(l => l.Quote);
        }
        foreach (var wall in state.Walls)
        {
            var away = Math.Abs(wall.Price / mid - 1);
            if (away <= WallNear)
            {
                if (!wall.Touching && wall.Touched && wall.MaxAway > 0) Depart(state, wall, ms);
                if (!wall.Touching) wall.Touches++;
                wall.Touching = wall.Touched = true; wall.MaxAway = 0;
            }
            else
            {
                wall.Touching = false;
                if (wall.Touched) wall.MaxAway = Math.Max(wall.MaxAway, away);
            }
        }
        while (state.Departures.TryPeek(out var d) && d.Ms < ms - 3_600_000) state.Departures.Dequeue();
        if (!ready) return;
        foreach (var wall in state.Walls.Where(w => !w.Approached && ms - w.FirstMs >= WallAge && Math.Abs(w.Price / mid - 1) <= WallNear))
        {
            wall.Approached = true;
            Emit(state, new(symbol, "Подход к плотности", -wall.Side, ms, mid,
                $"Цена в {Math.Abs(wall.Price / mid - 1) * 100:0.##}% от плотности {wall.Quote / 1000:0.#} тыс. $ {(wall.Side == 1 ? "на покупку" : "на продажу")}"));
        }
    }

    private static void Depart(State state, Wall wall, long ms)
    {
        var pct = Math.Round(wall.MaxAway * 100, 4);
        wall.Departures.Add(pct); state.Departures.Enqueue((ms, pct));
        wall.MaxAway = 0;
    }

    public CoinView? View(string symbol)
    {
        if (!states.TryGetValue(symbol, out var state) || state.BookMs == long.MinValue) return null;
        var ranges = state.Minutes.TakeLast(61).SkipLast(1).Where(m => m.Low > 0).Select(m => (m.High - m.Low) / m.Low).ToList();
        return new(symbol, state.BookMs, state.Bid, state.Ask, ranges.Count >= 30 ? Math.Round(ranges.Average() * 100, 4) : null,
            state.MedianLevel, state.Volume,
            state.Walls.Select(w => new WallView(w.Side, w.Price, w.Quote, w.Peak, w.FirstMs, Math.Max(0, w.Quote - w.Eaten),
                w.Eats.Sum(x => x.Quote) / 3, w.Level, w.Touches, w.Departures.ToArray(), w.Ticks, w.Front)).ToArray(),
            state.Departures.Select(d => d.Pct).ToArray(), state.LastPrice, state.LastPriceMs);
    }

    private void Emit(State state, LabEvent e, bool cooldown = true)
    {
        if (cooldown && state.Last.TryGetValue(e.Type, out var last) && e.AtMs - last < Cooldown) return;
        state.Last[e.Type] = e.AtMs;
        var ranges = state.Minutes.TakeLast(61).SkipLast(1).Where(m => m.Low > 0).Select(m => (m.High - m.Low) / m.Low).ToList();
        if (ranges.Count >= 30) e = e with { TypicalPct = Math.Round(ranges.Average() * 100, 4) };
        var pending = new Pending(e);
        state.Pending.Add(pending); all.Add(pending);
        Detected?.Invoke(e);
    }

    // Entry and exits are read from the book as the coin's own clock passes them; a stale book leaves the value unknown.
    private void Advance(State state, long ms)
    {
        firstMs = Math.Min(firstMs, ms);
        lastMs = Math.Max(lastMs, ms);
        foreach (var p in state.Pending.ToArray())
        {
            var at = p.Event.AtMs;
            // Before the first snapshot there is no book at all (BookMs is the minimum and ms - BookMs would overflow).
            var fresh = state.BookMs != long.MinValue && ms - state.BookMs <= BookAge && state.Bid > 0 && state.Ask > 0;
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
        all.Select(p => p.Event with { EntryAsk = p.Ask, EntryBid = p.Bid, ExitBid = p.ExitBid, ExitAsk = p.ExitAsk }).ToArray(),
        states.Where(x => x.Value.Snapshots > 0)
            .Select(x => new LabSpread(x.Key, x.Value.Snapshots, Math.Round(x.Value.SpreadSum, 4), x.Value.Above4, x.Value.Above10)).ToArray());

    private static decimal Dec(JsonElement e, string name) => Dec(e.GetProperty(name));
    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDecimal()
        : decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    // Runs a whole finished scan recording, plain or compressed.
    public static LabRecording Run(string directory, CancellationToken ct, Action<long>? progress = null)
    {
        var (volumes, liquidations) = Manifest(directory);
        var detector = new SignalDetector(volumes, liquidations);
        foreach (var item in Read(directory, ct, progress)) detector.Feed(item);
        return detector.Result(Path.GetFileName(directory));
    }

    // Coins of a recording with their 24-hour volume, and whether the liquidation stream was recorded.
    public static (Dictionary<string, decimal> Volumes, bool Liquidations) Manifest(string directory)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var volumes = manifest.RootElement.GetProperty("symbols").EnumerateArray()
            .ToDictionary(x => x.GetProperty("Symbol").GetString()!, x => x.GetProperty("QuoteVolume").GetDecimal(), StringComparer.Ordinal);
        return (volumes, manifest.RootElement.TryGetProperty("liquidations", out var flag) && flag.GetBoolean());
    }

    // The recorded events in file order, from the plain or the compressed file; unreadable lines are skipped.
    public static IEnumerable<CaptureEvent> Read(string directory, CancellationToken ct, Action<long>? progress = null)
    {
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
            if (item is not null) yield return item;
        }
        progress?.Invoke(lines);
    }

    // Process without failing on one malformed message: it is skipped, as in the recording check.
    public void Feed(CaptureEvent item)
    {
        try { Process(item); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException) { LastSymbol = null; }
    }
}
