using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record ReplaySettings(int CalibrationSeconds = 120, decimal ImpulseBps = 3, int WindowMs = 250,
    int MaxAgeMs = 500, decimal FeeBps = 5, decimal SlippageBps = 1, decimal MinEdgeBps = 2,
    decimal StopLossBps = 20, int MaxHoldMs = 2000, decimal LeaderAdvantageBps = .5m,
    string ExecutionModel = "depth", decimal OrderNotional = 10, decimal PriceLimitBps = 5,
    string EntryOrder = "taker", decimal MakerFeeBps = 2, int MakerWaitMs = 1000)
{
    public void Validate()
    {
        new AnalysisSettings(ImpulseBps, WindowMs, 100, MaxHoldMs, MaxAgeMs, FeeBps, SlippageBps).Validate();
        if (CalibrationSeconds is < 30 or > 1800 || MinEdgeBps is < 0 or > 100 || StopLossBps is < 1 or > 1000
            || LeaderAdvantageBps is < 0.1m or > 100)
            throw new ArgumentException("Проверьте параметры стратегии: обучение 30–1800 с, запас 0–100 б.п., стоп 1–1000 б.п., преимущество лидера 0,1–100 б.п.");
        if (ExecutionModel is not ("depth" or "bbo") || OrderNotional is < 1 or > 100000 || PriceLimitBps is < 0 or > 100)
            throw new ArgumentException("Модель исполнения: depth или bbo; номинал заявки 1–100000, предел цены 0–100 б.п.");
        if (EntryOrder is not ("taker" or "maker") || MakerFeeBps is < -10 or > 100 || MakerWaitMs is < 10 or > 60000)
            throw new ArgumentException("Вход: taker или maker; комиссия maker от −10 до 100 б.п., ожидание исполнения 10–60000 мс.");
    }
}
public sealed record ReplayDecision(double Seconds, string Action, string Reason, string? Side = null, decimal? Price = null, decimal? NetBps = null, decimal? Quantity = null);
public sealed record ReplayTrade(double EntrySeconds, double ExitSeconds, string Side, decimal EntryPrice, decimal ExitPrice, decimal NetBps, string ExitReason,
    decimal? Quantity = null, decimal? NetQuote = null, decimal? FeesQuote = null);
public sealed record ReplayScenario(string Mode, int DelayMs, int Trades, decimal? MeanNetBps, double? WinPercent,
    decimal SumNetBps, decimal ClosedTradeDrawdownBps, int UnresolvedPositions, int Skipped, int Signals,
    ReplayTrade[] TradeLog, ReplayDecision[] Journal, bool JournalTruncated, int PartialEntries = 0, int PartialExits = 0,
    decimal? ClosedNetQuote = null, decimal? UnresolvedQuantity = null, int? MakerOrders = null, int? MakerFills = null);
public sealed record ReplayLeader(string SourceExchange, string Exchange, int Samples, decimal? ResponseBps);
public sealed record ReplayReport(string Strategy, string Version, AnalysisRecording Recording, ReplaySettings Settings,
    string Status, string Explanation, string? Leader, string? ExecutionExchange, decimal? NormalBasisBps,
    double DurationSeconds, ReplayLeader[] Candidates, ReplayScenario[] Scenarios, string[] Notes, ReplayInstrument? Instrument = null);

