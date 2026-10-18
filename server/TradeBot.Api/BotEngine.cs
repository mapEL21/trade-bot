using System.Globalization;
using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// A closed trade of the engine in units of the deposit: Fraction is the notional as a share of the deposit at entry (the
// money pass in SignalBots turns it into USDT), so the same engine result serves any deposit.
public sealed record EngineTrade(string Symbol, string Side, long OpenedMs, long ClosedMs, decimal Entry, decimal Exit, decimal Fraction,
    string EntryReason, string ExitReason, decimal BestBps, decimal WorstBps);
public sealed record EngineResult(int Version, string Recording, long FirstMs, long LastMs, EngineTrade[] Trades, Dictionary<string, int> Skipped,
    int Signals, int Incomplete);
// A position held right now, for the live view: the move so far is at the exit-side best price, before fees.
public sealed record EnginePosition(string Symbol, string Side, long OpenedMs, decimal Entry, decimal Mark, decimal Fraction, string EntryReason);
// A decision the engine sends out as it makes it, for an executor that places real orders (the engine itself keeps
// simulating). Price: the best price the order would take now; CancelPrice: where the strategy's scenario is cancelled,
// for the protective stop on the exchange.
public sealed record EngineIntent(string Kind, string Symbol, int Side, long Ms, decimal Fraction, decimal Price, decimal? CancelPrice, string Reason);
// A wall the screener confirmed (a "screener" event in the stream): the density bot trades only these when it has them.
public sealed record ScreenedWall(string Symbol, int Side, decimal Price);

// What a strategy decides: entries on its signals or on what it sees in the book, and exits by its own logic. Rules are in
// docs/BOT_LOGIC.md; the numbers there are fixed before any run on data.
public interface IBotStrategy
{
    void OnSignal(BotEngine bot, LabEvent e);
    void Manage(BotEngine bot, BotEngine.Position position, CoinView view, long ms);
    void Scan(BotEngine bot, CoinView view, long ms);
    void OnScreener(BotEngine bot, IReadOnlyList<ScreenedWall> walls) { }
}

// The bot over the raw market stream of a recording: the lab's detector keeps the market state (book, trades, walls,
// events), the strategy decides, the engine executes. Every decision uses only what has arrived so far; a market order is
// filled 250 ms after the decision at the opposite best price of the first fresh book snapshot (at most 2 s old), so a wall
// broken in between gives a worse fill. The same code runs a finished recording now and the live stream later.
public sealed class BotEngine
{
    // 2: density bounce entry by price steps and the queue before the wall (user's rule of 18.10.2026), screener walls.
    // 3: no entry decided on a book older than BookAge.
    // 4: fee rules of 19.10.2026 (user's decision): no trade whose fees are over 30% of its risk, for every strategy; a break of
    // a density only when the coin's typical minute candle is at least three round-trip fees.
    // 5: «Вынос цены · 60 с» as hypothesis No. 2 — cancelled only by the whole spike undone (decision of 19.10.2026).
    public const int Version = 5;
    // A trade whose round-trip fees take more than this share of its risk is not opened: the stop is too close for the fees.
    public const decimal MaxFeeShare = .3m;
    public const long Latency = 250, BookAge = 2_000;

    public sealed class Position(string symbol, int side, decimal fraction, string entryReason, object? context)
    {
        public readonly string Symbol = symbol; public readonly int Side = side; public readonly decimal Fraction = fraction;
        public readonly string EntryReason = entryReason; public readonly object? Context = context;
        public long OpenedMs, ExtremeMs; public decimal Entry, Extreme, Best, Worst;
        public bool Exiting; public string? ExitReason;
    }
    private sealed record Order(string Symbol, int Side, long DecideMs, bool Exit, string Reason, decimal Fraction, object? Context);

    private readonly SignalParameters parameters;
    private readonly SignalDetector detector;
    private readonly IBotStrategy strategy;
    private readonly Dictionary<string, Position> positions = new(StringComparer.Ordinal);
    private readonly List<Order> orders = [];
    private readonly List<EngineTrade> trades = [];
    private readonly Dictionary<string, int> skipped = [];
    private int signals, incomplete;
    private long firstMs = long.MaxValue, lastMs;

