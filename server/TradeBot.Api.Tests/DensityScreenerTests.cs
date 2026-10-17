using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradeBot.Api;
using Xunit;

namespace TradeBot.Api.Tests;

public sealed class DensityScreenerTests : IDisposable
{
    private const long T0 = 1_760_000_000_000;
    private readonly string root = Path.Combine(Path.GetTempPath(), "screener-tests-" + Guid.NewGuid());
    // 1 million a day: about 694 USDT a minute, so 10 minutes of trading is under the 10 000 USDT floor a wall needs.
    private static readonly ScreenerMarket Market = new("XUSDT", .01m, 1_000_000, 100);

    // Bids from best 100.00 down and asks from 100.01 up, 0.01 apart, 10 coins (about 1 000 USDT) each; walls replace a level.
    private static DepthBook Book(decimal bestBid, Dictionary<decimal, decimal>? walls = null) => new(
        Enumerable.Range(0, 300).Select(k => { var p = bestBid - .01m * k; return (p, walls?.GetValueOrDefault(p) is > 0 and var q ? q : 10m); }).ToArray(),
        Enumerable.Range(0, 300).Select(k => { var p = bestBid + .01m + .01m * k; return (p, walls?.GetValueOrDefault(p) is > 0 and var q ? q : 10m); }).ToArray());

