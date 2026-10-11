using TradeBot.Api;
using Xunit;

namespace TradeBot.Api.Tests;

public class StrategyReplayTests
{
    private static readonly AnalysisRecording Recording = new("fixture", "SOL", "hyperliquid", "binance", DateTimeOffset.UnixEpoch, "completed", 0, false);
    private static readonly ReplaySettings Settings = new(FeeBps: 1, SlippageBps: 0, MinEdgeBps: 1, ExecutionModel: "bbo");
    internal static List<MarketAnalysis.Input> Data()
    {
        List<MarketAnalysis.Input> input = [new(0, -2, "hyperliquid", "c", "connected", null), new(0, -1, "binance-public", "c", "connected", null)];
        for (var ms = 0; ms <= 125000; ms += 50)
        {
            decimal source = 100, target = 100;
            for (var impulse = 1250; impulse < 120000; impulse += 6000)
            { if (ms >= impulse) source++; if (ms >= impulse + 50) target++; }
            if (ms >= 120250) source++; if (ms >= 120500) target++;
            void Add(string venue, decimal mid, int sequence) => input.Add(new(ms, sequence, venue, "c", "market",
                new(mid - .01m, mid + .01m, 100, 100, ms, DateTimeOffset.UnixEpoch.AddMilliseconds(ms), ms)));
            Add("hyperliquid", source, ms * 2); Add("binance-public", target, ms * 2 + 1);
        }
        return input;
    }
    private static ReplayReport Run(List<MarketAnalysis.Input> input, long end = 125000) => StrategyReplay.Run(Recording, Settings, 1000, 0, end, input.Where(x => x.Ticks <= end).ToList(), default);

    [Fact]
    public void ChoosesLeaderOnCalibrationAndTradesOnlyLaterWithBothExecutionDelays()
    {
        var report = Run(Data());
        Assert.Equal("evaluated", report.Status); Assert.Equal("hyperliquid", report.Leader);
        Assert.Equal(8, report.Scenarios.Length);
        foreach (var scenario in report.Scenarios)
        {
            var trade = Assert.Single(scenario.TradeLog);
            Assert.Equal(120.25 + scenario.DelayMs / 1000.0, trade.EntrySeconds, 6);
            Assert.Equal(120.5 + scenario.DelayMs / 1000.0, trade.ExitSeconds, 6);
            Assert.Equal(0, scenario.UnresolvedPositions);
            Assert.True(scenario.Mode == "follow" ? trade.NetBps > 0 : trade.NetBps < 0);
            var expectedEntry = scenario.Mode == "follow" ? 120.01m : 119.99m;
            var expectedExit = scenario.Mode == "follow" ? 120.99m : 121.01m;
            var sign = scenario.Mode == "follow" ? 1 : -1;
            Assert.Equal(expectedEntry, trade.EntryPrice); Assert.Equal(expectedExit, trade.ExitPrice);
            Assert.Equal(sign * (expectedExit / expectedEntry - 1) * 10000 - (1 + expectedExit / expectedEntry), trade.NetBps);
            Assert.Contains(scenario.Journal, x => x.Action == "вход отправлен");
        }
    }

    [Fact]
    public void ChangingFutureCannotChangeChosenLeaderOrBasis()
    {
        var data = Data(); var original = Run(data);
        data = data.Select(x => x.Ticks >= 120000 && x.Quote is { } q && x.Source == "binance-public"
            ? x with { Quote = q with { Bid = q.Bid * 2, Ask = q.Ask * 2 } } : x).ToList();
        var changed = Run(data);
        Assert.Equal(original.Leader, changed.Leader); Assert.Equal(original.Candidates, changed.Candidates);
        Assert.Equal(original.NormalBasisBps, changed.NormalBasisBps);
    }

    [Fact]
    public void GapWhileHoldingDoesNotInventExitOrResumeScenario()
    {
        var data = Data(); data.Add(new(120400, 999999, "binance-public", "c", "gap", null));
        var result = Run(data);
        Assert.All(result.Scenarios, s => { Assert.Equal(1, s.UnresolvedPositions); Assert.Empty(s.TradeLog); });
    }

    [Fact]
    public void GapOnExecutionVenueWhileEntryIsInFlightLeavesFillUnknown()
    {
        // The order already left; losing the data does not cancel it.
        var data = Data(); data.Add(new(120255, 999999, "binance-public", "c", "gap", null));
        Assert.All(Run(data).Scenarios, s =>
        {
            Assert.Empty(s.TradeLog); Assert.Equal(1, s.UnresolvedPositions);
            Assert.Contains(s.Journal, x => x.Reason.Contains("исполнение неизвестно"));
        });
    }