// Pure offline execution: local arrival times only; no API keys, networking, orders or account state.
public static class StrategyReplay
{
    private sealed record Tick(double Ms, int Venue, MarketQuote? Quote, bool Reset, int Sequence, string Connection, bool IsBook = false, ReplayBookUpdate? Book = null,
        bool IsTrade = false, ReplayPrint[]? Prints = null);
    // Quantity is the open remainder; earlier partial exits are kept so the closed trade gets one weighted exit price.
    private sealed record Position(int Sign, double At, decimal Price, decimal EntryResidual, decimal Quantity = 0,
        decimal ExitedQuantity = 0, decimal ExitedValue = 0, string? RetryReason = null, decimal? EntryFeeBps = null);
    // An order already sent: side, size, limit and the signal's lag are fixed at the decision and cannot be revised in flight.
    private sealed record Pending(int Sign, double Due, string Reason, bool Exit, decimal Quantity = 0, decimal Limit = 0, decimal Residual = 0);
    // A post-only entry resting at its own price from Live until a cancel request (CancelAt) reaches the exchange.
    // QueueAhead is the visible size at our price when the order went live; Traded is the aggressor volume at or through our price since.
    private sealed record Resting(int Sign, double Live, decimal Price, decimal Quantity, double Expires, double? CancelAt, decimal Residual,
        decimal QueueAhead = 0, decimal Traded = 0);
    private sealed class Quotes
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
        public void Apply(Tick tick)
        {
            if (tick.Reset) History.Clear();
            Current = tick.Quote; At = tick.Ms;
            if (tick.Quote is not null) History.Add((tick.Ms, tick.Quote));
        }
    }

    public static ReplayReport Run(AnalysisRecording recording, ReplaySettings settings, long frequency, long first, long last,
        List<MarketAnalysis.Input> input, CancellationToken ct, ReplayMarketData? market = null)
    {
        settings.Validate();
        if (frequency <= 0 || frequency > 1_000_000_000_000 || first < 0 || last < first)
            throw new InvalidDataException("Некорректные временные метки записи.");
        var duration = (last - first) / (double)frequency;
        if (duration > 86400) throw new InvalidDataException("Неподдерживаемая длительность записи.");
        var cutoff = checked(first + (long)settings.CalibrationSeconds * frequency);
        var calibration = input.Where(x => x.Ticks < cutoff).ToList();
        var analysis = MarketAnalysis.Analyze(recording, new(settings.ImpulseBps, settings.WindowMs, 100, 100,
            settings.MaxAgeMs, settings.FeeBps, settings.SlippageBps), frequency, first, Math.Min(last, cutoff), calibration.Count, 0, calibration, ct);
        var candidates = analysis.Directions.Select(d => new ReplayLeader(d.SourceExchange, d.Exchange,
            d.Horizons[0].Samples, d.Horizons[0].MeanMoveBps)).ToArray();
        string[] notes = [
            "Исследовательская стратегия v0.3.2: поводырь выбран только на начальном участке и зафиксирован. Это предварительная гипотеза лидерства по реакции через 100 мс, не доказанная причинность.",
            settings.ExecutionModel == "depth"
                ? "Исполнение по видимому стакану: условная немедленная заявка с пределом цены от bid/ask в момент решения. Размер округляется вниз. Цена — средняя по уровням плюс заданное дополнительное проскальзывание; комиссия за обе стороны. Это модель IOC, а не точная эмуляция правил брокера."
                : "Упрощённая модель BBO: последние свежие bid/ask, проскальзывание и комиссия. Объём, минимальная заявка и частичные исполнения не учитываются. Номинал и предел цены в этой модели не используются.",
            "Одна позиция на сценарий. Задержка — время от отправки заявки до её исполнения на бирже, она применяется и к входу, и к выходу. Все условия входа проверяются в момент сигнала; отправленная заявка исполняется на зафиксированных условиях, даже если отставание к тому времени исчезло. Потеря данных ведомой биржи до исполнения даёт неопределённый результат, а не отмену. При потере свежести или разрыве во время позиции сценарий останавливается с неоценённой позицией, без выдуманного закрытия.",
            "В модели стакана сумма и просадка в б.п. нормированы на заданный номинал, средний итог — на фактический объём каждой закрытой сделки. Если выход не уложился в предел цены, остаток закрывается новой заявкой после следующего обновления котировок, а сделка получает среднюю цену всех частей выхода. Если исполнение за записанной глубиной неизвестно, остаток не закрывается выдуманной ценой: сценарий прекращается, вся незавершённая сделка исключена из итогов закрытых сделок.",
            "Используется только записанная глубина, без скрытых заявок. Если исполнение отправленной заявки за последним записанным уровнем неизвестно, результат не определён и сценарий прекращается. Запись не отражает влияние нашего бота на дальнейший рынок. Плечо, маржа, funding, ликвидация, ценовые коридоры биржи и персональные лимиты не моделируются; тариф 5 б.п. — пример.",
            "Контрольный вход против импульса использует те же первоначальные сигналы отбора, но не гарантирует доступного движения в обратную сторону. Сценарии независимы; результаты нельзя складывать.",
            "Выбор лучшей задержки на этой записи требует новой независимой проверки. Положительный результат не подтверждает прибыльность. Живая симуляция и торговое исполнение ещё не подключены.",
            .. (settings.EntryOrder == "maker" ? new[] { "Вход maker (экспериментальная модель): post-only заявка по своей лучшей цене (bid для покупки, ask для продажи) на момент решения, с той же задержкой доставки; отклоняется, если цена уже дошла до её уровня. В модели стакана заявка встаёт в конец очереди своего уровня: исполненный объём = записанные сделки агрессора по её цене или хуже минус видимый объём перед ней, не больше размера заявки. Уход заявок из очереди не учитывается. При частичном исполнении остаток отменяется, и пока отмена идёт, может исполниться ещё часть. В упрощённой модели BBO объём неизвестен: исполнение засчитывается целиком, когда встречная цена дошла до уровня заявки. Разрыв потока сделок, устаревание котировок ведомой биржи или конец записи при выставленной заявке дают неопределённый результат. Выход — taker." } : Array.Empty<string>())
        ];
        ReplayReport Empty(string status, string reason) => new("leader-lag", "0.3.2", recording, settings, status, reason,
            null, null, null, duration, candidates, [], notes);
        if (recording.Status != "completed") return Empty("incomplete", "Нужна запись без ошибок и разрывов: текущая сессия завершена неполно.");
        if (duration <= settings.CalibrationSeconds + settings.MaxHoldMs / 1000.0 + .2)
            return Empty("too-short", "После участка выбора поводыря не осталось достаточно данных для проверки сделок.");
        var best = candidates.OrderByDescending(x => x.ResponseBps ?? decimal.MinValue).First();
        var other = candidates.Single(x => x.SourceExchange != best.SourceExchange);
        if (candidates.Any(x => x.Samples < 10) || best.ResponseBps is null or <= 0 || other.ResponseBps is null
            || best.ResponseBps.Value - other.ResponseBps.Value < settings.LeaderAdvantageBps)
            return Empty("no-leader", "Поводырь не определён: нужны минимум 10 наблюдений в каждом направлении, положительная реакция и достаточное преимущество. Сделки не моделировались.");
        var ticks = Normalize(recording, frequency, first, input, ct);
        var leader = best.SourceExchange == recording.SourceExchange ? 0 : 1;
        var instrument = market?.Instruments[1 - leader];
        if (settings.ExecutionModel == "depth" && (instrument is null || market!.Books.All(x => x.Venue != 1 - leader || x.Update is null)))
            return Empty("no-execution-data", "Для выбранной отстающей биржи нет сохранённых правил размера заявки или глубины стакана. Нужна новая запись либо явно выбранная упрощённая модель BBO.");
        var makerDepth = settings.ExecutionModel == "depth" && settings.EntryOrder == "maker";
        if (makerDepth && market!.Trades?.Any(x => x.Venue == 1 - leader && x.Prints is not null) != true)
            return Empty("no-execution-data", "Для maker-входа по стакану нужны записанные сделки отстающей биржи: по ним считается объём, прошедший через цену заявки. Нужна запись со сделками либо упрощённая модель BBO.");
        var quotes = new[] { new Quotes(), new Quotes() };
        var bases = new List<decimal>(); double sampledAt = -100;
        foreach (var tick in ticks.TakeWhile(t => t.Ms < settings.CalibrationSeconds * 1000))
        {
            ct.ThrowIfCancellationRequested(); quotes[tick.Venue].Apply(tick);
            if (tick.Ms - sampledAt < 100 || quotes.Any(q => !q.Fresh(tick.Ms, settings.MaxAgeMs))) continue;
            bases.Add((quotes[leader].Current!.Mid / quotes[1 - leader].Current!.Mid - 1) * 10000); sampledAt = tick.Ms;
        }
        if (bases.Count < 100) return Empty("no-basis", "Недостаточно совместных свежих котировок для оценки обычной разницы цен.");
        bases.Sort(); var basis = bases[bases.Count / 2];
        if (settings.ExecutionModel == "depth")
        {
            ticks.AddRange(market!.Books.Where(x => x.Venue == 1 - leader && x.Ticks >= first && x.Ticks <= last).Select(x =>
                new Tick((x.Ticks - first) * 1000.0 / frequency, x.Venue, null, false, x.Sequence, x.Connection, true, x.Update)));
            if (makerDepth)
                ticks.AddRange(market.Trades!.Where(x => x.Venue == 1 - leader && x.Ticks >= first && x.Ticks <= last).Select(x =>
                    new Tick((x.Ticks - first) * 1000.0 / frequency, x.Venue, null, false, x.Sequence, x.Connection, IsTrade: true, Prints: x.Prints)));
            ticks = ticks.OrderBy(x => x.Ms).ThenBy(x => x.Sequence).ToList();
        }
        var scenarios = new List<ReplayScenario>();
        foreach (var delay in new[] { 10, 25, 50, 100 })
            foreach (var counter in new[] { false, true }) scenarios.Add(Simulate(ticks, leader, basis, duration * 1000, settings, delay, counter, ct, instrument));
        return new("leader-lag", "0.3.2", recording, settings, "evaluated", "Поводырь выбран на начальном участке. Результаты ниже относятся только к последующей части записи.",
            best.SourceExchange, best.Exchange, basis, duration, candidates, scenarios.ToArray(), notes, settings.ExecutionModel == "depth" ? instrument : null);
    }

    private static List<Tick> Normalize(AnalysisRecording recording, long frequency, long first, List<MarketAnalysis.Input> input, CancellationToken ct)
    {
        string[] ids = [RecorderOptions.QuoteFeedId(recording.SourceExchange), RecorderOptions.QuoteFeedId(recording.Exchange)];
        string?[] connections = [null, null]; MarketQuote?[] latest = [null, null]; var result = new List<Tick>();
        foreach (var item in input.OrderBy(x => x.Ticks).ThenBy(x => x.Sequence))
        {
            ct.ThrowIfCancellationRequested(); var venue = Array.IndexOf(ids, item.Source); if (venue < 0) continue;
            var at = (item.Ticks - first) * 1000.0 / frequency;
            if (item.Kind is "connecting" or "connected" or "gap")
            { connections[venue] = item.Connection; latest[venue] = null; result.Add(new(at, venue, null, true, item.Sequence, item.Connection)); continue; }
            if (connections[venue] != item.Connection) continue;
            if (item.Quote is null) { latest[venue] = null; result.Add(new(at, venue, null, true, item.Sequence, item.Connection)); continue; }
            if (latest[venue] is { } old && old.SourceOrder > item.Quote.SourceOrder) continue;
            latest[venue] = item.Quote; result.Add(new(at, venue, item.Quote, false, item.Sequence, item.Connection));
        }
        return result;
    }

    private static ReplayScenario Simulate(List<Tick> ticks, int leader, decimal basis, double end, ReplaySettings s, int delay, bool counter, CancellationToken ct, ReplayInstrument? instrument)
    {
        var q = new[] { new Quotes(), new Quotes() }; var journal = new List<ReplayDecision>(); var trades = new List<ReplayTrade>();
        var depth = s.ExecutionModel == "depth"; var book = new ReplayBook(); string? bookConnection = null, tradeConnection = null;
        Position? position = null; Pending? pending = null; int skipped = 0, signals = 0, unresolved = 0, journalCount = 0;
        int partialEntries = 0, partialExits = 0, makerOrders = 0, makerFills = 0;
        var maker = s.EntryOrder == "maker"; Resting? resting = null;
        decimal sum = 0, peak = 0, drawdown = 0, closedNet = 0; double nextSignal = s.CalibrationSeconds * 1000; bool halted = false;
        void Log(double at, string action, string reason, int sign = 0, decimal? price = null, decimal? net = null, decimal? quantity = null)
        { journalCount++; if (journal.Count < 500) journal.Add(new(at / 1000, action, reason, sign == 0 ? null : sign > 0 ? "long" : "short", price, net, quantity)); }
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
                return new(sign, at + delay, reason, false, depth ? instrument!.RoundQuantity(s.OrderNotional / own) : 0, own);
            }
            if (!depth) return new(sign, at + delay, reason, exit);
            var buy = exit ? sign < 0 : sign > 0; var quote = q[1 - leader].Current!;
            var reference = buy ? quote.Ask : quote.Bid;
            var limit = instrument!.Limit(reference, buy, s.PriceLimitBps);
            // For buys, size against the worst allowed price so the configured nominal is a ceiling.
            var quantity = exit ? position!.Quantity : limit <= 0 ? 0 : instrument.RoundQuantity(s.OrderNotional / (buy ? limit : reference));
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
            makerFills++; if (quantity < r.Quantity) partialEntries++;
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
                    else { resting = null; skipped++; Log(due, "пропуск", "Заявка maker не исполнилась и снята."); }
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
                    var net = Net(price);
                    decimal? fees = depth ? total * (position!.Price * (position.EntryFeeBps ?? s.FeeBps) + price * s.FeeBps) / 10000 : null;
                    decimal? cash = depth ? total * position!.Sign * (price - position.Price) - fees : null;
                    trades.Add(new(position!.At / 1000, due / 1000, order.Sign > 0 ? "long" : "short", position.Price, price, net, order.Reason,
                        depth ? total : null, cash, fees));
                    closedNet += cash ?? 0;
                    sum += depth ? cash!.Value / s.OrderNotional * 10000 : net; peak = Math.Max(peak, sum); drawdown = Math.Max(drawdown, peak - sum);
                    Log(due, "выход", order.Reason, order.Sign, price, net, depth ? total : null); position = null;
                }
                else
                {
                    // No second look at the lag here: the order left at the signal and executes on the conditions fixed then.
                    if (maker)
                    {
                        var quote = q[1 - leader].Current!;
                        if (order.Sign > 0 ? quote.Ask <= order.Limit : quote.Bid >= order.Limit)
                        { skipped++; Log(due, "пропуск", "Post-only заявка отклонена: цена уже дошла до её уровня, и она исполнилась бы как taker."); continue; }
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
                        { skipped++; Log(due, "пропуск", "В пределах цены заявки нет доступного объёма."); continue; }
                        book.Fill(order.Sign > 0, order.Quantity, order.Limit, s.SlippageBps, instrument.QuantityStep, true);
                        price = fill.Price; quantity = fill.Quantity;
                        if (quantity < order.Quantity) { partialEntries++; Log(due, "частичный вход", "Доступна только часть объёма до предела цены; неисполненный остаток отменён.", order.Sign, price, quantity: quantity); }
                    }
                    position = new(order.Sign, due, price, order.Residual, quantity);
                    Log(due, "вход", counter ? "Контрольный вход против импульса" : "Отставание покрывает заданный порог расходов", order.Sign, price, quantity: depth ? quantity : null);
                }
            }
        }
        foreach (var tick in ticks)
        {
            ct.ThrowIfCancellationRequested(); Advance(tick.Ms); if (halted) break;
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
                var filled = MakerFilled(resting);
                if (filled >= resting.Quantity) OpenMaker(tick.Ms, resting, filled, "Заявка maker исполнена: объём сделок по её цене превысил очередь перед ней");
                else if (filled > 0 && resting.CancelAt is null)
                {
                    // The bot sees the partial fill and cancels the rest; more can fill while the cancel travels.
                    resting = resting with { CancelAt = tick.Ms };
                    Log(tick.Ms, "отмена запрошена", "Заявка maker исполнена частично; остаток отменяется.", resting.Sign, resting.Price, quantity: filled);
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
                if (resting.CancelAt is null && Math.Sign(resting.Residual) * residual <= s.MinEdgeBps)
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
                    var mark = Net(markPrice);
                    var direction = Math.Sign(position.EntryResidual);
                    var reason = position.RetryReason is { } retry ? "Повтор выхода: " + retry
                        : insufficient ? "Недостаточно видимой ликвидности для полного выхода" : mark <= -s.StopLossBps ? "Ограничение убытка" : direction * residual <= s.MinEdgeBps ? "Отставание сократилось или сменило знак" : null;
                    var prior = q[leader].Before(tick.Ms - s.WindowMs, s.MaxAgeMs);
                    if (reason is null && prior is not null && direction * (q[leader].Current!.Mid / prior.Mid - 1) * 10000 <= -s.ImpulseBps)
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
            pending = order; Log(tick.Ms, "вход отправлен", pending.Reason, pending.Sign, depth ? pending.Limit : null, quantity: depth ? pending.Quantity : null);
        }
        if (!halted) Advance(end);
        if (!halted && position is not null) { unresolved++; Log(end, "не оценено", "Запись закончилась с открытой позицией."); }
        // No confirmed cancel exists for an order still in flight or resting at the end, so its outcome is unknown.
        else if (!halted && (pending is not null || resting is not null))
        { unresolved++; Log(end, "не оценено", "Запись закончилась при отправленной или выставленной заявке: исполнение неизвестно.", quantity: depth ? pending?.Quantity ?? resting?.Quantity : null); }
        return new(counter ? "counter" : "follow", delay, trades.Count, trades.Count == 0 ? null : trades.Average(x => x.NetBps),
            trades.Count == 0 ? null : 100.0 * trades.Count(x => x.NetBps > 0) / trades.Count, sum, drawdown, unresolved, skipped, signals,
            trades.Take(500).ToArray(), journal.ToArray(), journalCount > 500 || trades.Count > 500, partialEntries, partialExits,
            depth ? closedNet : null, depth && unresolved > 0 ? position?.Quantity ?? resting?.Quantity ?? pending?.Quantity : null,
            maker ? makerOrders : null, maker ? makerFills : null);
    }
}
