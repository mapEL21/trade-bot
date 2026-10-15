using System.Globalization;
using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// Thresholds of the level scanner; all of them are settings, the defaults are a first guess to be checked on data.
public sealed record ScanSettings(int LookbackMinutes = 240, int SwingSpan = 2, decimal TouchTolerance = 0.3m, int MinTouches = 2,
    int MinTouchGapMinutes = 5, decimal LevelDistancePct = 3, int ConsolidationMinutes = 15, decimal NearPct = 1,
    decimal ApproachPct = 1.5m, int ApproachMinutes = 5, decimal BreakoutBufferPct = .05m, int ActivitySeconds = 10,
    int BaselineMinutes = 15, decimal TargetPct = 1.5m, decimal StopBufferPct = .15m, int OutcomeMinutes = 10,
    decimal WallMultiple = 5, decimal WallDistancePct = .5m, decimal RisePct = 5);

public sealed record Candle(long OpenMs, decimal Open, decimal High, decimal Low, decimal Close, decimal QuoteVolume);
// A level is a zone: Price is the extreme of its touches, Tolerance the half-width that counted as a touch.
public sealed record ScanLevel(string Side, decimal Price, decimal Tolerance, int Touches, long FirstMs, long LastMs, bool PeriodExtreme);
public sealed record ScoreItem(string Name, int Points, string Detail);
public sealed record ScanCandidate(string Id, string Symbol, string Side, long AtMs, decimal Level, decimal Price, int Touches, int Score,
    ScoreItem[] Items, string Result, decimal MaxFavorablePct, decimal MaxAdversePct, decimal LastPct, long? ResolvedMs);
public sealed record ScanLevelView(string Side, decimal Price, decimal ZoneLow, decimal ZoneHigh, int Touches, decimal DistancePct);
public sealed record ScanSymbolDetail(string Symbol, Candle[] Candles, ScanLevelView[] Levels, ScanCandidate[] Candidates);

// Turns the scan recording into candles, levels and scored breakouts with their outcome. Events are processed in file order,
// the order a live scan writes them, so a replay of the file gives the same candidates. No orders are sent anywhere.
// Liquidations: whether the recording has the liquidation stream; without it the sign is left out, not scored as "none".
public sealed class LevelScanner(ScanSettings settings, IReadOnlyDictionary<string, decimal> quoteVolumes, bool liquidations = false)
{
    private sealed class Book { public (decimal Price, decimal Size)[] Bids = [], Asks = []; }
    private sealed class Tracked(ScanCandidate candidate, ScanLevel level) { public ScanCandidate Candidate = candidate; public ScanLevel Level = level; }
    private sealed class State
    {
        public readonly List<Candle> Candles = [];
        public Candle? Current;
        public readonly Queue<(long Ms, decimal Quote, bool Buy)> Trades = new();
        // Forced orders: Buy closes a short (pushes the price up), sell closes a long.
        public readonly Queue<(long Ms, decimal Quote, bool Buy)> Liquidations = new();
        public decimal? Price;
        public Book Book = new();
        public List<ScanLevel> Levels = [];
        public readonly HashSet<(string, decimal)> Broken = [];
        public readonly List<Tracked> Open = [];
    }

    private readonly Dictionary<string, State> states = quoteVolumes.Keys.ToDictionary(x => x, _ => new State(), StringComparer.Ordinal);
    private readonly List<ScanCandidate> candidates = [];
    private long lastMs;
    // Candidates still followed when the recording ends keep "open": no result is made up for them.
    public IReadOnlyList<ScanCandidate> Candidates => candidates;
    public long LastMs => lastMs;

    public void Process(CaptureEvent item)
    {
        if (item.Kind == "history") { History(item.Payload); return; }
        if (item.Kind != "market") return;
        using var doc = JsonDocument.Parse(item.Payload);
        var data = doc.RootElement.GetProperty("data");
        var symbol = (data.TryGetProperty("s", out var s) ? s : data.GetProperty("o").GetProperty("s")).GetString()!;
        if (!states.TryGetValue(symbol, out var state)) return;
        switch (data.GetProperty("e").GetString())
        {
            case "aggTrade":
                Trade(symbol, state, data.GetProperty("T").GetInt64(), Dec(data, "p"), Dec(data, "q"), !data.GetProperty("m").GetBoolean());
                break;
            case "depthUpdate":
                state.Book = new() { Bids = Levels(data.GetProperty("b")), Asks = Levels(data.GetProperty("a")) };
                break;
            case "forceOrder":
                var order = data.GetProperty("o");
                var filled = Dec(order, "z");
                var average = Dec(order, "ap");
                var ms = order.GetProperty("T").GetInt64();
                state.Liquidations.Enqueue((ms, (average > 0 ? average : Dec(order, "p")) * filled, order.GetProperty("S").GetString() == "BUY"));
                while (state.Liquidations.TryPeek(out var old) && old.Ms < ms - settings.BaselineMinutes * 60000L) state.Liquidations.Dequeue();
                break;
        }
    }