    [Fact]
    public void SentOrderExecutesEvenIfTheLagDisappearedInFlight()
    {
        // The target catches up at 120.3 s: before the 100 ms order arrives, after the 50 ms one.
        var data = Data().Select(x => x.Ticks is >= 120300 and < 120500 && x.Source == "binance-public" && x.Quote is { } q
            ? x with { Quote = q with { Bid = 120.99m, Ask = 121.01m } } : x).ToList();
        var result = Run(data);
        var late = Assert.Single(result.Scenarios.Single(x => x.Mode == "follow" && x.DelayMs == 100).TradeLog);
        Assert.Equal(121.01m, late.EntryPrice); Assert.True(late.NetBps < 0);
        Assert.Equal(120.01m, Assert.Single(result.Scenarios.Single(x => x.Mode == "follow" && x.DelayMs == 50).TradeLog).EntryPrice);
    }

    [Fact]
    public void TailDoesNotUseQuotesFromOutsideRecording()
    {
        var report = Run(Data(), 122300);
        Assert.Equal("evaluated", report.Status);
        // A much longer holding period leaves an unresolved position if the target never follows.
        var input = Data().Where(x => x.Ticks <= 122300).Select(x => x.Ticks >= 120500 && x.Source == "binance-public" && x.Quote is { } q
            ? x with { Quote = q with { Bid = 119.99m, Ask = 120.01m } } : x).ToList();
        var result = StrategyReplay.Run(Recording, Settings with { MaxHoldMs = 2000 }, 1000, 0, 122300, input, default);
        Assert.Contains(result.Scenarios, x => x.UnresolvedPositions > 0);
    }

    [Fact]
    public void FlatCalibrationAndShortRecordingsDoNotForceTrades()
    {
        var flat = Data().Select(x => x.Quote is { } q ? x with { Quote = q with { Bid = 99.99m, Ask = 100.01m } } : x).ToList();
        Assert.Equal("no-leader", Run(flat).Status); Assert.Empty(Run(flat).Scenarios);
        Assert.Equal("too-short", Run(Data(), 60000).Status);
        Assert.Equal("incomplete", StrategyReplay.Run(Recording with { Status = "failed" }, Settings, 1000, 0, 125000, Data(), default).Status);
    }

    [Fact]
    public void StaleQuotesHaltOpenPositionEvenWithoutNextMarketEvent()
    {
        var data = Data().Where(x => x.Ticks <= 120350).ToList();
        var result = Run(data);
        Assert.All(result.Scenarios, s => { Assert.Equal(1, s.UnresolvedPositions); Assert.Empty(s.TradeLog); });
    }

    // Target dips for one tick so that its ask reaches the maker bid placed at 120.25 s.
    internal static List<MarketAnalysis.Input> DipData() => Data().Select(x => x.Ticks == 120300 && x.Source == "binance-public" && x.Quote is { } q
        ? x with { Quote = q with { Bid = 119.96m, Ask = 119.98m } } : x).ToList();

    [Fact]
    public void MakerEntryFollowingTheImpulseIsNotFilledWhenPriceRunsAway()
    {
        var result = StrategyReplay.Run(Recording, Settings with { EntryOrder = "maker" }, 1000, 0, 125000, Data(), default);
        var follow = result.Scenarios.Where(x => x.Mode == "follow").ToArray();
        Assert.All(follow, s =>
        {
            Assert.Empty(s.TradeLog); Assert.Equal(1, s.MakerOrders); Assert.Equal(0, s.MakerFills);
            Assert.Contains(s.Journal, x => x.Reason == "Заявка maker не исполнилась и снята.");
        });
        Assert.Null(Run(Data()).Scenarios[0].MakerOrders);
    }

    [Fact]
    public void MakerEntryFillsOnlyWhenOppositePriceReachesItsLevelAndPaysMakerFee()
    {
        var result = StrategyReplay.Run(Recording, Settings with { EntryOrder = "maker", MakerFeeBps = 2 }, 1000, 0, 125000, DipData(), default);
        var fast = result.Scenarios.Single(x => x.Mode == "follow" && x.DelayMs == 10); var trade = Assert.Single(fast.TradeLog);
        Assert.Equal(1, fast.MakerFills); Assert.Equal(120.3, trade.EntrySeconds, 6); Assert.Equal(119.99m, trade.EntryPrice);
        Assert.Equal(120.99m, trade.ExitPrice);
        Assert.Equal((120.99m / 119.99m - 1) * 10000 - 2 - 1 * 120.99m / 119.99m, trade.NetBps);
        // With 100 ms delay the order arrives after the dip: post-only must reject it rather than fill as taker.
        var slow = result.Scenarios.Single(x => x.Mode == "follow" && x.DelayMs == 100);
        Assert.Empty(slow.TradeLog); Assert.Equal(0, slow.MakerOrders);
        Assert.Contains(slow.Journal, x => x.Reason.StartsWith("Post-only заявка отклонена"));
    }