    public BotEngine(SignalParameters parameters, IReadOnlyDictionary<string, decimal> volumes, bool liquidations)
    {
        this.parameters = parameters;
        strategy = parameters.Signal switch
        {
            SignalParameters.Spike => new SpikeStrategy(),
            SignalParameters.Spike60 => new SpikeStrategy(fixedExitMs: 60_000),
            _ => new DensityStrategy(),
        };
        detector = new SignalDetector(volumes, liquidations) { Detected = e => strategy.OnSignal(this, e) };
    }

    public SignalParameters Parameters => parameters;
    public event Action<EngineIntent>? Intent;
    public Position? PositionOf(string symbol) => positions.GetValueOrDefault(symbol);
    public void CountSignal() => signals++;
    public void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

    public void Process(CaptureEvent item)
    {
        if (item.Kind == "screener")
        {
            try
            {
                using var doc = JsonDocument.Parse(item.Payload);
                strategy.OnScreener(this, doc.RootElement.GetProperty("walls").EnumerateArray()
                    .Select(w => new ScreenedWall(w.GetProperty("symbol").GetString()!, w.GetProperty("side").GetInt32(), w.GetProperty("price").GetDecimal()))
                    .ToList());
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            return;
        }
        detector.Feed(item);
        if (detector.LastSymbol is { } symbol) Update(symbol, detector.LastMs);
    }

    private void Update(string symbol, long ms)
    {
        if (detector.View(symbol) is not { } view) return;
        firstMs = Math.Min(firstMs, ms); lastMs = Math.Max(lastMs, ms);
        var fresh = ms - view.BookMs <= BookAge && view.Bid > 0 && view.Ask > 0;
        if (!fresh) return;
        foreach (var order in orders.Where(o => o.Symbol == symbol && ms >= o.DecideMs + Latency).ToList())
        {
            orders.Remove(order);
            if (order.Exit)
            {
                if (!positions.Remove(symbol, out var pos)) continue;
                var exit = Fill(view, -pos.Side);
                pos.Best = Math.Max(pos.Best, Move(pos, exit)); pos.Worst = Math.Min(pos.Worst, Move(pos, exit));
                trades.Add(new(symbol, pos.Side > 0 ? "long" : "short", pos.OpenedMs, ms, pos.Entry, exit, pos.Fraction, pos.EntryReason,
                    pos.ExitReason ?? "", Math.Round(pos.Best * 10_000, 2), Math.Round(pos.Worst * 10_000, 2)));
            }
            else
            {
                var entry = Fill(view, order.Side);
                positions[symbol] = new(symbol, order.Side, order.Fraction, order.Reason, order.Context)
                {
                    OpenedMs = ms, Entry = entry, Extreme = order.Side > 0 ? view.Bid : view.Ask, ExtremeMs = ms
                };
            }
        }
        if (positions.TryGetValue(symbol, out var open) && !open.Exiting)
        {
            var price = open.Side > 0 ? view.Bid : view.Ask;
            open.Best = Math.Max(open.Best, Move(open, price)); open.Worst = Math.Min(open.Worst, Move(open, price));
            strategy.Manage(this, open, view, ms);
        }
        if (!positions.ContainsKey(symbol) && orders.All(o => o.Symbol != symbol)) strategy.Scan(this, view, ms);
    }

    // A market order fills at the opposite best price of the book; a trade printed after that snapshot at a worse price shows
    // the book has moved since (a wall eaten between snapshots), and the order fills there instead.
    private static decimal Fill(CoinView view, int side)
    {
        var book = side > 0 ? view.Ask : view.Bid;
        if (view.LastTradeMs <= view.BookMs || view.LastTrade <= 0) return book;
        return side > 0 ? Math.Max(book, view.LastTrade) : Math.Min(book, view.LastTrade);
    }

    private static decimal Move(Position p, decimal price) => p.Entry > 0 ? (p.Side > 0 ? price / p.Entry - 1 : 1 - price / p.Entry) : 0;

    // A new entry, sized so that reaching the cancel level costs RiskPct of the deposit with fees: the notional as a share of
    // the deposit is RiskPct ÷ (risk distance + round-trip fees), within the leverage left. A flip opens while the old
    // position on the coin is being closed.
    public bool Open(string symbol, int side, long ms, decimal riskPct, string reason, object? context)
    {
        if (!parameters.InSession(ms)) { Skip("вне выбранных торговых сессий"); return false; }
        // A decision on an old book is a decision on a market that may be gone.
        if (detector.View(symbol) is not { Bid: > 0, Ask: > 0 } book || ms - book.BookMs > BookAge) { Skip("стакан устарел"); return false; }
        if (positions.TryGetValue(symbol, out var held) && !held.Exiting || orders.Any(o => o.Symbol == symbol && !o.Exit))
        { Skip("уже в сделке по этой монете"); return false; }
        var open = positions.Values.Where(p => !p.Exiting).ToList();
        var entries = orders.Where(o => !o.Exit).ToList();
        if (open.Count + entries.Count >= parameters.MaxPositions) { Skip("достигнут предел одновременных сделок"); return false; }
        if (riskPct <= 0) { Skip("нет расстояния до уровня отмены"); return false; }
        if (parameters.FeePct / (riskPct + parameters.FeePct) > MaxFeeShare) { Skip("комиссия больше 30% риска"); return false; }
        var fraction = parameters.RiskPct / (riskPct + parameters.FeePct);
        var left = parameters.MaxLeverage - open.Sum(p => p.Fraction) - entries.Sum(o => o.Fraction);
        if (left < .05m) { Skip("достигнут предел плеча"); return false; }
        var size = Math.Round(Math.Min(fraction, left), 4);
        orders.Add(new(symbol, side, ms, false, reason, size, context));
        if (Intent is { } intent && detector.View(symbol) is { } view)
        {
            var price = side > 0 ? view.Ask : view.Bid;
            // The cancel level is the risk distance away from the price, against the trade.
            intent(new("entry", symbol, side, ms, size, price, Math.Round(price * (1 - side * riskPct / 100), 10), reason));
        }
        return true;
    }

    public void Close(Position position, long ms, string reason)
    {
        if (position.Exiting) return;
        position.Exiting = true; position.ExitReason = reason;
        orders.Add(new(position.Symbol, -position.Side, ms, true, reason, 0, null));
        if (Intent is { } intent && detector.View(position.Symbol) is { } view)
            intent(new("exit", position.Symbol, -position.Side, ms, position.Fraction, position.Side > 0 ? view.Bid : view.Ask, null, reason));
    }

    // The result so far while the stream runs: open positions are neither trades nor incomplete yet.
    public EngineResult Current(string recording) =>
        new(Version, recording, firstMs == long.MaxValue ? 0 : firstMs, lastMs, trades.ToArray(), new(skipped), signals, incomplete);

    public EnginePosition[] Open() => positions.Values.Select(p =>
    {
        var view = detector.View(p.Symbol);
        var mark = view is null ? p.Entry : p.Side > 0 ? view.Bid : view.Ask;
        return new EnginePosition(p.Symbol, p.Side > 0 ? "long" : "short", p.OpenedMs, p.Entry, mark, p.Fraction, p.EntryReason);
    }).ToArray();

    // The end of the recording: what is still open has no known result and is not counted as a trade.
    public EngineResult Finish(string recording)
    {
        incomplete += positions.Count;
        if (orders.Any(o => !o.Exit)) skipped["запись закончилась до входа"] = orders.Count(o => !o.Exit);
        return new(Version, recording, firstMs == long.MaxValue ? 0 : firstMs, lastMs, trades.ToArray(), new(skipped), signals, incomplete);
    }

    public static EngineResult Run(string directory, SignalParameters parameters, CancellationToken ct)
    {
        var (volumes, liquidations) = SignalDetector.Manifest(directory);
        var engine = new BotEngine(parameters, volumes, liquidations);
        foreach (var item in SignalDetector.Read(directory, ct)) engine.Process(item);
        return engine.Finish(Path.GetFileName(directory));
    }
}

// A forced price jerk: follow it while the forced flow lasts (docs/BOT_LOGIC.md, «Вынос цены»); cancelled at the middle of
// the spike. With fixedExitMs it is hypothesis No. 2 as fixed in advance: out that long after the entry (60 s), and the only
// other exit is the whole spike undone — the price back at its start, which also sets the size and the protective stop.
public sealed class SpikeStrategy(long? fixedExitMs = null) : IBotStrategy
{
    private sealed record Spike(decimal Cancel);
    public const long StallMs = 60_000, HorizonMs = 300_000;

