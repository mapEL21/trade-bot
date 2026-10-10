using TradeBot.Api;
using Xunit;

namespace TradeBot.Api.Tests;

public class AnalysisTests
{
    private static readonly AnalysisRecording Recording = new("test", "SOL", "hyperliquid", "binance", DateTimeOffset.UtcNow, "completed", 1, false);
    private static MarketAnalysis.Input Point(string source, long ms, decimal mid, int order = 1) =>
        new(ms, (int)ms, source, "c", "market", new(mid - .01m, mid + .01m, 10, 10, order, DateTimeOffset.UtcNow, ms));
    private static List<MarketAnalysis.Input> Data(decimal move = 100.1m) => [
        new(0, -2, "hyperliquid", "c", "connected", null), new(0, -1, "binance-public", "c", "connected", null),
        Point("hyperliquid", 0, 100), Point("binance-public", 0, 100),
        Point("hyperliquid", 250, move, 2), Point("binance-public", 350, 100, 2),
        Point("binance-public", 1350, 100.2m, 3), Point("hyperliquid", 5000, move, 3),
        Point("binance-public", 5000, 100.2m, 4) ];
    private static AnalysisReport Run(List<MarketAnalysis.Input> data, AnalysisSettings? settings = null) =>
        MarketAnalysis.Analyze(Recording, settings ?? new(MaxAgeMs: 5000), 1000, 0, 6000, data.Count, 0, data, default);

    [Fact]
    public void LongUsesAskThenBidAndBothNotionalFees()
    {
        var model = Run(Data()).Directions[0].Model;
        var entry = 100.01m * 1.0001m;
        var exit = 100.19m * .9999m;
        var gross = (exit / entry - 1) * 10000;
        Assert.Equal(1, model.Trades);
        Assert.Equal(gross, model.MeanGrossBps);
        Assert.Equal(gross - 5 * (1 + exit / entry), model.MeanNetBps);
    }

    [Fact]
    public void FutureQuoteCannotLeakIntoExit()
    {
        var data = Data();
        data.RemoveAll(x => x.Source == "binance-public" && x.Ticks == 1350);
        data.Add(Point("binance-public", 1351, 200, 3));
        var report = Run(data);
        Assert.True(report.Directions[0].Model.MeanNetBps < 0);
        Assert.Equal(0m, report.Directions[0].Horizons.Single(h => h.HorizonMs == 1000).MeanMoveBps);
        Assert.Equal(0d, report.Directions[0].Horizons[0].SameDirectionPercent);
    }

    [Fact]
    public void ShortUsesBidThenAskWithAdverseSlippage()
    {
        var model = Run(Data(99.9m)).Directions[0].Model;
        var entry = 99.99m * .9999m;
        var exit = 100.21m * 1.0001m;
        Assert.Equal(-(exit / entry - 1) * 10000 - 5 * (1 + exit / entry), model.MeanNetBps);
        Assert.Equal("short", model.Examples[0].Side);
    }

    [Fact]
    public void GapExcludesWindowsEvenAfterPricesReturn()
    {
        var data = Data(); data.Add(new(500, 500, "binance-public", "c", "gap", null));
        var direction = Run(data).Directions[0];
        Assert.Equal(0, direction.Model.Trades); Assert.Equal(1, direction.Model.Excluded);
        Assert.Equal(0, direction.Horizons.Single(h => h.HorizonMs == 1000).Samples);
    }

    [Fact]
    public void StaleAndMissingTailAreExcluded()
    {
        Assert.Equal(0, Run(Data(), new(MaxAgeMs: 10)).Directions[0].Impulses);
        Assert.Equal(0, Run(Data(), new(HoldMs: 10000, MaxAgeMs: 5000)).Directions[0].Model.Trades);
    }

    [Fact]
    public void ArrivalOrderingAndOldExchangeSequenceAreHandled()
    {
        var data = Data(); data.Add(Point("hyperliquid", 300, 200, 1)); data.Reverse();
        var report = Run(data);
        Assert.Equal(1, report.OutOfOrderQuotes);
        Assert.Equal(1, report.Directions[0].Impulses);
        Assert.Equal(Run(Data()).Directions[0].Model.MeanNetBps, report.Directions[0].Model.MeanNetBps);
        Assert.Equal(100d, report.CoveragePercent);
    }

    [Fact]
    public void UnchangedTargetHasZeroHitRateAndLosesCosts()
    {
        var data = Data(); data.RemoveAll(x => x.Source == "binance-public" && x.Ticks > 350);
        data.Add(Point("binance-public", 5000, 100, 3));
        var direction = Run(data).Directions[0];
        Assert.All(direction.Horizons, h => Assert.Equal(0d, h.SameDirectionPercent));
        Assert.True(direction.Model.MeanNetBps < 0);
    }
}
