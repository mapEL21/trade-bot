using System.Globalization;
using TradeBot.Recorder;

namespace TradeBot.Api;

// A closed trade of the engine in units of the deposit: Fraction is the notional as a share of the deposit at entry (the
// money pass in SignalBots turns it into USDT), so the same engine result serves any deposit.
public sealed record EngineTrade(string Symbol, string Side, long OpenedMs, long ClosedMs, decimal Entry, decimal Exit, decimal Fraction,
    string EntryReason, string ExitReason, decimal BestBps, decimal WorstBps);
public sealed record EngineResult(int Version, string Recording, long FirstMs, long LastMs, EngineTrade[] Trades, Dictionary<string, int> Skipped,
    int Signals, int Incomplete);

// What a strategy decides: entries on its signals or on what it sees in the book, and exits by its own logic. Rules are in
// docs/BOT_LOGIC.md; the numbers there are fixed before any run on data.
public interface IBotStrategy
{
    void OnSignal(BotEngine bot, LabEvent e);
    void Manage(BotEngine bot, BotEngine.Position position, CoinView view, long ms);
    void Scan(BotEngine bot, CoinView view, long ms);
}

// The bot over the raw market stream of a recording: the lab's detector keeps the market state (book, trades, walls,
// events), the strategy decides, the engine executes. Every decision uses only what has arrived so far; a market order is
// filled 250 ms after the decision at the opposite best price of the first fresh book snapshot (at most 2 s old), so a wall
// broken in between gives a worse fill. The same code runs a finished recording now and the live stream later.
public sealed class BotEngine
{
    public const int Version = 1;
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
        strategy = parameters.Signal == SignalParameters.Spike ? new SpikeStrategy() : new DensityStrategy();
        detector = new SignalDetector(volumes, liquidations) { Detected = e => strategy.OnSignal(this, e) };
    }

    public SignalParameters Parameters => parameters;
    public Position? PositionOf(string symbol) => positions.GetValueOrDefault(symbol);
    public void CountSignal() => signals++;
    public void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

    public void Process(CaptureEvent item)
    {
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
        if (positions.TryGetValue(symbol, out var held) && !held.Exiting || orders.Any(o => o.Symbol == symbol && !o.Exit))
        { Skip("уже в сделке по этой монете"); return false; }
        var open = positions.Values.Where(p => !p.Exiting).ToList();
        var entries = orders.Where(o => !o.Exit).ToList();
        if (open.Count + entries.Count >= parameters.MaxPositions) { Skip("достигнут предел одновременных сделок"); return false; }
        if (riskPct <= 0) { Skip("нет расстояния до уровня отмены"); return false; }
        var fraction = parameters.RiskPct / (riskPct + parameters.FeePct);
        var left = parameters.MaxLeverage - open.Sum(p => p.Fraction) - entries.Sum(o => o.Fraction);
        if (left < .05m) { Skip("достигнут предел плеча"); return false; }
        orders.Add(new(symbol, side, ms, false, reason, Math.Round(Math.Min(fraction, left), 4), context));
        return true;
    }

    public void Close(Position position, long ms, string reason)
    {
        if (position.Exiting) return;
        position.Exiting = true; position.ExitReason = reason;
        orders.Add(new(position.Symbol, -position.Side, ms, true, reason, 0, null));
    }

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

// A forced price jerk: follow it while the forced flow lasts (docs/BOT_LOGIC.md, «Вынос цены»).
public sealed class SpikeStrategy : IBotStrategy
{
    private sealed record Spike(decimal Middle);
    public const long StallMs = 60_000, HorizonMs = 300_000;
    public const decimal MaxFeeShare = .3m;

    public void OnSignal(BotEngine bot, LabEvent e)
    {
        if (e.Type is "Вынос цены" or "Каскад ликвидаций" && bot.PositionOf(e.Symbol) is { Exiting: false } held && held.Side == -e.Direction)
            bot.Close(held, e.AtMs, $"встречный сигнал: {e.Type.ToLowerInvariant()} в обратную сторону");
        if (e.Type != "Вынос цены") return;
        bot.CountSignal();
        if (e.Origin is not { } origin || origin <= 0) { bot.Skip("неизвестно начало выноса"); return; }
        var middle = (origin + e.Price) / 2;
        var risk = Math.Abs(e.Price - middle) / e.Price * 100;
        var fee = bot.Parameters.FeePct;
        if (fee / (risk + fee) > MaxFeeShare) { bot.Skip("комиссия больше 30% риска"); return; }
        bot.Open(e.Symbol, e.Direction, e.AtMs, risk, $"вынос цены: {e.Detail}", new Spike(middle));
    }

    public void Manage(BotEngine bot, BotEngine.Position p, CoinView view, long ms)
    {
        if (p.Context is not Spike spike) return;
        var price = p.Side > 0 ? view.Bid : view.Ask;
        if (p.Side > 0 ? price <= spike.Middle : price >= spike.Middle) { bot.Close(p, ms, "импульс поглощён: цена вернулась к середине выноса"); return; }
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
    public const decimal MinLevels = 10, MinVolumeShare = .0002m, MaxDistancePct = .15m, EatenShare = .5m, EatSeconds = 1.5m,
        SlippagePct = .05m, MinTargetFees = 3;
    public const int MaxLevel = 2, MinCoinDepartures = 3;
    public const long MinAgeMs = 60_000, StallMs = 30_000;
    private readonly HashSet<string> seen = [];

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
        foreach (var w in view.Walls.Where(w => Strong(w, view, ms) && Distance(w, view) <= MaxDistancePct))
        {
            // Once per approach of the price to the wall.
            if (!seen.Add($"{view.Symbol}|{w.Side}|{w.Price}|{w.FirstMs}|{w.Touches}")) continue;
            bot.CountSignal();
            if (view.TypicalPct is not { } typical) { bot.Skip("мало истории монеты"); return; }
            if (Eaten(w))
            {
                bot.Open(view.Symbol, -w.Side, ms, Distance(w, view) + typical / 2,
                    $"проедание плотности {Size(w)} на {N(w.Price, "0.########")}: осталось {Left(w)}", new Break(w.Side, w.Price, typical));
                return;
            }
            if (w.Level > MaxLevel) { bot.Skip("плотность дальше третьего уровня стакана"); continue; }
            decimal? target = w.Departures.Length > 0 ? w.Departures.Average()
                : view.CoinDepartures.Length >= MinCoinDepartures ? Median(view.CoinDepartures) : null;
            if (target is not { } t) { bot.Skip("нет статистики отскоков"); return; }
            if (t < MinTargetFees * bot.Parameters.FeePct) { bot.Skip("средний отскок меньше трёх комиссий"); return; }
            bot.Open(view.Symbol, w.Side, ms, Distance(w, view) + SlippagePct,
                $"отскок от плотности {Size(w)} на {N(w.Price, "0.########")} ({N(w.Quote / Math.Max(1, view.MedianLevel), "0")}× среднего уровня, стоит {(ms - w.FirstMs) / 1000} с), цель {N(t)}%",
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

    private static decimal Median(decimal[] values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