    [Fact]
    public void GapOnExecutionVenueWhileMakerOrderRestsLeavesItUnresolved()
    {
        var data = Data(); data.Add(new(120400, 999999, "binance-public", "c", "gap", null));
        var result = StrategyReplay.Run(Recording, Settings with { EntryOrder = "maker" }, 1000, 0, 125000, data, default);
        Assert.All(result.Scenarios.Where(x => x.Mode == "follow"), s => { Assert.Equal(1, s.UnresolvedPositions); Assert.Empty(s.TradeLog); });
    }

    [Fact]
    public void RecordingEndingWhileMakerOrderRestsIsUnknownNotSkipped()
    {
        var input = Data().Where(x => x.Ticks <= 120450).ToList();
        var result = StrategyReplay.Run(Recording, Settings with { EntryOrder = "maker", MaxHoldMs = 100 }, 1000, 0, 120450, input, default);
        Assert.All(result.Scenarios.Where(x => x.Mode == "follow"), s =>
        {
            Assert.Equal(1, s.UnresolvedPositions); Assert.Equal(0, s.Skipped);
            Assert.Contains(s.Journal, x => x.Reason.StartsWith("Запись закончилась при отправленной или выставленной заявке"));
        });
    }

    [Fact]
    public void SilentExecutionVenueWhileMakerOrderRestsIsUnknownNotUnfilled()
    {
        var input = Data().Where(x => !(x.Source == "binance-public" && x.Ticks is > 120300 and < 122000)).ToList();
        var result = StrategyReplay.Run(Recording, Settings with { EntryOrder = "maker" }, 1000, 0, 125000, input, default);
        var follow = result.Scenarios.Single(x => x.Mode == "follow" && x.DelayMs == 10);
        Assert.Equal(1, follow.UnresolvedPositions);
        Assert.DoesNotContain(follow.Journal, x => x.Reason == "Заявка maker не исполнилась и снята.");
        Assert.Contains(follow.Journal, x => x.Reason.Contains("устарели при выставленной заявке"));
    }

    [Fact]
    public void SeriesTotalsCombineOnlyEvaluatedRecordingsAndCountOrderOutcomes()
    {
        var first = Run(Data());
        var flat = Data().Select(x => x.Quote is { } q ? x with { Quote = q with { Bid = 99.99m, Ask = 100.01m } } : x).ToList();
        var noLeader = StrategyReplay.Run(Recording with { Id = "flat" }, Settings, 1000, 0, 125000, flat, default);
        var second = StrategyReplay.Run(Recording with { Id = "second", StartedUtc = DateTimeOffset.UnixEpoch.AddDays(1) }, Settings, 1000, 0, 125000, Data(), default);
        var series = StrategySeries.Build(Settings, [(StrategySeries.Row(first), first), (StrategySeries.Row(noLeader), noLeader),
            (StrategySeries.Row(second), second), (StrategySeries.Failed("broken", "Повреждённая строка 3."), null)], DateTimeOffset.UnixEpoch);
        Assert.Equal(4, series.Recordings.Length); Assert.Equal("error", series.Recordings[3].Status);
        Assert.Equal(8, series.Totals.Length);
        var follow = series.Totals.Single(x => x.Mode == "follow" && x.DelayMs == 10);
        var single = first.Scenarios.Single(x => x.Mode == "follow" && x.DelayMs == 10);
        Assert.Equal(2, follow.Recordings); Assert.Equal(2, follow.RecordingsPositive);
        Assert.Equal(2, follow.OrdersSent); Assert.Equal(2, follow.EntriesFilled); Assert.Equal(0, follow.OrdersUnfilled);
        Assert.Equal(2, follow.Trades); Assert.Equal(2 * single.SumNetBps, follow.SumNetBps); Assert.Equal(single.MeanNetBps, follow.MeanNetBps);
        var counter = series.Totals.Single(x => x.Mode == "counter" && x.DelayMs == 10);
        Assert.Equal(0, counter.RecordingsPositive); Assert.Equal(-counter.SumNetBps, counter.DrawdownBps);
    }

    [Fact]
    public void TooSmallResidualIsLoggedAsSkippedRatherThanForcedTrade()
    {
        var result = StrategyReplay.Run(Recording, Settings with { MinEdgeBps = 100 }, 1000, 0, 125000, Data(), default);
        Assert.All(result.Scenarios, s => { Assert.Empty(s.TradeLog); Assert.True(s.Skipped > 0); });
    }
}
