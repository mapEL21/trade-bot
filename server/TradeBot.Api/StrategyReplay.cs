using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record ReplaySettings(int CalibrationSeconds = 120, decimal ImpulseBps = 3, int WindowMs = 250,
    int MaxAgeMs = 500, decimal FeeBps = 5, decimal SlippageBps = 1, decimal MinEdgeBps = 2,
    decimal StopLossBps = 20, int MaxHoldMs = 2000, decimal LeaderAdvantageBps = .5m)
{
    public void Validate()
    {
        new AnalysisSettings(ImpulseBps, WindowMs, 100, MaxHoldMs, MaxAgeMs, FeeBps, SlippageBps).Validate();
        if (CalibrationSeconds is < 30 or > 1800 || MinEdgeBps is < 0 or > 100 || StopLossBps is < 1 or > 1000
            || LeaderAdvantageBps is < 0.1m or > 100)
            throw new ArgumentException("Проверьте параметры стратегии: обучение 30–1800 с, запас 0–100 б.п., стоп 1–1000 б.п., преимущество лидера 0,1–100 б.п.");
    }
}
public sealed record ReplayDecision(double Seconds, string Action, string Reason, string? Side = null, decimal? Price = null, decimal? NetBps = null);
public sealed record ReplayTrade(double EntrySeconds, double ExitSeconds, string Side, decimal EntryPrice, decimal ExitPrice, decimal NetBps, string ExitReason);
public sealed record ReplayScenario(string Mode, int DelayMs, int Trades, decimal? MeanNetBps, double? WinPercent,
    decimal SumNetBps, decimal ClosedTradeDrawdownBps, int UnresolvedPositions, int Skipped, int Signals,
    ReplayTrade[] TradeLog, ReplayDecision[] Journal, bool JournalTruncated);
public sealed record ReplayLeader(string SourceExchange, string Exchange, int Samples, decimal? ResponseBps);
public sealed record ReplayReport(string Strategy, string Version, AnalysisRecording Recording, ReplaySettings Settings,
    string Status, string Explanation, string? Leader, string? ExecutionExchange, decimal? NormalBasisBps,
    double DurationSeconds, ReplayLeader[] Candidates, ReplayScenario[] Scenarios, string[] Notes);