    public void OnSignal(BotEngine bot, LabEvent e)
    {
        if (fixedExitMs is null && e.Type is "Вынос цены" or "Каскад ликвидаций" && bot.PositionOf(e.Symbol) is { Exiting: false } held && held.Side == -e.Direction)
            bot.Close(held, e.AtMs, $"встречный сигнал: {e.Type.ToLowerInvariant()} в обратную сторону");
        if (e.Type != "Вынос цены") return;
        bot.CountSignal();
        if (e.Origin is not { } origin || origin <= 0) { bot.Skip("неизвестно начало выноса"); return; }
        var cancel = fixedExitMs is null ? (origin + e.Price) / 2 : origin;
        var risk = Math.Abs(e.Price - cancel) / e.Price * 100;
        bot.Open(e.Symbol, e.Direction, e.AtMs, risk, $"вынос цены: {e.Detail}", new Spike(cancel));
    }

    public void Manage(BotEngine bot, BotEngine.Position p, CoinView view, long ms)
    {
        if (p.Context is not Spike spike) return;
        var price = p.Side > 0 ? view.Bid : view.Ask;
        if (p.Side > 0 ? price <= spike.Cancel : price >= spike.Cancel)
        {
            bot.Close(p, ms, fixedExitMs is null ? "импульс поглощён: цена вернулась к середине выноса" : "вынос отменён целиком: цена вернулась к его началу");
            return;
        }
        if (fixedExitMs is { } hold)
        {
            if (ms - p.OpenedMs >= hold) bot.Close(p, ms, $"{hold / 1000} с после входа — выход по гипотезе № 2");
            return;
        }
        if (p.Side > 0 ? price > p.Extreme : price < p.Extreme) { p.Extreme = price; p.ExtremeMs = ms; }
        else if (ms - p.ExtremeMs >= StallMs) { bot.Close(p, ms, "движение выдохлось: 60 с без нового экстремума"); return; }
        if (ms - p.OpenedMs >= HorizonMs) bot.Close(p, ms, "горизонт гипотезы: 5 минут");
    }

