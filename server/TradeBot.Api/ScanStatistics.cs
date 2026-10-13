namespace TradeBot.Api;

public sealed record ScanGroup(string Name, int Count, int Targets, int Stops, int Timeouts, decimal TargetShare, decimal MeanPct,
    decimal MeanR, decimal SumR);
public sealed record ScanFeature(string Name, ScanGroup With, ScanGroup Without);
public sealed record ScanStats(int Journals, int Unreadable, int Total, int Open, decimal FeePct, ScanGroup All, ScanGroup[] ByScore,
    ScanGroup[] BySide, ScanFeature[] Features, string[] Notes);

// Would the scanner's breakouts have paid, entering every one at the breakout price? Each finished breakout is valued as if it
// exited exactly at its outcome: the target, the stop just behind the level, or the last price when the time ran out, minus the
// round-trip fee. R is that result over the planned risk (distance to the stop plus fees), the unit of risk-based sizing.
// Slippage on entry and stop is not in it, so this is an upper bound that tells whether the idea is worth trading at all.
public static class ScanStatistics
{
    public static readonly (int From, int To)[] ScoreBands = [(0, 39), (40, 59), (60, 79), (80, 100)];
    // Signs that are always written when their data was recorded: a missing one means "no data", not "without".
    // The others are written only when they fired, so their absence means "without".
    public static readonly HashSet<string> NeedData = ["Ликвидации"];

    public static (decimal Pct, decimal R) Value(ScanCandidate c, ScanSettings s, decimal feePct)
    {
        var sign = c.Side == "long" ? 1 : -1;
        var stopPrice = c.Level * (1 - sign * s.StopBufferPct / 100);
        var stopPct = sign * (stopPrice / c.Price - 1) * 100; // negative: the move to the stop from the entry
        var move = c.Result switch { "target" => s.TargetPct, "stop" => stopPct, _ => c.LastPct };
        var risk = Math.Max(-stopPct, 0) + feePct;
        var pct = move - feePct;
        return (pct, risk > 0 ? pct / risk : 0);
    }

    public static ScanStats Compute(IReadOnlyCollection<(ScanCandidate Candidate, ScanSettings Settings)> all, decimal feePct, int journals = 0,
        int unreadable = 0)
    {
        var done = all.Where(x => x.Candidate.Result is "target" or "stop" or "timeout").ToList();
        ScanGroup Group(string name, IEnumerable<(ScanCandidate Candidate, ScanSettings Settings)> items)
        {
            var list = items.ToList();
            var values = list.Select(x => Value(x.Candidate, x.Settings, feePct)).ToList();
            int Count(string result) => list.Count(x => x.Candidate.Result == result);
            return new(name, list.Count, Count("target"), Count("stop"), Count("timeout"),
                list.Count == 0 ? 0 : Math.Round(Count("target") * 100m / list.Count, 1),
                list.Count == 0 ? 0 : Math.Round(values.Average(v => v.Pct), 3),
                list.Count == 0 ? 0 : Math.Round(values.Average(v => v.R), 3),
                Math.Round(values.Sum(v => v.R), 2));
        }
        var names = done.SelectMany(x => x.Candidate.Items.Select(i => i.Name)).Distinct().Order(StringComparer.Ordinal).ToList();
        bool Has(ScanCandidate c, string name) => c.Items.Any(i => i.Name == name && i.Points != 0);
        var notes = new List<string>
        {
            "Результат считается так, будто вход был ровно по цене пробоя, а выход — ровно на цели, на стопе за уровнем или по последней цене, когда время вышло. Проскальзывание входа и стопа не учтено: это верхняя оценка.",
            $"Из каждого результата вычтены комиссии круга {feePct.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}%. R — результат в долях риска сделки (расстояние до стопа + комиссии).",
        };
        if (done.Count < 200)
            notes.Add($"Завершённых пробоев {done.Count}: для выводов нужно хотя бы 200–300, разбивки по признакам пока почти случайны.");
        return new(journals, unreadable, all.Count, all.Count - done.Count, feePct, Group("Все пробои", done),
            ScoreBands.Select(b => Group($"Оценка {b.From}–{b.To}", done.Where(x => x.Candidate.Score >= b.From && x.Candidate.Score <= b.To))).ToArray(),
            [Group("Лонг", done.Where(x => x.Candidate.Side == "long")), Group("Шорт", done.Where(x => x.Candidate.Side == "short"))],
            names.Select(n => new ScanFeature(n, Group("С признаком", done.Where(x => Has(x.Candidate, n))),
                Group("Без признака", done.Where(x => !Has(x.Candidate, n) && (!NeedData.Contains(n) || x.Candidate.Items.Any(i => i.Name == n)))))).ToArray(),
            notes.ToArray());
    }
}