    // Closed 1-minute klines from the recording's start; the still open one was dropped by the recorder.
    private void History(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var symbol = doc.RootElement.GetProperty("symbol").GetString()!;
        if (!states.TryGetValue(symbol, out var state)) return;
        foreach (var k in doc.RootElement.GetProperty("klines").EnumerateArray().SkipLast(1))
            state.Candles.Add(new(k[0].GetInt64(), Dec(k[1]), Dec(k[2]), Dec(k[3]), Dec(k[4]), Dec(k[7])));
        if (state.Candles.Count > 0) state.Price = state.Candles[^1].Close;
        state.Levels = FindLevels(state.Candles, settings);
    }

    private void Trade(string symbol, State state, long ms, decimal price, decimal quantity, bool buy)
    {
        // Exchange time only moves forward here: a late print keeps its price but not an earlier clock.
        ms = Math.Max(ms, state.Current?.OpenMs ?? 0);
        lastMs = Math.Max(lastMs, ms);
        var minute = ms / 60000 * 60000;
        if (state.Current is { } current && current.OpenMs != minute)
        {
            state.Candles.Add(current);
            if (state.Candles.Count > settings.LookbackMinutes + 120) state.Candles.RemoveRange(0, state.Candles.Count - settings.LookbackMinutes - 120);
            state.Current = null;
            // Levels are drawn on closed candles; a level already broken in this session is not drawn again.
            state.Levels = FindLevels(state.Candles, settings).Where(l => !state.Broken.Contains((l.Side, l.Price))).ToList();
        }
        var quote = price * quantity;
        state.Current = state.Current is { } c
            ? c with { High = Math.Max(c.High, price), Low = Math.Min(c.Low, price), Close = price, QuoteVolume = c.QuoteVolume + quote }
            : new Candle(minute, price, price, price, price, quote);
        state.Trades.Enqueue((ms, quote, buy));
        while (state.Trades.TryPeek(out var old) && old.Ms < ms - settings.BaselineMinutes * 60000L) state.Trades.Dequeue();
        state.Price = price;
        Track(state, ms, price);
        foreach (var level in state.Levels.ToArray())
        {
            var up = level.Side == "resistance";
            if (up ? price < level.Price * (1 + settings.BreakoutBufferPct / 100) : price > level.Price * (1 - settings.BreakoutBufferPct / 100)) continue;
            state.Levels.Remove(level); state.Broken.Add((level.Side, level.Price));
            var items = Score(symbol, state, level, ms, up);
            var candidate = new ScanCandidate($"{symbol}-{ms}-{level.Side}", symbol, up ? "long" : "short", ms, level.Price, price, level.Touches,
                Math.Clamp(items.Sum(x => x.Points), 0, 100), items.ToArray(), "open", 0, 0, 0, null);
            candidates.Add(candidate);
            state.Open.Add(new(candidate, level));
        }
    }

    // What the price did after the breakout: target first, back behind the level first, or neither within the time.
    private void Track(State state, long ms, decimal price)
    {
        foreach (var t in state.Open.ToArray())
        {
            var c = t.Candidate; var sign = c.Side == "long" ? 1 : -1;
            var move = sign * (price / c.Price - 1) * 100;
            c = c with { MaxFavorablePct = Math.Max(c.MaxFavorablePct, move), MaxAdversePct = Math.Min(c.MaxAdversePct, move), LastPct = move };
            var back = sign > 0 ? price <= t.Level.Price * (1 - settings.StopBufferPct / 100) : price >= t.Level.Price * (1 + settings.StopBufferPct / 100);
            var result = move >= settings.TargetPct ? "target" : back ? "stop" : ms - c.AtMs >= settings.OutcomeMinutes * 60000L ? "timeout" : null;
            if (result is not null) { c = c with { Result = result, ResolvedMs = ms }; state.Open.Remove(t); }
            else t.Candidate = c;
            candidates[candidates.FindIndex(x => x.Id == c.Id)] = c;
        }
    }

