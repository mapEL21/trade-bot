using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradeBot.Api;
using Xunit;

namespace TradeBot.Api.Tests;

// The live density bot on a fake stream: the screener points at a wall, the coin is watched, the bot bounces off the wall,
// and the run computed again from its own recording gives exactly the same trade.
public sealed class LiveBotsTests : IDisposable
{
    private const long T0 = 1_760_000_000_000;
    private readonly string root = Path.Combine(Path.GetTempPath(), "live-bots-tests-" + Guid.NewGuid());
    private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    // 1 million a day: a screener wall needs 10 000 USDT; the price step is 0.05.
    private static readonly ScreenerMarket Market = new("XUSDT", .05m, 1_000_000, 100);

    private sealed class FakeFeed : ILiveFeed
    {
        public event Action<string>? Message;
        public readonly List<string> Watched = [];
        public void Watch(string symbol) => Watched.Add(symbol);
        public void Unwatch(string symbol) => Watched.Remove(symbol);
        public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { });
        public void Send(string raw) => Message?.Invoke(raw);
    }

    private sealed class FakeSource : IDepthSource
    {
        public int UsedWeight => 0;
        public Task<ScreenerMarket[]> MarketsAsync(CancellationToken ct) => Task.FromResult(new[] { Market });
        public Task<DepthBook> DepthAsync(string symbol, CancellationToken ct) => Task.FromResult(ScreenerBook());
        // An hour of 1% candles: the coin's typical minute range.
        public Task<string> KlinesAsync(string symbol, CancellationToken ct) => Task.FromResult(
            $"{{\"symbol\":\"{symbol}\",\"interval\":\"1m\",\"klines\":[{string.Join(',', Enumerable.Range(0, 62).Select(i => $"[{T0 - (62 - i) * 60_000},\"100\",\"101\",\"100\",\"100\",\"1\",0,\"100\",1,\"1\",\"100\",\"0\"]"))}]}}");
    }

    // The screener's view: a bid wall of 200 coins at 99.95 in a book of 1-coin levels 0.05 apart.
    private static DepthBook ScreenerBook() => new(
        Enumerable.Range(0, 60).Select(k => { var p = 100 - .05m * k; return (p, p == 99.95m ? 200m : 1m); }).ToArray(),
        Enumerable.Range(0, 60).Select(k => (100.05m + .05m * k, 1m)).ToArray());

    // The live stream: a trade and a book of 20 levels each second, the bid wall of 200 coins at 99.95 while it stands.
    private static IEnumerable<string> Second(long second, decimal bid, bool wall = true)
    {
        var ms = T0 + second * 1000;
        string Side(int sign) => string.Join(',', Enumerable.Range(0, 20).Select(k =>
        {
            var price = sign < 0 ? bid - .05m * k : bid + .05m + .05m * k;
            return $"[\"{S(price)}\",\"{(wall && price == 99.95m ? "200" : "1")}\"]";
        }));
        yield return $"{{\"stream\":\"xusdt@aggTrade\",\"data\":{{\"e\":\"aggTrade\",\"s\":\"XUSDT\",\"p\":\"{S(bid)}\",\"q\":\"1\",\"T\":{ms},\"m\":false}}}}";
        yield return $"{{\"stream\":\"xusdt@depth20@100ms\",\"data\":{{\"e\":\"depthUpdate\",\"E\":{ms + 1},\"T\":{ms + 1},\"s\":\"XUSDT\",\"b\":[{Side(-1)}],\"a\":[{Side(1)}]}}}}";
    }

    [Fact]
    public async Task ALiveRunWatchesTheScreenersCoinTradesTheBounceAndItsRecordingGivesTheSameTrade()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Live:Enabled"] = "true", ["Live:Path"] = Path.Combine(root, "live"), ["Screener:Path"] = Path.Combine(root, "screener"),
        }).Build();
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root };
        var source = new FakeSource();
        using var screener = new DensityScreener(source, config, environment, NullLogger<DensityScreener>.Instance);
        // Two rounds in a row confirm the wall.
        screener.Scan(Market, ScreenerBook(), T0);
        screener.Scan(Market, ScreenerBook(), T0 + 1000);
        Assert.Single(screener.Snapshot().Walls);
        var store = new PaperRunStore(Path.Combine(root, "workspace.db"));
        var parameters = new SignalParameters(SignalParameters.Density, FeePct: .02m);
        var run = new SignalRun("run-1", "bot-1", T0 - 1000, null, DateTimeOffset.UnixEpoch, 100, 30, parameters, Live: true);
        await store.SaveSignalRunAsync(run, CancellationToken.None);
        var feed = new FakeFeed();
        using var live = new LiveBots(store, screener, source, feed, config, environment, NullLogger<LiveBots>.Instance);
        await live.StartAsync(CancellationToken.None);
        try
        {
            await live.Sync(CancellationToken.None);
            Assert.Equal(["XUSDT"], feed.Watched);
            var watching = Assert.Single(live.Current(run.Id)!.Value.View.Watching);
            Assert.Equal((1, 99.95m), (watching.Side, watching.Wall));

            // The price touches the wall, goes up and comes back (a bounce the bot measures), then bounces again.
            for (var t = 0; t < 100; t++) foreach (var m in Second(t, 100)) feed.Send(m);
            for (var t = 100; t < 120; t++) foreach (var m in Second(t, 100.5m)) feed.Send(m);
            for (var t = 120; t < 140; t++) foreach (var m in Second(t, 100)) feed.Send(m);
            for (var t = 140; t < 150; t++) foreach (var m in Second(t, 100.55m)) feed.Send(m);

            var current = live.Current(run.Id)!.Value;
            var trade = Assert.Single(current.Result.Trades);
            Assert.StartsWith("отскок от плотности 20 тыс. $ на 99,95", trade.EntryReason);
            Assert.Contains("1 шаг. до неё", trade.EntryReason);
            Assert.StartsWith("цена отошла от плотности на средний отскок", trade.ExitReason);

            // The run stops: the session closes, and the run computed from its recording gives the same trade.
            await store.SaveSignalRunAsync(run with { ToMs = T0 + 200_000 }, CancellationToken.None);
            await live.Sync(CancellationToken.None);
            Assert.Null(live.Current(run.Id));
            Assert.Empty(feed.Watched);
            Assert.True(File.Exists(Path.Combine(current.Directory, "summary.json")));
            var replay = BotEngine.Run(current.Directory, parameters, CancellationToken.None);
            Assert.Equal(current.Result.Trades, replay.Trades);
        }
        finally
        {
            await live.StopAsync(CancellationToken.None);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task WithoutLiveEnabledNoSessionIsOpened()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Live:Path"] = Path.Combine(root, "live") }).Build();
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root };
        using var screener = new DensityScreener(new FakeSource(), config, environment, NullLogger<DensityScreener>.Instance);
        var store = new PaperRunStore(Path.Combine(root, "workspace.db"));
        await store.SaveSignalRunAsync(new SignalRun("run-2", "bot", T0, null, DateTimeOffset.UnixEpoch, 100, 30, new(), Live: true), CancellationToken.None);
        using var live = new LiveBots(store, screener, new FakeSource(), new FakeFeed(), config, environment, NullLogger<LiveBots>.Instance);
        await live.Sync(CancellationToken.None);
        Assert.Null(live.Current("run-2"));
        Assert.False(Directory.Exists(Path.Combine(root, "live")));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
