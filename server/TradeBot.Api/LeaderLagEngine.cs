using TradeBot.Recorder;

namespace TradeBot.Api;

// One input to the strategy engine, in local arrival time (ms from the session start). IsClock only moves time forward,
// which a live session needs so that orders come due even when the market is quiet.
public sealed record EngineEvent(double Ms, int Venue, MarketQuote? Quote, bool Reset, int Sequence, string Connection, bool IsBook = false,
    ReplayBookUpdate? Book = null, bool IsTrade = false, ReplayPrint[]? Prints = null, bool IsClock = false);
// MaxNotional caps one order (a bot's allocated balance); OrderQuantity in the settings fixes the size in coins instead of USDT.
// With Leverage above 1, MaxNotional is the balance times leverage and a position whose loss nears its isolated margin is closed.
public sealed record EngineSetup(int Leader, decimal Basis, ReplaySettings Settings, int DelayMs, bool Counter, ReplayInstrument? Instrument,
    decimal? MaxNotional = null, int Leverage = 1);
// Receives every decision and closed trade as it happens (the report keeps only the first 500); called on the engine thread.
public interface IEngineObserver
{
    void Decision(ReplayDecision decision);
    void Trade(ReplayTrade trade);
    // Whether a position, an order in flight or a resting order exists; reported when it changes.
    void Exposure(bool open) { }
    // A reason to send no new entry at this virtual time (pause, loss limit, stopping), or null. Exits are never blocked.
    string? EntryBlock(double ms) => null;
    // The open position marked at the current exit price after fees, on each quote while it is held; null once flat.
    void Position(OpenPosition? position) { }
    // True when the user asked to close now: the position exits as if a strategy exit fired, a resting order is cancelled.
    bool ExitNow(double ms) => false;
}

// MarkNetBps and MarkNetQuote are what closing now would give after both fees; in the BBO model the size is the configured one.
public sealed record OpenPosition(string Side, double SinceSeconds, decimal EntryPrice, decimal Quantity, decimal MarkPrice,
    decimal MarkNetBps, decimal? MarkNetQuote, bool ExitPending);

internal sealed class VenueQuotes
{
    public readonly List<(double Ms, MarketQuote Quote)> History = [];
    public MarketQuote? Current;
    public double At;
    public bool Fresh(double ms, int age) => Current is not null && ms - At <= age;
    public MarketQuote? Before(double ms, int age)
    {
        int lo = 0, hi = History.Count - 1, found = -1;
        while (lo <= hi) { var mid = lo + (hi - lo) / 2; if (History[mid].Ms <= ms) { found = mid; lo = mid + 1; } else hi = mid - 1; }
        return found >= 0 && ms - History[found].Ms <= age ? History[found].Quote : null;
    }
    public void Apply(EngineEvent tick)
    {
        if (tick.Reset) History.Clear();
        Current = tick.Quote; At = tick.Ms;
        if (tick.Quote is not null) History.Add((tick.Ms, tick.Quote));
    }
}

// Turns quote-feed inputs into engine events one at a time: messages from a superseded connection and quotes older than
// the latest seen are dropped; connection events and unreadable quotes reset the venue. Shared by replay and live.
public sealed class QuoteNormalizer(string sourceExchange, string exchange)
{
    private readonly string[] ids = [RecorderOptions.QuoteFeedId(sourceExchange), RecorderOptions.QuoteFeedId(exchange)];
    private readonly string?[] connections = [null, null];
    private readonly MarketQuote?[] latest = [null, null];

    public EngineEvent? Push(MarketAnalysis.Input item, double at)
    {
        var venue = Array.IndexOf(ids, item.Source); if (venue < 0) return null;
        if (item.Kind is "connecting" or "connected" or "gap")
        { connections[venue] = item.Connection; latest[venue] = null; return new(at, venue, null, true, item.Sequence, item.Connection); }
        if (connections[venue] != item.Connection) return null;
        if (item.Quote is null) { latest[venue] = null; return new(at, venue, null, true, item.Sequence, item.Connection); }
        if (latest[venue] is { } old && old.SourceOrder > item.Quote.SourceOrder) return null;
        latest[venue] = item.Quote; return new(at, venue, item.Quote, false, item.Sequence, item.Connection);
    }
}