    public static List<ScanLevel> FindLevels(IReadOnlyList<Candle> all, ScanSettings s)
    {
        var candles = all.Skip(Math.Max(0, all.Count - s.LookbackMinutes)).ToList();
        if (candles.Count < 2 * s.SwingSpan + 2) return [];
        // The zone width follows the coin's own volatility: a share of the average 1-minute range of the last hour.
        var recent = candles.Skip(Math.Max(0, candles.Count - 60)).ToList();
        var tolerance = Math.Max(recent.Average(c => c.High - c.Low) * s.TouchTolerance, candles[^1].Close * .0001m);
        var levels = new List<ScanLevel>();
        foreach (var up in new[] { true, false })
        {
            decimal Extreme(Candle c) => up ? c.High : c.Low;
            var swings = new List<int>();
            for (var i = s.SwingSpan; i < candles.Count - s.SwingSpan; i++)
            {
                var value = Extreme(candles[i]);
                var isSwing = true;
                for (var j = i - s.SwingSpan; j <= i + s.SwingSpan && isSwing; j++)
                    if (j != i && (up ? Extreme(candles[j]) > value || (j < i && Extreme(candles[j]) == value) : Extreme(candles[j]) < value || (j < i && Extreme(candles[j]) == value)))
                        isSwing = false;
                if (isSwing) swings.Add(i);
            }
            var periodExtreme = up ? candles.Max(c => c.High) : candles.Min(c => c.Low);
            var used = new HashSet<int>();
            foreach (var anchor in swings.OrderBy(i => up ? -Extreme(candles[i]) : Extreme(candles[i])))
            {
                if (used.Contains(anchor)) continue;
                var price = Extreme(candles[anchor]);
                var members = swings.Where(i => !used.Contains(i) && Math.Abs(Extreme(candles[i]) - price) <= tolerance).OrderBy(i => i).ToList();
                foreach (var i in members) used.Add(i);
                if (members.Count < s.MinTouches) continue;
                if ((candles[members[^1]].OpenMs - candles[members[0]].OpenMs) < s.MinTouchGapMinutes * 60000L) continue;
                // Still intact: no candle closed beyond the zone since the first touch.
                if (candles.Skip(members[0]).Any(c => up ? c.Close > price + tolerance : c.Close < price - tolerance)) continue;
                levels.Add(new(up ? "resistance" : "support", price, tolerance, members.Count, candles[members[0]].OpenMs,
                    candles[members[^1]].OpenMs, price == periodExtreme));
            }
        }
        var close = candles[^1].Close;
        return levels.Where(l => Math.Abs(l.Price / close - 1) * 100 <= s.LevelDistancePct
            && (l.Side == "resistance" ? l.Price >= close : l.Price <= close)).ToList();
    }

