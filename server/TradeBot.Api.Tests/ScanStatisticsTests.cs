using TradeBot.Api;
using Xunit;

namespace TradeBot.Api.Tests;

public class ScanStatisticsTests
{
    private static readonly ScanSettings Settings = new();

    private static ScanCandidate Candidate(string id, string side, decimal price, string result, int score, decimal last = 0, bool print = false) =>
        new(id, "XUSDT", side, 0, 100, price, 2, score,
            [new("Касания", 10, ""), .. print ? new[] { new ScoreItem("Крупный принт", 10, "") } : []], result, 0, 0, last, null);

    [Fact]
    public void EachOutcomeIsValuedAtItsExitAfterFeesAndInUnitsOfRisk()
    {
        // Long from 100.06 over a level at 100: the stop is 0.15% behind the level, at 99.85.
        var stopPct = (99.85m / 100.06m - 1) * 100;
        var risk = -stopPct + .1m;
        var target = ScanStatistics.Value(Candidate("a", "long", 100.06m, "target", 70), Settings, .1m);
        Assert.Equal(1.4m, target.Pct);
        Assert.Equal(1.4m / risk, target.R, 10);
        var stop = ScanStatistics.Value(Candidate("b", "long", 100.06m, "stop", 50), Settings, .1m);
        Assert.Equal(-1m, stop.R, 10);
        // A short below a support at 100: the stop is above it, at 100.15; a timeout keeps the last move.
        var timeout = ScanStatistics.Value(Candidate("c", "short", 99.94m, "timeout", 85, last: .3m), Settings, .1m);
        Assert.Equal(.2m, timeout.Pct);
        Assert.Equal(.2m / ((100.15m / 99.94m - 1) * 100 + .1m), timeout.R, 10);
    }

    [Fact]
    public void GroupsSplitByScoreSideAndFeatureAndSkipUnfinishedBreakouts()
    {
        var stats = ScanStatistics.Compute([
            (Candidate("a", "long", 100.06m, "target", 70, print: true), Settings),
            (Candidate("b", "long", 100.06m, "stop", 50), Settings),
            (Candidate("c", "short", 99.94m, "timeout", 85, last: .3m), Settings),
            (Candidate("d", "long", 100.06m, "open", 90), Settings)], .1m, journals: 2);
        Assert.Equal((4, 1, 2), (stats.Total, stats.Open, stats.Journals));
        Assert.Equal((3, 1, 1, 1, 33.3m), (stats.All.Count, stats.All.Targets, stats.All.Stops, stats.All.Timeouts, stats.All.TargetShare));
        Assert.Equal([0, 1, 1, 1], stats.ByScore.Select(x => x.Count));
        Assert.Equal((2, 1), (stats.BySide[0].Count, stats.BySide[1].Count));
        var print = stats.Features.Single(f => f.Name == "Крупный принт");
        Assert.Equal((1, 1, 2, 0), (print.With.Count, print.With.Targets, print.Without.Count, print.Without.Targets));
        Assert.Contains(stats.Notes, n => n.Contains("Завершённых пробоев 3"));
        // Liquidations are known only where the stream was recorded: candidates without the sign are left out of "without".
        var withLiquidations = ScanStatistics.Compute([
            (Candidate("a", "long", 100.06m, "target", 70) with { Items = [new("Ликвидации", 10, "")] }, Settings),
            (Candidate("b", "long", 100.06m, "stop", 50) with { Items = [new("Ликвидации", 0, "")] }, Settings),
            (Candidate("c", "long", 100.06m, "stop", 50), Settings)], .1m).Features.Single(f => f.Name == "Ликвидации");
        Assert.Equal((1, 1), (withLiquidations.With.Count, withLiquidations.Without.Count));
        Assert.Equal(Math.Round(new[] { "a", "b", "c" }.Select((id, i) => ScanStatistics.Value(
            Candidate(id, i == 2 ? "short" : "long", i == 2 ? 99.94m : 100.06m, new[] { "target", "stop", "timeout" }[i], 0, last: .3m), Settings, .1m).R).Sum(), 2),
            stats.All.SumR);
    }
}