// Pure offline execution: local arrival times only; no API keys, networking, orders or account state.
public static class StrategyReplay
{
    private sealed record Tick(double Ms, int Venue, MarketQuote? Quote, bool Reset);
    private sealed record Position(int Sign, double At, decimal Price, decimal EntryResidual);
    private sealed record Pending(int Sign, double Due, string Reason, bool Exit);
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
        List<MarketAnalysis.Input> input, CancellationToken ct)
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
            "Исследовательская стратегия v0.1: поводырь выбран только на начальном участке и зафиксирован. Это предварительная гипотеза лидерства по реакции через 100 мс, не доказанная причинность.",
            "Исполнение условное: последние свежие bid/ask к моменту заявки, заданное проскальзывание и комиссия за обе стороны. Объём, очередь, частичные исполнения и funding не моделируются; тариф 5 б.п. — пример.",
            "Одна позиция на сценарий. Задержка применяется и к входу, и к выходу. При потере свежести или разрыве во время позиции сценарий останавливается с неоценённой позицией, без выдуманного закрытия.",
            "Сумма и просадка выражены в б.п. при одинаковом номинале каждой сделки. Просадка рассчитана по закрытым сделкам, без переоценки открытой позиции; это не доходность счёта.",
            "Контрольный вход против импульса использует те же первоначальные сигналы отбора, но не гарантирует доступного движения в обратную сторону. Сценарии независимы; результаты нельзя складывать.",
            "Выбор лучшей задержки на этой записи требует новой независимой проверки. Положительный результат не подтверждает прибыльность. Живая симуляция и торговое исполнение ещё не подключены."
        ];
        ReplayReport Empty(string status, string reason) => new("leader-lag", "0.1.0", recording, settings, status, reason,
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
        var scenarios = new List<ReplayScenario>();
        foreach (var delay in new[] { 10, 25, 50, 100 })
            foreach (var counter in new[] { false, true }) scenarios.Add(Simulate(ticks, leader, basis, duration * 1000, settings, delay, counter, ct));
        return new("leader-lag", "0.1.0", recording, settings, "evaluated", "Поводырь выбран на начальном участке. Результаты ниже относятся только к последующей части записи.",
            best.SourceExchange, best.Exchange, basis, duration, candidates, scenarios.ToArray(), notes);
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
            { connections[venue] = item.Connection; latest[venue] = null; result.Add(new(at, venue, null, true)); continue; }
            if (connections[venue] != item.Connection) continue;
            if (item.Quote is null) { latest[venue] = null; result.Add(new(at, venue, null, true)); continue; }
            if (latest[venue] is { } old && old.SourceOrder > item.Quote.SourceOrder) continue;
            latest[venue] = item.Quote; result.Add(new(at, venue, item.Quote, false));
        }
        return result;
    }

    private static ReplayScenario Simulate(List<Tick> ticks, int leader, decimal basis, double end, ReplaySettings s, int delay, bool counter, CancellationToken ct)
    {
        var q = new[] { new Quotes(), new Quotes() }; var journal = new List<ReplayDecision>(); var trades = new List<ReplayTrade>();
        Position? position = null; Pending? pending = null; int skipped = 0, signals = 0, unresolved = 0, journalCount = 0;
        decimal sum = 0, peak = 0, drawdown = 0; double nextSignal = s.CalibrationSeconds * 1000; bool halted = false;
        void Log(double at, string action, string reason, int sign = 0, decimal? price = null, decimal? net = null)
        { journalCount++; if (journal.Count < 500) journal.Add(new(at / 1000, action, reason, sign == 0 ? null : sign > 0 ? "long" : "short", price, net)); }
        bool Fresh(double at) => q.All(x => x.Fresh(at, s.MaxAgeMs));
        decimal Residual() => (q[leader].Current!.Mid / q[1 - leader].Current!.Mid - 1) * 10000 - basis;
        decimal Price(int sign, bool exit)
        { var buy = exit ? sign < 0 : sign > 0; var quote = q[1 - leader].Current!; return buy ? quote.Ask * (1 + s.SlippageBps / 10000) : quote.Bid * (1 - s.SlippageBps / 10000); }
        decimal Net(decimal exit) => position!.Sign * (exit / position.Price - 1) * 10000 - s.FeeBps * (1 + exit / position.Price);
        void LosePosition(double at)
        { unresolved++; halted = true; pending = null; Log(at, "не оценено", "Потеря данных при открытой позиции: сценарий остановлен."); }
        void Advance(double to)
        {
            while (!halted)
            {
                var due = pending?.Due ?? (position is not null ? position.At + s.MaxHoldMs : double.PositiveInfinity);
                var staleAt = position is null ? double.PositiveInfinity : Math.Min(q[0].At, q[1].At) + s.MaxAgeMs + .001;
                if (staleAt <= to && staleAt < due) { LosePosition(staleAt); break; }
                if (due > to) break;
                if (pending is null) { pending = new(position!.Sign, due + delay, "Лимит времени удержания", true); Log(due, "выход запрошен", pending.Reason); continue; }
                var order = pending; pending = null;
                if (!Fresh(due))
                { if (position is not null) LosePosition(due); else { skipped++; Log(due, "пропуск", "К моменту входа котировки устарели."); } continue; }
                if (order.Exit)
                {
                    var price = Price(order.Sign, true); var net = Net(price);
                    trades.Add(new(position!.At / 1000, due / 1000, order.Sign > 0 ? "long" : "short", position.Price, price, net, order.Reason));
                    sum += net; peak = Math.Max(peak, sum); drawdown = Math.Max(drawdown, peak - sum);
                    Log(due, "выход", order.Reason, order.Sign, price, net); position = null;
                }
                else
                {
                    var direction = counter ? -order.Sign : order.Sign;
                    var costs = 2 * (s.FeeBps + s.SlippageBps) + q[1 - leader].Current!.SpreadBps + s.MinEdgeBps;
                    if (direction * Residual() <= costs) { skipped++; Log(due, "пропуск", "Отставание исчезло до исполнения."); continue; }
                    var price = Price(order.Sign, false); position = new(order.Sign, due, price, Residual());
                    Log(due, "вход", counter ? "Контрольный вход против импульса" : "Отставание покрывает заданный порог расходов", order.Sign, price);
                }
            }
        }
        foreach (var tick in ticks)
        {
            ct.ThrowIfCancellationRequested(); Advance(tick.Ms); if (halted) break;
            var previousMid = q[tick.Venue].Current?.Mid; q[tick.Venue].Apply(tick);
            if (tick.Reset)
            {
                if (position is not null) { LosePosition(tick.Ms); break; }
                if (pending is not null) { pending = null; skipped++; Log(tick.Ms, "пропуск", "Разрыв данных до исполнения."); }
                continue;
            }
            if (tick.Ms < s.CalibrationSeconds * 1000 || !Fresh(tick.Ms)) continue;
            var residual = Residual();
            if (position is not null)
            {
                if (pending is null)
                {
                    var mark = Net(Price(position.Sign, true));
                    var direction = Math.Sign(position.EntryResidual);
                    var reason = mark <= -s.StopLossBps ? "Ограничение убытка" : direction * residual <= s.MinEdgeBps ? "Отставание сократилось или сменило знак" : null;
                    var prior = q[leader].Before(tick.Ms - s.WindowMs, s.MaxAgeMs);
                    if (reason is null && prior is not null && direction * (q[leader].Current!.Mid / prior.Mid - 1) * 10000 <= -s.ImpulseBps)
                        reason = "Поводырь развернулся";
                    if (reason is not null) { pending = new(position.Sign, tick.Ms + delay, reason, true); Log(tick.Ms, "выход запрошен", reason); }
                }
                continue;
            }
            if (pending is not null || tick.Venue != leader || tick.Quote?.Mid == previousMid || tick.Ms < nextSignal) continue;
            var baseline = q[leader].Before(tick.Ms - s.WindowMs, s.MaxAgeMs);
            if (baseline is null) continue;
            var impulse = (q[leader].Current!.Mid / baseline.Mid - 1) * 10000;
            if (Math.Abs(impulse) < s.ImpulseBps) continue;
            signals++; nextSignal = tick.Ms + Math.Max(250, s.WindowMs);
            var sign = Math.Sign(impulse); var threshold = 2 * (s.FeeBps + s.SlippageBps) + q[1 - leader].Current!.SpreadBps + s.MinEdgeBps;
            if (sign * residual <= threshold) { skipped++; Log(tick.Ms, "пропуск", "Оставшееся отставание меньше порога расходов и запаса."); continue; }
            pending = new(counter ? -sign : sign, tick.Ms + delay, "Импульс и отставание", false);
            Log(tick.Ms, "вход запрошен", pending.Reason, pending.Sign);
        }
        if (!halted) Advance(end);
        if (!halted && position is not null) { unresolved++; Log(end, "не оценено", "Запись закончилась с открытой позицией."); }
        else if (!halted && pending is not null) { skipped++; Log(end, "пропуск", "Запись закончилась до входа."); }
        return new(counter ? "counter" : "follow", delay, trades.Count, trades.Count == 0 ? null : sum / trades.Count,
            trades.Count == 0 ? null : 100.0 * trades.Count(x => x.NetBps > 0) / trades.Count, sum, drawdown, unresolved, skipped, signals,
            trades.Take(500).ToArray(), journal.ToArray(), journalCount > 500 || trades.Count > 500);
    }
}