    public void Scan(BotEngine bot, CoinView view, long ms) { }
}

// Trading around a large resting order (docs/BOT_LOGIC.md, «Плотность»): a bounce off it while it holds, out before it is
// eaten and over into the break, and the break itself while its impulse lasts.
public sealed class DensityStrategy : IBotStrategy
{
    private sealed record Bounce(int WallSide, decimal Wall, decimal Target, decimal TypicalPct);
    private sealed record Break(int WallSide, decimal Wall, decimal TypicalPct);
    // Entry (user's rule of 18.10.2026): the price is at most 5 steps from the wall and the quote resting before it is less
    // than a quarter of the wall — the price has almost reached it.
    public const decimal MinLevels = 10, MinVolumeShare = .0002m, EatenShare = .5m, EatSeconds = 1.5m, SlippagePct = .05m,
        MinTargetFees = 3, MaxFrontShare = .25m;
    public const int MaxTicks = 5, MinCoinDepartures = 3;
    public const long MinAgeMs = 60_000, StallMs = 30_000;
    private readonly HashSet<string> seen = [];
    // Walls the screener confirmed; null when the stream has none (a lab recording): then every strong wall counts.
    private HashSet<(string, int, decimal)>? screened;

    public void OnScreener(BotEngine bot, IReadOnlyList<ScreenedWall> walls) =>
        screened = walls.Select(w => (w.Symbol, w.Side, w.Price)).ToHashSet();
    private bool Screened(string symbol, WallView w) => screened is null || screened.Contains((symbol, w.Side, w.Price));

    private static bool Strong(WallView w, CoinView v, long ms) =>
        w.Quote >= MinLevels * v.MedianLevel && w.Quote >= MinVolumeShare * v.Volume && ms - w.FirstMs >= MinAgeMs;
    private static bool Eaten(WallView w) => w.Remaining <= EatenShare * w.Peak || w.EatRate > 0 && w.Remaining / w.EatRate <= EatSeconds;
    private static decimal Mid(CoinView v) => (v.Bid + v.Ask) / 2;
    private static decimal Distance(WallView w, CoinView v) => Math.Abs(w.Price / Mid(v) - 1) * 100;
    // Reasons are written the same on every machine, with a decimal comma.
    private static string N(decimal v, string format = "0.##") => v.ToString(format, CultureInfo.InvariantCulture).Replace('.', ',');
    private static string Size(WallView w) => $"{N(w.Quote / 1000, "0.#")} тыс. $";
    private static string Left(WallView w) => $"{(w.Peak > 0 ? w.Remaining / w.Peak * 100 : 0):0}%";

    public void OnSignal(BotEngine bot, LabEvent e) { }