    private List<ScoreItem> Score(string symbol, State state, ScanLevel level, long ms, bool up)
    {
        var items = new List<ScoreItem>();
        var sign = up ? 1 : -1;
        items.Add(new("Касания", level.Touches >= 4 ? 25 : level.Touches == 3 ? 18 : 10, $"{level.Touches} касания уровня"));
        if (level.PeriodExtreme) items.Add(new("Экстремум периода", 5, up ? "Уровень — максимум за период" : "Уровень — минимум за период"));

        var window = state.Candles.Skip(Math.Max(0, state.Candles.Count - settings.ConsolidationMinutes)).ToList();
        if (state.Current is { } current) window.Add(current);
        var near = window.Count == 0 ? 0 : window.Count(c => sign * (level.Price - c.Close) >= 0
            && Math.Abs(c.Close / level.Price - 1) * 100 <= settings.NearPct) / (decimal)window.Count;
        var touched = window.Any(c => Math.Abs((up ? c.High : c.Low) - level.Price) <= level.Tolerance);
        items.Add(new("Проторговка", (int)Math.Round(near * (touched ? 20 : 10)),
            $"{near * 100:0}% минут у уровня (в пределах {settings.NearPct}%){(touched ? ", было касание" : ", без касания")}"));
        if (window.Count >= 15)
        {
            var last = window.TakeLast(5); var before = window.Skip(window.Count - 15).Take(10);
            var squeeze = up ? last.Min(c => c.Low) > before.Min(c => c.Low) : last.Max(c => c.High) < before.Max(c => c.High);
            if (squeeze) items.Add(new("Поджимка", 5, up ? "Минимумы последних 5 минут выше предыдущих" : "Максимумы последних 5 минут ниже предыдущих"));
        }
        var approachFrom = state.Candles.Count >= settings.ApproachMinutes ? state.Candles[^settings.ApproachMinutes].Open : (decimal?)null;
        if (approachFrom is { } from)
        {
            var approach = sign * (level.Price / from - 1) * 100;
            if (approach >= settings.ApproachPct && near < .5m)
                items.Add(new("Подлёт", -20, $"За {settings.ApproachMinutes} мин цена прошла к уровню {approach:0.##}% без проторговки"));
        }

        var trades = state.Trades.ToArray();
        var recent = trades.Where(t => t.Ms > ms - settings.ActivitySeconds * 1000L).ToArray();
        var baseline = trades.Where(t => t.Ms <= ms - settings.ActivitySeconds * 1000L).ToArray();
        var baselineSeconds = Math.Max(settings.ActivitySeconds, (ms - (baseline.Length > 0 ? baseline[0].Ms : ms)) / 1000m);
        var perWindow = baseline.Sum(t => t.Quote) / baselineSeconds * settings.ActivitySeconds;
        var volume = recent.Sum(t => t.Quote);
        var ratio = perWindow > 0 ? volume / perWindow : 0;
        items.Add(new("Активность ленты", (int)Math.Round(Math.Clamp((ratio - 1) / 2, 0, 1) * 20),
            $"Объём за {settings.ActivitySeconds} с в {ratio:0.#}× выше среднего"));
        var with = recent.Where(t => t.Buy == up).Sum(t => t.Quote);
        var share = volume > 0 ? with / volume : 0;
        items.Add(new("Направление ленты", share >= .6m ? 10 : share <= .4m ? -10 : 0,
            $"{share * 100:0}% объёма — {(up ? "покупки" : "продажи")} агрессором"));
        if (baseline.Length >= 100)
        {
            var sizes = baseline.Select(t => t.Quote).Order().ToArray();
            var p99 = sizes[(int)Math.Ceiling(.99 * sizes.Length) - 1];
            var big = recent.Where(t => t.Buy == up).Select(t => t.Quote).DefaultIfEmpty(0).Max();
            if (big >= p99) items.Add(new("Крупный принт", 10, $"Сделка {big:0} USDT — крупнее 99% сделок за {settings.BaselineMinutes} мин"));
        }
        if (liquidations)
        {
            // Stops and liquidations taken out by the breakout itself: forced orders in its direction just before it.
            var forced = state.Liquidations.Where(l => l.Buy == up && l.Ms > ms - settings.ActivitySeconds * 1000L && l.Ms <= ms).Sum(l => l.Quote);
            items.Add(new("Ликвидации", forced > 0 ? 10 : 0, forced > 0
                ? $"Ликвидации {(up ? "шортов" : "лонгов")} на {forced:0} USDT за {settings.ActivitySeconds} с"
                : $"Ликвидаций {(up ? "шортов" : "лонгов")} за {settings.ActivitySeconds} с не было"));
        }

        var dayVolume = quoteVolumes.GetValueOrDefault(symbol);
        if (dayVolume >= 250_000_000) items.Add(new("Объём монеты", 5, $"{dayVolume / 1_000_000:0} млн $ за 24 ч"));
        var minuteVolume = perWindow / settings.ActivitySeconds * 60;
        var beyond = (up ? state.Book.Asks : state.Book.Bids)
            .Where(x => sign * (x.Price - level.Price) >= 0 && Math.Abs(x.Price / level.Price - 1) * 100 <= settings.WallDistancePct)
            .Select(x => x.Price * x.Size).DefaultIfEmpty(0).Max();
        if (minuteVolume > 0 && beyond >= settings.WallMultiple * minuteVolume)
            items.Add(new("Плотность за уровнем", -15, $"Заявка {beyond:0} USDT в {beyond / minuteVolume:0.#}× среднего минутного объёма"));
        var lookback = state.Candles.Skip(Math.Max(0, state.Candles.Count - settings.LookbackMinutes)).ToList();
        if (lookback.Count > 0)
        {
            var move = up ? (1 - lookback.Max(c => c.High) / level.Price) * 100 : (level.Price / lookback.Min(c => c.Low) - 1) * 100;
            if (!up && move >= settings.RisePct) items.Add(new("Монета после роста", 5, $"Рост на {move:0.#}% за период"));
        }
        return items;
    }

    public ScanSymbolDetail? Detail(string symbol, int candles = 180)
    {
        if (!states.TryGetValue(symbol, out var state)) return null;
        var list = state.Candles.TakeLast(candles).ToList();
        if (state.Current is { } current) list.Add(current);
        return new(symbol, list.ToArray(), state.Levels.Select(l => View(l, state.Price)).ToArray(), candidates.Where(c => c.Symbol == symbol).ToArray());
    }

    private static ScanLevelView View(ScanLevel l, decimal? price) => new(l.Side, l.Price, l.Price - l.Tolerance, l.Price + l.Tolerance, l.Touches,
        price is { } p && p > 0 ? Math.Round(Math.Abs(l.Price / p - 1) * 100, 3) : 0);

    private static (decimal, decimal)[] Levels(JsonElement side) => side.EnumerateArray().Select(x => (Dec(x[0]), Dec(x[1]))).ToArray();
    private static decimal Dec(JsonElement e, string name) => Dec(e.GetProperty(name));
    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDecimal()
        : decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