    private DensityScreener Screener(IDepthSource? source = null) => new(source ?? new FakeSource([]),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Screener:Path"] = root }).Build(),
        new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root }, NullLogger<DensityScreener>.Instance);

    [Fact]
    public void WallsAreLevelsManyTimesTheLevelsAroundWithTheirDistanceInPriceSteps()
    {
        // 300 coins at 99.80 (30 000 USDT, 30× the median level); 9 coins at 100.05 is the largest ask but under 10 000 USDT.
        var walls = DensityScreener.Find(Market, Book(100, new() { [99.80m] = 300, [100.05m] = 90 }), T0);
        var wall = Assert.Single(walls);
        Assert.Equal((1, 99.80m, 20L, "near"), (wall.Side, wall.Price, wall.Ticks, wall.Status));
        Assert.Equal(29.9m, wall.Ratio);
        Assert.Equal(.205m, wall.DistancePct);
        Assert.Equal(43.1m, wall.Minutes);
        // Only the largest level of a side, and only when it stands out: a second wall of the same size is not one.
        Assert.Empty(DensityScreener.Find(Market, Book(100, new() { [99.80m] = 300, [99.70m] = 200 }), T0));
        // A coin that hardly moves in a day (a stablecoin, gold) has no walls.
        Assert.Empty(DensityScreener.Find(Market with { RangePct = 1 }, Book(100, new() { [99.80m] = 300 }), T0));
        // Farther than 0.3% from the price: standing, not near.
        Assert.Equal("stands", Assert.Single(DensityScreener.Find(Market, Book(100, new() { [99.50m] = 300 }), T0)).Status);
    }

    [Fact]
    public void AWallIsFollowedUntilItIsEatenOrPulledAndTheJournalKeepsIt()
    {
        using var screener = Screener();
        screener.Scan(Market, Book(100, new() { [99.80m] = 300, [100.30m] = 300 }), T0);
        // Seen once it is only a candidate; the next round confirms it.
        Assert.Empty(screener.Snapshot().Walls);
        screener.Scan(Market, Book(100, new() { [99.80m] = 300, [100.30m] = 300 }), T0 + 30_000);
        Assert.Equal(2, screener.Snapshot().Walls.Length);
        // The bid wall loses a third: being eaten.
        screener.Scan(Market, Book(100, new() { [99.80m] = 190, [100.30m] = 300 }), T0 + 60_000);
        var eating = screener.Snapshot().Walls.Single(w => w.Side == 1);
        Assert.Equal(("eating", T0, 29_940m), (eating.Status, eating.FirstMs, eating.Peak));
        // The price goes through the bid wall; the ask wall vanishes with the price still below it.
        screener.Scan(Market, Book(99.70m), T0 + 120_000);
        var snapshot = screener.Snapshot();
        Assert.Empty(snapshot.Walls);
        Assert.Equal(["pulled", "eaten"], snapshot.Finished.OrderBy(w => w.Side).Select(w => w.Status));
        Assert.Equal((2, 1, 1), (snapshot.Day.Found, snapshot.Day.Eaten, snapshot.Day.Pulled));
        Assert.Equal(2m, snapshot.Day.MedianMinutes);
        // Found and finished walls are in the day's journal and come back after a restart.
        var journal = Assert.Single(Directory.GetFiles(root, "walls-*.ndjson"));
        Assert.Equal(4, File.ReadAllLines(journal).Length);
    }

    [Fact]
    public void AWallAtTheBestPriceThatShrankIsNotCountedButOneAwayFromItWasPulled()
    {
        using var screener = Screener();
        // At the best bid trades can eat it: shrinking there is not a pull.
        screener.Scan(Market, Book(100, new() { [100m] = 300 }), T0);
        screener.Scan(Market, Book(100, new() { [100m] = 300 }), T0 + 30_000);
        screener.Scan(Market, Book(100, new() { [100m] = 50 }), T0 + 60_000);
        Assert.Empty(screener.Snapshot().Finished);
        // 20 steps away no trade can reach it: it was cancelled.
        screener.Scan(Market, Book(100, new() { [99.80m] = 300 }), T0 + 120_000);
        screener.Scan(Market, Book(100, new() { [99.80m] = 300 }), T0 + 150_000);
        screener.Scan(Market, Book(100, new() { [99.80m] = 50 }), T0 + 180_000);
        Assert.Equal("pulled", Assert.Single(screener.Snapshot().Finished).Status);
    }

    [Fact]
    public void MarketsCarryThePriceStepOfEachTradingPerpetual()
    {
        const string info = """
            {"symbols":[
              {"symbol":"XUSDT","quoteAsset":"USDT","contractType":"PERPETUAL","status":"TRADING","filters":[{"filterType":"PRICE_FILTER","tickSize":"0.0001"}]},
              {"symbol":"YUSDT","quoteAsset":"USDT","contractType":"CURRENT_QUARTER","status":"TRADING","filters":[]},
              {"symbol":"ZUSDT","quoteAsset":"USDT","contractType":"PERPETUAL","status":"SETTLING","filters":[]}]}
            """;
        const string tickers = """[{"symbol":"XUSDT","quoteVolume":"123456","lastPrice":"0.5"},{"symbol":"YUSDT","quoteVolume":"1","lastPrice":"1"}]""";
        var market = Assert.Single(BinanceDepthSource.Markets(info, tickers));
        Assert.Equal(("XUSDT", .0001m, 123456m), (market.Symbol, market.TickSize, market.QuoteVolume));
        var book = BinanceDepthSource.Book("""{"bids":[["0.4999","100"]],"asks":[["0.5001","200"]]}""");
        Assert.Equal((.4999m, 200m), (book.Bids[0].Price, book.Asks[0].Quantity));
    }

    [Fact]
    public async Task TheScreenerGoesRoundEveryMarketUntilStopped()
    {
        DensityScreener.Pause = TimeSpan.Zero;
        var source = new FakeSource([Market, Market with { Symbol = "WUSDT" }]);
        using var screener = Screener(source);
        Assert.True(screener.Start());
        Assert.False(screener.Start());
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (screener.Snapshot().Rounds < 2) await Task.Delay(10, wait.Token);
        var snapshot = screener.Snapshot();
        Assert.Equal(("running", 2), (snapshot.State, snapshot.Markets));
        Assert.Equal(["WUSDT", "XUSDT"], snapshot.Walls.Select(w => w.Symbol).Order());
        Assert.True(screener.Stop());
        while (screener.Snapshot().State != "stopped") await Task.Delay(10, wait.Token);
    }

    private sealed class FakeSource(ScreenerMarket[] markets) : IDepthSource
    {
        public int UsedWeight => 0;
        public Task<ScreenerMarket[]> MarketsAsync(CancellationToken ct) => Task.FromResult(markets);
        public Task<DepthBook> DepthAsync(string symbol, CancellationToken ct) => Task.FromResult(Book(100, new() { [99.80m] = 300 }));
        public Task<string> KlinesAsync(string symbol, CancellationToken ct) => Task.FromResult("{}");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
