namespace TradeBot.Api;

public sealed record SeriesRequest(string[] Ids, ReplaySettings Settings);
public sealed record SeriesScenario(string Mode, int DelayMs, int OrdersSent, int EntriesFilled, int OrdersUnfilled, int Trades,
    int Unresolved, decimal SumNetBps, decimal ClosedTradeDrawdownBps, decimal? ClosedNetQuote);
public sealed record SeriesRecording(string Id, string? Coin, string? SourceExchange, string? Exchange, DateTimeOffset? StartedUtc,
    string Status, string Explanation, string? Leader, string? ExecutionExchange, SeriesScenario[] Scenarios);
public sealed record SeriesTotal(string Mode, int DelayMs, int Recordings, int RecordingsPositive, int OrdersSent, int EntriesFilled,
    int OrdersUnfilled, int Trades, int Unresolved, decimal SumNetBps, decimal? MeanNetBps, double? WinPercent, decimal DrawdownBps,
    decimal? ClosedNetQuote, bool DrawdownFromTruncatedLog);
public sealed record SeriesReport(string Strategy, string Version, DateTimeOffset CreatedUtc, ReplaySettings Settings,
    SeriesRecording[] Recordings, SeriesTotal[] Totals, string[] Notes);

public static class StrategySeries
{
    public const int MaxRecordings = 30;

    public static void Validate(SeriesRequest request)
    {
        if (request.Ids is null || request.Settings is null || request.Ids.Length is < 1 or > MaxRecordings || request.Ids.Distinct().Count() != request.Ids.Length)
            throw new ArgumentException($"Выберите от 1 до {MaxRecordings} разных записей.");
        request.Settings.Validate();
    }

    public static SeriesRecording Failed(string id, string reason) => new(id, null, null, null, null, "error", reason, null, null, []);

    public static SeriesRecording Row(ReplayReport report) => new(report.Recording.Id, report.Recording.Coin, report.Recording.SourceExchange,
        report.Recording.Exchange, report.Recording.StartedUtc, report.Status, report.Explanation, report.Leader, report.ExecutionExchange,
        report.Scenarios.Select(s => new SeriesScenario(s.Mode, s.DelayMs, s.OrdersSent ?? 0, s.EntriesFilled ?? 0, s.OrdersUnfilled ?? 0,
            s.Trades, s.UnresolvedPositions, s.SumNetBps, s.ClosedTradeDrawdownBps, s.ClosedNetQuote)).ToArray());

    // Totals only combine recordings evaluated with the same settings; drawdown runs over closed trades in recording order.
    public static SeriesReport Build(ReplaySettings settings, IReadOnlyList<(SeriesRecording Row, ReplayReport? Report)> items, DateTimeOffset now)
    {
        var evaluated = items.Where(x => x.Report?.Status == "evaluated").OrderBy(x => x.Report!.Recording.StartedUtc).ToList();
        var depth = settings.ExecutionModel == "depth";
        var totals = evaluated.SelectMany(x => x.Report!.Scenarios).Select(s => (s.Mode, s.DelayMs)).Distinct()
            .OrderBy(x => x.Mode == "follow" ? 0 : 1).ThenBy(x => x.DelayMs).Select(key =>
            {
                var scenarios = evaluated.Select(x => x.Report!.Scenarios.Single(s => s.Mode == key.Mode && s.DelayMs == key.DelayMs)).ToList();
                var trades = scenarios.Sum(s => s.Trades);
                decimal sum = 0, peak = 0, drawdown = 0;
                foreach (var trade in scenarios.SelectMany(s => s.TradeLog))
                {
                    sum += depth && trade.NetQuote is { } cash ? cash / settings.OrderNotional * 10000 : trade.NetBps;
                    peak = Math.Max(peak, sum); drawdown = Math.Max(drawdown, peak - sum);
                }
                return new SeriesTotal(key.Mode, key.DelayMs, scenarios.Count, scenarios.Count(s => s.SumNetBps > 0),
                    scenarios.Sum(s => s.OrdersSent ?? 0), scenarios.Sum(s => s.EntriesFilled ?? 0), scenarios.Sum(s => s.OrdersUnfilled ?? 0),
                    trades, scenarios.Sum(s => s.UnresolvedPositions), scenarios.Sum(s => s.SumNetBps),
                    trades == 0 ? null : scenarios.Sum(s => (s.MeanNetBps ?? 0) * s.Trades) / trades,
                    trades == 0 ? null : scenarios.Sum(s => (s.WinPercent ?? 0) * s.Trades) / trades,
                    drawdown, depth ? scenarios.Sum(s => s.ClosedNetQuote ?? 0) : null, scenarios.Any(s => s.Trades > s.TradeLog.Length));
            }).ToArray();
        string[] notes = [
            "Все записи пересчитаны заново с одними и теми же параметрами; результат каждой записи также сохранён рядом с ней.",
            "Итоги складывают только записи со статусом «проверено». Записи без поводыря, слишком короткие или с ошибкой показаны отдельно и в итоги не входят.",
            "Просадка считается по закрытым сделкам всех записей подряд, в порядке времени записи. Неопределённые результаты в денежный итог не входят: если их много, итог неполный.",
            "Поводырь выбирается в каждой записи заново на её начальном участке. Ни одна площадка не закреплена как источник сигнала.",
            "Устойчивость важнее лучшей строки: смотрите, в скольких записях сценарий в плюсе, а не только на сумму. Выбор лучшего сценария по этой таблице требует проверки на новых записях."
        ];
        return new("leader-lag", StrategyReplay.Version, now, settings,
            items.Select(x => x.Row).ToArray(), totals, notes);
    }
}