    public void Scan(BotEngine bot, CoinView view, long ms)
    {
        foreach (var w in view.Walls.Where(w => Strong(w, view, ms) && Screened(view.Symbol, w) && w.Ticks <= MaxTicks))
        {
            var key = $"{view.Symbol}|{w.Side}|{w.Price}|{w.FirstMs}|{w.Touches}";
            if (Eaten(w))
            {
                if (!seen.Add(key + "|break")) continue;
                bot.CountSignal();
                if (view.TypicalPct is not { } t0) { bot.Skip("мало истории монеты"); return; }
                if (!BreakWorthIt(bot, t0)) return;
                bot.Open(view.Symbol, -w.Side, ms, Distance(w, view) + t0 / 2,
                    $"проедание плотности {Size(w)} на {N(w.Price, "0.########")}: осталось {Left(w)}", new Break(w.Side, w.Price, t0));
                return;
            }
            // Still a queue before the wall: wait until the price has almost reached it.
            if (w.Front >= MaxFrontShare * w.Quote) continue;
            // Once per approach of the price to the wall.
            if (!seen.Add(key)) continue;
            bot.CountSignal();
            if (view.TypicalPct is not { } typical) { bot.Skip("мало истории монеты"); return; }
            decimal? target = w.Departures.Length > 0 ? w.Departures.Average()
                : view.CoinDepartures.Length >= MinCoinDepartures ? Median(view.CoinDepartures) : null;
            if (target is not { } t) { bot.Skip("нет статистики отскоков"); return; }
            if (t < MinTargetFees * bot.Parameters.FeePct) { bot.Skip("средний отскок меньше трёх комиссий"); return; }
            bot.Open(view.Symbol, w.Side, ms, Distance(w, view) + SlippagePct,
                $"отскок от плотности {Size(w)} на {N(w.Price, "0.########")} ({N(w.Quote / Math.Max(1, view.MedianLevel), "0")}× среднего уровня, стоит {(ms - w.FirstMs) / 1000} с, {w.Ticks} шаг. до неё), цель {N(t)}%",
                new Bounce(w.Side, w.Price, t, typical));
            return;
        }
    }

    public void Manage(BotEngine bot, BotEngine.Position p, CoinView view, long ms)
    {
        var price = p.Side > 0 ? view.Bid : view.Ask;
        if (p.Context is Bounce b)
        {
            var wall = view.Walls.FirstOrDefault(w => w.Side == b.WallSide && w.Price == b.Wall);
            if (wall is null)
            {
                var through = b.WallSide == 1 ? view.Bid < b.Wall : view.Ask > b.Wall;
                bot.Close(p, ms, through ? "плотность пробита до выхода" : "плотность сняли");
                return;
            }
            if (Eaten(wall))
            {
                bot.Close(p, ms, $"плотность проедают: осталось {Left(wall)} — выход до пробоя");
                if (!BreakWorthIt(bot, b.TypicalPct)) return;
                bot.Open(p.Symbol, -b.WallSide, ms, Distance(wall, view) + b.TypicalPct / 2,
                    $"переворот в проедание плотности {Size(wall)} на {N(wall.Price, "0.########")}", new Break(b.WallSide, b.Wall, b.TypicalPct));
                return;
            }
            if (Math.Abs(price / b.Wall - 1) * 100 >= b.Target)
                bot.Close(p, ms, $"цена отошла от плотности на средний отскок {N(b.Target)}%");
            return;
        }
        if (p.Context is Break k)
        {
            // Back beyond the eaten wall by half a typical minute candle: the break did not happen.
            var back = k.Wall * (1 + k.WallSide * k.TypicalPct / 200);
            if (k.WallSide == 1 ? price >= back : price <= back) { bot.Close(p, ms, "проедание не состоялось: цена вернулась за плотность"); return; }
            if (p.Side > 0 ? price > p.Extreme : price < p.Extreme) { p.Extreme = price; p.ExtremeMs = ms; }
            else if (ms - p.ExtremeMs >= StallMs) bot.Close(p, ms, "импульс после проедания выдохся: 30 с без нового экстремума");
        }
    }

    // A break is followed while the move lasts; what it can be expected to give is about the coin's typical minute candle.
    // When that is under three round-trip fees, the break does not pay (rule of 19.10.2026).
    private static bool BreakWorthIt(BotEngine bot, decimal typicalPct)
    {
        if (typicalPct >= MinTargetFees * bot.Parameters.FeePct) return true;
        bot.Skip("типичная свеча меньше трёх комиссий");
        return false;
    }

    private static decimal Median(decimal[] values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