// The leader-lag decision and virtual execution engine, shared by recording replay and live paper trading.
// No API keys, networking, orders or account state: every fill here is simulated.
public static class LeaderLagEngine
{
    // Quantity is the open remainder; earlier partial exits are kept so the closed trade gets one weighted exit price.
    private sealed record Position(int Sign, double At, decimal Price, decimal EntryResidual, decimal Quantity = 0,
        decimal ExitedQuantity = 0, decimal ExitedValue = 0, string? RetryReason = null, decimal? EntryFeeBps = null,
        decimal? Best = null, decimal? Worst = null)
    {
        // Price move in the position's direction before fees, in b.p.; Best/Worst track it while the position is open.
        public decimal Move(decimal exit) => Sign * (exit / Price - 1) * 10000;
        public Position Seen(decimal exit) { var m = Move(exit); return this with { Best = Math.Max(Best ?? m, m), Worst = Math.Min(Worst ?? m, m) }; }
    }
    // An order already sent: side, size, limit and the signal's lag are fixed at the decision and cannot be revised in flight.
    private sealed record Pending(int Sign, double Due, string Reason, bool Exit, decimal Quantity = 0, decimal Limit = 0, decimal Residual = 0);
    // A post-only entry resting at its own price from Live until a cancel request (CancelAt) reaches the exchange.
    // QueueAhead is the visible size at our price when the order went live; Traded is the aggressor volume at or through our price since.
    private sealed record Resting(int Sign, double Live, decimal Price, decimal Quantity, double Expires, double? CancelAt, decimal Residual,
        decimal QueueAhead = 0, decimal Traded = 0);
    // Events must arrive in time order. A null end means "the last event seen", which is how a live session stops.
    public static ReplayScenario Run(IEnumerable<EngineEvent> events, EngineSetup setup, CancellationToken ct, double? end = null, IEngineObserver? observer = null)
    {
        var (leader, basis, s, delay, counter, instrument, _, leverage) = setup;
        // Loss after fees, in b.p. of the entry nominal, at which an isolated position nears liquidation: its margin minus a
        // maintenance margin assumed at 0.5% (exchanges use roughly 0.4-0.5% for small positions). Unreachable at 1x.
        var liquidationBps = leverage > 1 ? 10000m / leverage - 50 : decimal.MaxValue; double lastMs = 0; bool reportedExposure = false, reportedPosition = false;
        var q = new[] { new VenueQuotes(), new VenueQuotes() }; var journal = new List<ReplayDecision>(); var trades = new List<ReplayTrade>();
        var depth = s.ExecutionModel == "depth"; var book = new ReplayBook(); string? bookConnection = null, tradeConnection = null;
        Position? position = null; Pending? pending = null; int skipped = 0, signals = 0, unresolved = 0, journalCount = 0;
        int partialEntries = 0, partialExits = 0, makerOrders = 0, makerFills = 0, sent = 0, filled = 0, unfilled = 0;
        var maker = s.EntryOrder == "maker"; Resting? resting = null;
        decimal sum = 0, peak = 0, drawdown = 0, closedNet = 0; double nextSignal = s.CalibrationSeconds * 1000; bool halted = false;
        void Log(double at, string action, string reason, int sign = 0, decimal? price = null, decimal? net = null, decimal? quantity = null)
        {
            var decision = new ReplayDecision(at / 1000, action, reason, sign == 0 ? null : sign > 0 ? "long" : "short", price, net, quantity);
            journalCount++; if (journal.Count < 500) journal.Add(decision); observer?.Decision(decision);
        }
        bool Fresh(double at) => q.All(x => x.Fresh(at, s.MaxAgeMs));
        decimal Residual() => (q[leader].Current!.Mid / q[1 - leader].Current!.Mid - 1) * 10000 - basis;
        decimal Price(int sign, bool exit)
        { var buy = exit ? sign < 0 : sign > 0; var quote = q[1 - leader].Current!; return buy ? quote.Ask * (1 + s.SlippageBps / 10000) : quote.Bid * (1 - s.SlippageBps / 10000); }
        decimal Net(decimal exit) => position!.Sign * (exit / position.Price - 1) * 10000 - (position.EntryFeeBps ?? s.FeeBps) - s.FeeBps * exit / position.Price;
        // A maker entry rests on its own side of the book, so mid-to-mid lag pays no spread; the taker exit still pays fee and slippage.
        decimal Costs() => maker ? s.MakerFeeBps + s.FeeBps + s.SlippageBps + s.MinEdgeBps
            : 2 * (s.FeeBps + s.SlippageBps) + q[1 - leader].Current!.SpreadBps + s.MinEdgeBps;
        bool SizeAllowed(decimal quantity, decimal price) => quantity > 0 && quantity >= instrument!.MinimumQuantity
            && (instrument.MaximumQuantity is not { } max || quantity <= max) && quantity * price >= instrument.MinimumNotional;
        Pending Order(int sign, double at, string reason, bool exit)
        {
            if (maker && !exit)
            {
                // Post-only price: our own best bid for a buy, best ask for a sell, fixed at the decision.
                var own = sign > 0 ? q[1 - leader].Current!.Bid : q[1 - leader].Current!.Ask;
                return new(sign, at + delay, reason, false, depth ? instrument!.RoundQuantity(s.OrderQuantity ?? s.OrderNotional / own) : 0, own);
            }
            if (!depth) return new(sign, at + delay, reason, exit);
            var buy = exit ? sign < 0 : sign > 0; var quote = q[1 - leader].Current!;
            var reference = buy ? quote.Ask : quote.Bid;
            var limit = instrument!.Limit(reference, buy, s.PriceLimitBps);
            // For buys, size against the worst allowed price so the configured nominal is a ceiling.
            var quantity = exit ? position!.Quantity : s.OrderQuantity is { } fixedSize ? instrument.RoundQuantity(fixedSize)
                : limit <= 0 ? 0 : instrument.RoundQuantity(s.OrderNotional / (buy ? limit : reference));
            return new(sign, at + delay, reason, exit, quantity, limit);
        }
        void LosePosition(double at, string reason = "Потеря данных при открытой позиции: сценарий остановлен.")
        { unresolved++; halted = true; pending = null; Log(at, "не оценено", reason, quantity: depth ? position?.Quantity : null); }
        // Losing data while an entry is in flight or resting leaves its fill unknown; it is not a guaranteed cancel.
        void LoseOrder(double at, decimal quantity, string reason = "Потеря данных ведомой биржи при отправленной заявке на вход: исполнение неизвестно.")
        { unresolved++; halted = true; pending = null; Log(at, "не оценено", reason, quantity: depth ? quantity : null); }
        // Our order joins the back of its price level: only aggressor volume beyond the visible queue ahead can reach it.
        decimal MakerFilled(Resting r) => Math.Clamp(decimal.Floor((r.Traded - r.QueueAhead) / instrument!.QuantityStep) * instrument.QuantityStep, 0, r.Quantity);
        void OpenMaker(double at, Resting r, decimal quantity, string reason)
        {
            makerFills++; filled++; if (quantity < r.Quantity) partialEntries++;
            position = new(r.Sign, at, r.Price, r.Residual, quantity, EntryFeeBps: s.MakerFeeBps); resting = null;
            Log(at, quantity < r.Quantity ? "частичный вход" : "вход", reason, r.Sign, r.Price, quantity: depth ? quantity : null);
        }
        void Advance(double to)
        {
            while (!halted)
            {
                // Once an exit is being retried the hold limit has no further role; re-arming it would reuse a past timestamp forever.
                var due = pending?.Due ?? (position is not null ? position.RetryReason is null ? position.At + s.MaxHoldMs : double.PositiveInfinity
                    : resting is not null ? resting.CancelAt is { } cancel ? cancel + delay : resting.Expires : double.PositiveInfinity);
                var staleAt = position is not null ? Math.Min(q[0].At, q[1].At) + s.MaxAgeMs + .001
                    // Silence from the execution venue while an order rests is not evidence that it did not fill.
                    : resting is not null ? q[1 - leader].At + s.MaxAgeMs + .001 : double.PositiveInfinity;
                if (depth && position is not null) staleAt = Math.Min(staleAt, book.At + s.MaxAgeMs + .001);
                if (staleAt <= to && staleAt < due)
                {
                    if (position is not null) LosePosition(staleAt);
                    else LoseOrder(staleAt, resting!.Quantity, "Данные ведомой биржи устарели при выставленной заявке maker: исполнение неизвестно.");
                    break;
                }
                if (due > to) break;
                if (resting is not null)
                {
                    // The cancel request needs the same delay to reach the exchange; the order can still fill meanwhile.
                    if (resting.CancelAt is null) { resting = resting with { CancelAt = due }; Log(due, "отмена запрошена", "Срок ожидания заявки maker истёк."); }
                    else if (depth && MakerFilled(resting) is > 0 and var part)
                        OpenMaker(due, resting, part, "Отмена дошла до биржи; позиция открыта на исполненную часть заявки maker.");
                    else { resting = null; skipped++; unfilled++; Log(due, "пропуск", "Заявка maker не исполнилась и снята."); }
                    continue;
                }
                if (pending is null) { pending = Order(position!.Sign, due, "Лимит времени удержания", true); Log(due, "выход запрошен", pending.Reason); continue; }
                var order = pending; pending = null;
                if (order.Exit && (!Fresh(due) || (depth && !book.Fresh(due, s.MaxAgeMs)))) { LosePosition(due); continue; }
                // An entry only needs the execution venue; the leader going quiet does not cancel an order already sent.
                if (!order.Exit && (!q[1 - leader].Fresh(due, s.MaxAgeMs) || (depth && !book.Fresh(due, s.MaxAgeMs))))
                { LoseOrder(due, order.Quantity); continue; }
                if (order.Exit)
                {
                    var price = Price(order.Sign, true); decimal total = 0;
                    if (depth)
                    {
                        var fill = book.Fill(order.Sign < 0, order.Quantity, order.Limit, s.SlippageBps, instrument!.QuantityStep, true);
                        if (fill.Quantity > 0 && fill.Quantity < position!.Quantity)
                        {
                            partialExits++; Log(due, "частичный выход", "Исполнена только видимая часть в пределах цены; сделка не закрыта полностью.", order.Sign, fill.Price, quantity: fill.Quantity);
                        }
                        position = position! with { Quantity = position.Quantity - fill.Quantity, ExitedQuantity = position.ExitedQuantity + fill.Quantity,
                            ExitedValue = position.ExitedValue + fill.Quantity * fill.Price };
                        if (position.Quantity > 0)
                        {
                            if (fill.BeyondVisibleDepth) { LosePosition(due, "Выход за пределы записанной глубины: остаток позиции не оценён."); continue; }
                            // A real bot keeps closing. Dropping trades whose exit missed the limit would hide the worst exits from statistics.
                            position = position with { RetryReason = position.RetryReason ?? order.Reason };
                            Log(due, "выход не исполнен", "Предел цены не позволил закрыть остаток; новая заявка на выход после следующего обновления котировок.", order.Sign, quantity: position.Quantity);
                            continue;
                        }
                        total = position.ExitedQuantity; price = position.ExitedValue / total;
                    }
                    var net = Net(price); position = position!.Seen(price);
                    decimal? fees = depth ? total * (position!.Price * (position.EntryFeeBps ?? s.FeeBps) + price * s.FeeBps) / 10000 : null;
                    decimal? cash = depth ? total * position!.Sign * (price - position.Price) - fees : null;
                    var closed = new ReplayTrade(position!.At / 1000, due / 1000, order.Sign > 0 ? "long" : "short", position.Price, price, net, order.Reason,
                        depth ? total : null, cash, fees, Math.Round(position.Best!.Value, 2), Math.Round(position.Worst!.Value, 2));
                    trades.Add(closed); observer?.Trade(closed);
                    closedNet += cash ?? 0;
                    // Sum and drawdown in b.p. of the configured nominal; with a size in coins, of this trade's entry nominal.
                    sum += depth ? cash!.Value / (s.OrderQuantity is null ? s.OrderNotional : total * position.Price) * 10000 : net; peak = Math.Max(peak, sum); drawdown = Math.Max(drawdown, peak - sum);
                    Log(due, "выход", order.Reason, order.Sign, price, net, depth ? total : null); position = null;
                }
                else
                {
                    // No second look at the lag here: the order left at the signal and executes on the conditions fixed then.
                    if (maker)
                    {
                        var quote = q[1 - leader].Current!;
                        if (order.Sign > 0 ? quote.Ask <= order.Limit : quote.Bid >= order.Limit)
                        { skipped++; unfilled++; Log(due, "пропуск", "Post-only заявка отклонена: цена уже дошла до её уровня, и она исполнилась бы как taker."); continue; }
                        makerOrders++; resting = new(order.Sign, due, order.Limit, order.Quantity, due + s.MakerWaitMs, null, order.Residual,
                            depth ? book.Quantity(order.Sign > 0, order.Limit) : 0);
                        Log(due, "заявка maker выставлена", counter ? "Контрольный вход против импульса" : "Отставание покрывает заданный порог расходов",
                            order.Sign, order.Limit, quantity: depth ? order.Quantity : null);
                        continue;
                    }
                    var price = Price(order.Sign, false); decimal quantity = 0;
                    if (depth)
                    {
                        var fill = book.Fill(order.Sign > 0, order.Quantity, order.Limit, s.SlippageBps, instrument!.QuantityStep, false);
                        if (fill.BeyondVisibleDepth)
                        {
                            unresolved++; halted = true;
                            Log(due, "не оценено", "Для объёма отправленной заявки не хватает записанной глубины; исполнение за последним уровнем неизвестно.", quantity: order.Quantity);
                            continue;
                        }
                        if (fill.Quantity == 0)
                        { skipped++; unfilled++; Log(due, "пропуск", "В пределах цены заявки нет доступного объёма."); continue; }
                        book.Fill(order.Sign > 0, order.Quantity, order.Limit, s.SlippageBps, instrument.QuantityStep, true);
                        price = fill.Price; quantity = fill.Quantity;
                        if (quantity < order.Quantity) { partialEntries++; Log(due, "частичный вход", "Доступна только часть объёма до предела цены; неисполненный остаток отменён.", order.Sign, price, quantity: quantity); }
                    }
                    position = new(order.Sign, due, price, order.Residual, quantity); filled++;
                    Log(due, "вход", counter ? "Контрольный вход против импульса" : "Отставание покрывает заданный порог расходов", order.Sign, price, quantity: depth ? quantity : null);
                }
            }
        }
        foreach (var tick in events)
        {
            ct.ThrowIfCancellationRequested(); lastMs = tick.Ms; Advance(tick.Ms); if (halted) break;
            var exposed = position is not null || pending is not null || resting is not null;
            if (exposed != reportedExposure) { reportedExposure = exposed; observer?.Exposure(exposed); }
            if (position is null && reportedPosition) { reportedPosition = false; observer?.Position(null); }
            if (tick.IsClock) continue;
            if (tick.IsTrade)
            {
                if (tick.Prints is null)
                {
                    // A reconnect or unreadable trade message: volume at our price may have been missed.
                    tradeConnection = tick.Connection;
                    if (resting is not null) { LoseOrder(tick.Ms, resting.Quantity, "Разрыв потока сделок при выставленной заявке maker: исполнение неизвестно."); break; }
                    continue;
                }
                if (tick.Connection != tradeConnection || resting is null) continue;
                var r = resting;
                var through = tick.Prints.Where(p => r.Sign > 0 ? !p.BuyerAggressor && p.Price <= r.Price : p.BuyerAggressor && p.Price >= r.Price)
                    .Sum(p => p.Quantity * instrument!.BasePerUnit);
                if (through == 0) continue;
                resting = r with { Traded = r.Traded + through };
                var done = MakerFilled(resting);
                if (done >= resting.Quantity) OpenMaker(tick.Ms, resting, done, "Заявка maker исполнена: объём сделок по её цене превысил очередь перед ней");
                else if (done > 0 && resting.CancelAt is null)
                {
                    // The bot sees the partial fill and cancels the rest; more can fill while the cancel travels.
                    resting = resting with { CancelAt = tick.Ms };
                    Log(tick.Ms, "отмена запрошена", "Заявка maker исполнена частично; остаток отменяется.", resting.Sign, resting.Price, quantity: done);
                }
                continue;
            }
            if (tick.IsBook)
            {
                if (tick.Connection != bookConnection) continue;
                book.Apply(tick.Book, tick.Ms, instrument!.BasePerUnit);
                if (!book.Valid && position is not null) { LosePosition(tick.Ms); break; }
                continue;
            }
            var previousMid = q[tick.Venue].Current?.Mid; q[tick.Venue].Apply(tick);
            if (tick.Reset)
            {
                if (tick.Venue == 1 - leader) { book.Reset(); bookConnection = tick.Connection; }
                if (position is not null) { LosePosition(tick.Ms); break; }
                if (pending is not null && tick.Venue == 1 - leader) { LoseOrder(tick.Ms, pending.Quantity); break; }
                if (resting is not null && tick.Venue == 1 - leader)
                {
                    // Without the execution venue's prices we cannot know whether the resting order filled.
                    unresolved++; halted = true; Log(tick.Ms, "не оценено", "Разрыв данных при выставленной заявке maker: исполнение неизвестно.", quantity: depth ? resting.Quantity : null);
                    break;
                }
                if (resting is { CancelAt: null }) { resting = resting with { CancelAt = tick.Ms }; Log(tick.Ms, "отмена запрошена", "Разрыв данных поводыря."); }
                continue;
            }
            // Simplified BBO model only: no sizes are known, so a fill is assumed when the opposite best price reaches our level.
            if (!depth && resting is not null && tick.Venue == 1 - leader && tick.Quote is { } own && tick.Ms >= resting.Live
                && (resting.Sign > 0 ? own.Ask <= resting.Price : own.Bid >= resting.Price))
                OpenMaker(tick.Ms, resting, resting.Quantity, "Заявка maker исполнена: встречная цена дошла до её уровня (упрощённо, без объёма)");
            if (tick.Ms < s.CalibrationSeconds * 1000 || !Fresh(tick.Ms)) continue;
            var residual = Residual();
            if (resting is not null)
            {
                if (resting.CancelAt is null && observer?.ExitNow(tick.Ms) == true)
                { resting = resting with { CancelAt = tick.Ms }; Log(tick.Ms, "отмена запрошена", "Остановка пользователем: заявка maker снимается."); }
                else if (resting.CancelAt is null && Math.Sign(resting.Residual) * residual <= s.MinEdgeBps)
                { resting = resting with { CancelAt = tick.Ms }; Log(tick.Ms, "отмена запрошена", "Отставание сократилось до исполнения заявки maker."); }
                continue;
            }
            if (position is not null)
            {
                if (pending is null)
                {
                    var markPrice = Price(position.Sign, true); bool insufficient = false;
                    if (depth)
                    {
                        var estimate = book.Fill(position.Sign < 0, position.Quantity, position.Sign < 0 ? decimal.MaxValue : 0,
                            s.SlippageBps, instrument!.QuantityStep, false);
                        insufficient = estimate.Quantity < position.Quantity;
                        if (!insufficient) markPrice = estimate.Price;
                    }
                    var mark = Net(markPrice); position = position.Seen(markPrice);
                    if (observer is not null)
                    {
                        var held = position.Quantity > 0 ? position.Quantity : (s.OrderQuantity ?? s.OrderNotional / position.Price);
                        observer.Position(new(position.Sign > 0 ? "long" : "short", position.At / 1000, position.Price, position.Quantity,
                            markPrice, mark, mark / 10000 * held * position.Price, false));
                        reportedPosition = true;
                    }
                    var direction = Math.Sign(position.EntryResidual);
                    var reason = position.RetryReason is { } retry ? "Повтор выхода: " + retry
                        : observer?.ExitNow(tick.Ms) == true ? "Остановка пользователем: закрытие позиции"
                        : mark <= -liquidationBps ? $"Риск ликвидации: убыток подошёл к марже позиции при плече {leverage}x"
                        : insufficient ? "Недостаточно видимой ликвидности для полного выхода" : mark <= -s.StopLossBps ? "Ограничение убытка" : direction * residual <= s.MinEdgeBps ? "Отставание сократилось или сменило знак" : null;
                    var prior = q[leader].Before(tick.Ms - s.WindowMs, s.MaxAgeMs);
                    // The reversal threshold is separate from the entry impulse: a low entry threshold need not make exits jumpy.
                    if (reason is null && prior is not null && direction * (q[leader].Current!.Mid / prior.Mid - 1) * 10000 <= -(s.ReversalBps ?? s.ImpulseBps))
                        reason = "Поводырь развернулся";
                    if (reason is not null) { pending = Order(position.Sign, tick.Ms, reason, true); Log(tick.Ms, "выход запрошен", reason); }
                }
                continue;
            }
            if (pending is not null || tick.Venue != leader || tick.Quote?.Mid == previousMid || tick.Ms < nextSignal) continue;
            var baseline = q[leader].Before(tick.Ms - s.WindowMs, s.MaxAgeMs);
            if (baseline is null) continue;
            var impulse = (q[leader].Current!.Mid / baseline.Mid - 1) * 10000;
            if (Math.Abs(impulse) < s.ImpulseBps) continue;
            signals++; nextSignal = tick.Ms + Math.Max(250, s.WindowMs);
            var sign = Math.Sign(impulse);
            if (sign * residual <= Costs()) { skipped++; Log(tick.Ms, "пропуск", "Оставшееся отставание меньше порога расходов и запаса."); continue; }
            // Everything that can stop an entry is decided here, before sending; afterwards only the exchange decides.
            if (depth && !book.Fresh(tick.Ms, s.MaxAgeMs))
            { skipped++; Log(tick.Ms, "пропуск", "Нет свежего восстановленного стакана для исполнения."); continue; }
            if (depth && maker && tradeConnection is null)
            { skipped++; Log(tick.Ms, "пропуск", "Поток сделок ведомой биржи не подключён: исполнение maker-заявки нельзя оценить."); continue; }
            var order = Order(counter ? -sign : sign, tick.Ms, "Импульс и отставание", false) with { Residual = residual };
            if (depth && !SizeAllowed(order.Quantity, maker ? order.Limit : Math.Min(order.Limit, q[1 - leader].Current!.Mid)))
            { skipped++; Log(tick.Ms, "пропуск", "Размер заявки после округления не проходит минимальный номинал или ограничения количества.", quantity: order.Quantity); continue; }
            if (depth && setup.MaxNotional is { } cap && order.Quantity * order.Limit > cap)
            {
                skipped++;
                Log(tick.Ms, "пропуск", leverage > 1 ? $"Размер заявки по предельной цене превышает выделенный боту баланс с плечом {leverage}x."
                    : "Размер заявки по предельной цене превышает выделенный боту баланс.", quantity: order.Quantity);
                continue;
            }
            if (observer?.EntryBlock(tick.Ms) is { } blocked) { skipped++; Log(tick.Ms, "пропуск", blocked); continue; }
            pending = order; sent++; Log(tick.Ms, "вход отправлен", pending.Reason, pending.Sign, depth ? pending.Limit : null, quantity: depth ? pending.Quantity : null);
        }
        var finish = end ?? lastMs;
        if (!halted) Advance(finish);
        if (!halted && position is not null) { unresolved++; Log(finish, "не оценено", "Запись закончилась с открытой позицией."); }
        // No confirmed cancel exists for an order still in flight or resting at the end, so its outcome is unknown.
        else if (!halted && (pending is not null || resting is not null))
        { unresolved++; Log(finish, "не оценено", "Запись закончилась при отправленной или выставленной заявке: исполнение неизвестно.", quantity: depth ? pending?.Quantity ?? resting?.Quantity : null); }
        return new(counter ? "counter" : "follow", delay, trades.Count, trades.Count == 0 ? null : trades.Average(x => x.NetBps),
            trades.Count == 0 ? null : 100.0 * trades.Count(x => x.NetBps > 0) / trades.Count, sum, drawdown, unresolved, skipped, signals,
            trades.Take(500).ToArray(), journal.ToArray(), journalCount > 500 || trades.Count > 500, partialEntries, partialExits,
            depth ? closedNet : null, depth && unresolved > 0 ? position?.Quantity ?? resting?.Quantity ?? pending?.Quantity : null,
            maker ? makerOrders : null, maker ? makerFills : null, sent, filled, unfilled);
    }
}