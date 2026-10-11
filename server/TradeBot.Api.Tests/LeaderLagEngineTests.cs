using TradeBot.Api;
using Xunit;

namespace TradeBot.Api.Tests;

public class LeaderLagEngineTests
{
    private static readonly AnalysisRecording Recording = new("fixture", "SOL", "hyperliquid", "binance", DateTimeOffset.UnixEpoch, "completed", 0, false);
    private static readonly ReplaySettings Settings = new(FeeBps: 1, SlippageBps: 0, MinEdgeBps: 1, ExecutionModel: "bbo");

    private static List<EngineEvent> Events()
    {
        var normalizer = new QuoteNormalizer("hyperliquid", "binance");
        return StrategyReplayTests.Data().OrderBy(x => x.Ticks).ThenBy(x => x.Sequence)
            .Select(x => normalizer.Push(x, x.Ticks)).OfType<EngineEvent>().ToList();
    }

    // A live session interleaves clock ticks and stops at its last event; neither may change any decision.
    [Theory]
    [InlineData(10, false)] [InlineData(100, true)]
    public void ClockTicksAndStreamingInputGiveTheSameResultAsReplay(int delay, bool counter)
    {
        var report = StrategyReplay.Run(Recording, Settings, 1000, 0, 125000, StrategyReplayTests.Data(), default);
        var expected = report.Scenarios.Single(x => x.DelayMs == delay && x.Mode == (counter ? "counter" : "follow"));
        var leader = report.Leader == "hyperliquid" ? 0 : 1;
        var events = Events();
        var withClock = events.SelectMany(x => new[] { x with { IsClock = true, Quote = null }, x })
            .Concat(Enumerable.Range(1, 30).Select(i => new EngineEvent(125000 + i * 5, 0, null, false, 0, "", IsClock: true)))
            .ToList();
        var setup = new EngineSetup(leader, report.NormalBasisBps!.Value, Settings, delay, counter, null);
        var live = LeaderLagEngine.Run(Stream(withClock), setup, default, 125000);
        Assert.Equal(expected.TradeLog, live.TradeLog);
        Assert.Equal(expected.Journal, live.Journal);
        Assert.Equal(expected.SumNetBps, live.SumNetBps);
        // Without an explicit end the session ends at its last event.
        Assert.Equal(expected.TradeLog, LeaderLagEngine.Run(events, setup, default).TradeLog);
    }

    [Fact]
    public void CalibrationHelpersMatchTheReplayReport()
    {
        var report = StrategyReplay.Run(Recording, Settings, 1000, 0, 125000, StrategyReplayTests.Data(), default);
        Assert.Equal(report.Leader, StrategyReplay.SelectLeader(report.Candidates, Settings)!.SourceExchange);
        Assert.Equal(report.NormalBasisBps, StrategyReplay.NormalBasis(Events(), report.Leader == "hyperliquid" ? 0 : 1, Settings, default));
        Assert.Null(StrategyReplay.SelectLeader(report.Candidates, Settings with { LeaderAdvantageBps = 100 }));
    }

    // Consumed lazily, as a live channel would deliver it.
    private static IEnumerable<EngineEvent> Stream(IEnumerable<EngineEvent> source) { foreach (var x in source) yield return x; }
}
