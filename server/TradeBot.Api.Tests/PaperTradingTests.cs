using System.Globalization;
using System.Text.Json;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

public sealed class PaperTradingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "paper-tests-" + Guid.NewGuid());
    private static readonly ReplaySettings Depth = new(OrderNotional: 1200, FeeBps: 1, SlippageBps: 0, MinEdgeBps: 1);
    private static readonly AnalysisRecording Recording = new("fixture", "SOL", "hyperliquid", "binance", DateTimeOffset.UnixEpoch, "completed", 0, false);

    public PaperTradingTests()
    {
        Directory.CreateDirectory(directory);
        using var metadata = JsonDocument.Parse("""{"symbols":[{"symbol":"SOLUSDT","baseAsset":"SOL","quoteAsset":"USDT","contractType":"PERPETUAL","filters":[{"filterType":"MARKET_LOT_SIZE","stepSize":"1","minQty":"1","maxQty":"10000"},{"filterType":"MIN_NOTIONAL","notional":"5"},{"filterType":"PRICE_FILTER","tickSize":"0.01"}]}]}""");
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 3, startedUtc = DateTimeOffset.UnixEpoch, stopwatchFrequency = 1000, options = new { Coin = "SOL" },
            exchangeMetadata = metadata.RootElement
        }));
    }

    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    // The fixture market as raw recorder events, in the order the file writer would emit them.
    private static List<CaptureEvent> Events(bool makerTrades = false)
    {
        static string S(decimal n) => n.ToString(CultureInfo.InvariantCulture);
        var events = new List<CaptureEvent>();
        foreach (var input in StrategyReplayTests.Data())
        {
            var payload = "";
            if (input.Quote is { } q)
            {
                payload = input.Source == "hyperliquid"
                    ? JsonSerializer.Serialize(new { channel = "bbo", data = new { time = input.Ticks, bbo = new[] { new { px = S(q.Bid), sz = "100" }, new { px = S(q.Ask), sz = "100" } } } })
                    : JsonSerializer.Serialize(new { stream = "solusdt@bookTicker", data = new Dictionary<string, object> { ["e"] = "bookTicker", ["u"] = input.Ticks, ["b"] = S(q.Bid), ["B"] = "100", ["a"] = S(q.Ask), ["A"] = "100" } });
                if (input.Source == "binance-public") events.Add(new(input.Source, "c", "market", DateTimeOffset.UnixEpoch, input.Ticks,
                    JsonSerializer.Serialize(new { stream = "solusdt@depth20@100ms", data = new { e = "depthUpdate", s = "SOLUSDT", u = input.Ticks,
                        b = new[] { new[] { S(q.Bid), "100" } }, a = new[] { new[] { S(q.Ask), "100" } } } })));
            }
            events.Add(new(input.Source, "c", input.Kind, DateTimeOffset.UnixEpoch, input.Ticks, payload));
        }
        if (makerTrades)
        {
            events.Add(new("binance-market", "t", "connected", DateTimeOffset.UnixEpoch, 0, ""));
            events.Add(new("binance-market", "t", "market", DateTimeOffset.UnixEpoch, 120300, """{"e":"aggTrade","s":"SOLUSDT","p":"119.99","q":"300","m":true}"""));
        }
        return events.OrderBy(x => x.ReceivedTicks).ToList();
    }

    // Exactly what RecordingAnalysisStore feeds the replay for a file with these lines.
    private ReplayScenario Replay(List<CaptureEvent> events, ReplaySettings settings, int delay)
    {
        var parser = new RecordingEventParser("SOL", "hyperliquid", "binance", true);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var market = new ReplayMarketData([null, ReplayInstrument.Read(manifest.RootElement, "binance", "SOL", false)], [], []);
        var points = new List<MarketAnalysis.Input>();
        for (var i = 0; i < events.Count; i++)
        {
            var parsed = parser.Parse(events[i], i + 1);
            if (parsed.Point is { } p) points.Add(p);
            if (parsed.Book is { } b) market.Books.Add(b);
            if (parsed.Trade is { } t) market.Trades!.Add(t);
        }
        var report = StrategyReplay.Run(Recording, settings, 1000, events.Min(x => x.ReceivedTicks), events.Max(x => x.ReceivedTicks), points, default, market);
        Assert.Equal("evaluated", report.Status);
        return report.Scenarios.Single(x => x.Mode == "follow" && x.DelayMs == delay);
    }

    // Writes the events one by one as the recorder would, advancing a fake clock; `written` is the file line number.
    private PaperSession Live(List<CaptureEvent> written, ReplaySettings settings, int delay, Func<int, long>? clockAt = null)
    {
        long now = 0; var stops = 0;
        var session = new PaperSession("paper", new PaperRequest("SOL", "hyperliquid", "binance", 600, settings, delay), () => now)
        { StopRecording = () => stops++ };
        session.Started(directory);
        for (var i = 0; i < written.Count; i++)
        {
            session.Recorded(written[i], i + 1, 0);
            now = clockAt?.Invoke(i) ?? written[i].ReceivedTicks;
            if (i % 7 == 0) Assert.False(session.Pump());
        }
        session.Finished(new(DateTimeOffset.UnixEpoch, "completed", null, 0, written.Count, 0, [], new { }));
        Assert.True(session.Pump());
        return session;
    }

    [Theory]
    [InlineData("taker", 10)] [InlineData("taker", 100)] [InlineData("maker", 10)]
    public void LiveSessionMatchesReplayOfItsOwnRecording(string entry, int delay)
    {
        var settings = Depth with { EntryOrder = entry };
        var events = Events(makerTrades: entry == "maker");
        var expected = Replay(events, settings, delay);
        var snapshot = Live(events, settings, delay).Snapshot();
        Assert.Equal("finished", snapshot.State); Assert.Equal("hyperliquid", snapshot.Leader); Assert.Equal(0, snapshot.LateEvents);
        Assert.Equal(expected.TradeLog, snapshot.Result!.TradeLog);
        Assert.Equal(expected.Journal, snapshot.Result.Journal);
        Assert.Equal(expected.SumNetBps, snapshot.SumNetBps);
        Assert.NotEmpty(snapshot.Trades);
        var saved = JsonSerializer.Deserialize<PaperRunReport>(File.ReadAllText(Path.Combine(directory, "paper-run-v1.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(expected.TradeLog, saved.Result!.TradeLog);
    }

    [Fact]
    public void EventsWrittenSlightlyOutOfOrderAreProcessedInReceiveOrder()
    {
        var events = Events();
        // Swap neighbours: the file order differs from receive order by up to one 50 ms step, well inside the hold-back.
        var written = events.ToList();
        for (var i = 10; i + 1 < written.Count; i += 9) (written[i], written[i + 1]) = (written[i + 1], written[i]);
        var replay = Replay(written.OrderBy(x => x.ReceivedTicks).ToList(), Depth, 10);
        var snapshot = Live(written, Depth, 10).Snapshot();
        Assert.Equal(0, snapshot.LateEvents);
        Assert.Equal(replay.TradeLog, snapshot.Result!.TradeLog);
    }

    [Fact]
    public void EventArrivingAfterTheHoldBackIsCountedAsLate()
    {
        var events = Events();
        var delayed = events[400];
        var written = events.Where((_, i) => i != 400).ToList(); written.Insert(700, delayed);
        var snapshot = Live(written, Depth, 10).Snapshot();
        Assert.Equal(1, snapshot.LateEvents);
        Assert.Contains("опозданием", snapshot.Explanation);
    }

    [Fact]
    public void NoLeaderStopsTheRecordingAndRunsNoTrades()
    {
        // An unreachable leader margin: calibration must refuse to pick a leader.
        var flat = Events();
        var settings = Depth with { LeaderAdvantageBps = 100 };
        long now = 0; var stops = 0;
        var session = new PaperSession("paper", new PaperRequest("SOL", "hyperliquid", "binance", 600, settings), () => now) { StopRecording = () => stops++ };
        session.Started(directory);
        for (var i = 0; i < flat.Count && flat[i].ReceivedTicks <= 121000; i++) { session.Recorded(flat[i], i + 1, 0); now = flat[i].ReceivedTicks; session.Pump(); }
        Assert.Equal("no-leader", session.Snapshot().State); Assert.Equal(1, stops);
        session.Finished(new(DateTimeOffset.UnixEpoch, "completed", null, 0, 0, 0, [], new { }));
        Assert.True(session.Pump()); Assert.True(session.Done); Assert.Null(session.Snapshot().Result);
    }

    // Feeds events and lets a test act on the session at a given receive time.
    private (PaperSession Session, Func<int> Stops) Drive(PaperRequest request, List<CaptureEvent> events, long actAt, Action<PaperSession> act)
    {
        long now = 0; var stops = 0; var acted = false;
        var session = new PaperSession("paper", request, () => now) { StopRecording = () => stops++ };
        session.Started(directory);
        for (var i = 0; i < events.Count; i++)
        {
            session.Recorded(events[i], i + 1, 0); now = events[i].ReceivedTicks; session.Pump();
            if (!acted && now >= actAt) { acted = true; act(session); }
        }
        session.Finished(new(DateTimeOffset.UnixEpoch, "completed", null, 0, events.Count, 0, [], new { }));
        Assert.True(session.Pump());
        return (session, () => stops);
    }

    [Fact]
    public void PauseBlocksNewEntriesButIsRecordedAsTheReason()
    {
        var (session, _) = Drive(new("SOL", "hyperliquid", "binance", 600, Depth), Events(), 100000, s => s.Pause(true));
        var snapshot = session.Snapshot();
        Assert.Empty(snapshot.Result!.TradeLog); Assert.Equal(0, snapshot.Result.OrdersSent);
        Assert.Contains(snapshot.Result.Journal, x => x.Reason.StartsWith("Пауза"));
        Assert.True(snapshot.Paused);
    }

    [Fact]
    public void LossLimitStopsNewEntriesAfterClosedLosses()
    {
        // The counter-impulse control loses on the fixture; a 0.01 limit is hit by that one trade.
        var request = new PaperRequest("SOL", "hyperliquid", "binance", 600, Depth, Counter: true, LossLimit: .01m);
        var (session, _) = Drive(request, Events(), long.MaxValue, _ => { });
        var record = session.Record();
        Assert.True(record.ClosedNetQuote < -.01m);
        Assert.Null(record.EntriesBlocked); // completed runs report no live block
        Assert.Contains("лимит убытка", session.EntryBlock(0)!);
    }

    [Fact]
    public void GracefulStopWaitsForTheOpenPositionToClose()
    {
        var (session, stops) = Drive(new("SOL", "hyperliquid", "binance", 600, Depth), Events(), 120900, s => s.RequestStop(false));
        var snapshot = session.Snapshot();
        Assert.Equal(1, stops());
        Assert.Single(snapshot.Result!.TradeLog); Assert.Equal(0, snapshot.Result.UnresolvedPositions);
        Assert.True(snapshot.Stopping);
    }

    [Fact]
    public void ForcedStopLeavesTheOpenPositionUnresolved()
    {
        var events = Events();
        long now = 0; var stops = 0;
        var session = new PaperSession("paper", new("SOL", "hyperliquid", "binance", 600, Depth), () => now) { StopRecording = () => stops++ };
        session.Started(directory);
        var i = 0;
        for (; i < events.Count && events[i].ReceivedTicks <= 120450; i++) { session.Recorded(events[i], i + 1, 0); now = events[i].ReceivedTicks; session.Pump(); }
        session.RequestStop(true);
        Assert.Equal(1, stops);
        session.Finished(new(DateTimeOffset.UnixEpoch, "completed", null, 0, i, 0, [], new { }));
        Assert.True(session.Pump());
        Assert.Equal(1, session.Snapshot().Result!.UnresolvedPositions);
    }

    [Fact]
    public void OrderLargerThanTheBotBalanceIsNotSent()
    {
        // 9 coins at about 120 USDT need over 1000 USDT; a 100 USDT balance must refuse the entry before it is sent.
        var (session, _) = Drive(new("SOL", "hyperliquid", "binance", 600, Depth, Budget: 100), Events(), long.MaxValue, _ => { });
        var result = session.Snapshot().Result!;
        Assert.Equal(0, result.OrdersSent);
        Assert.Contains(result.Journal, x => x.Reason.Contains("выделенный боту баланс"));
    }

    [Fact]
    public void BotRunRefusesWhenItsOwnExchangeLeads()
    {
        // The fixture leader is Hyperliquid; a bot trading on Hyperliquid with a Binance signal has nothing to follow.
        var request = new PaperRequest("SOL", "binance", "hyperliquid", 600, Depth with { ExecutionModel = "bbo" }, RequireSourceLeader: true);
        var (session, stops) = Drive(request, Events(), long.MaxValue, _ => { });
        var snapshot = session.Snapshot();
        Assert.Equal("no-leader", snapshot.State); Assert.Contains("биржа бота", snapshot.Explanation); Assert.Equal(1, stops());
    }

    [Fact]
    public void InterruptedRunsAreMarkedWhenTheStoreOpensAgain()
    {
        var path = Path.Combine(directory, "workspace.db");
        var store = new PaperRunStore(path);
        var running = new PaperRunRecord("r1", "bot", "Бот", StrategyReplay.Version, DateTimeOffset.UnixEpoch, null, "running", "Идёт.", "SOL",
            "hyperliquid", "binance", Depth, 50, false, "hyperliquid", "binance", null, 0, 0, 0, 0, 0, null, [], []);
        store.SaveAsync(running, default).GetAwaiter().GetResult();
        store.SaveAsync(running with { Id = "r0", State = "finished", StartedUtc = DateTimeOffset.UnixEpoch.AddDays(-1) }, default).GetAwaiter().GetResult();
        var reopened = new PaperRunStore(path);
        var runs = reopened.ListAsync("bot", 10, default).GetAwaiter().GetResult();
        Assert.Equal(["r1", "r0"], runs.Select(x => x.Id));
        Assert.Equal("interrupted", runs[0].State); Assert.NotNull(runs[0].EndedUtc);
        Assert.Equal("finished", runs[1].State);
        Assert.Equal("r1", reopened.LatestByBotAsync(default).GetAwaiter().GetResult()["bot"].Id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void StoppingDuringCalibrationEndsWithoutTrades()
    {
        var events = Events().Where(x => x.ReceivedTicks < 60000).ToList();
        var snapshot = Live(events, Depth, 10).Snapshot();
        Assert.Equal("stopped", snapshot.State); Assert.Null(snapshot.Result); Assert.Empty(snapshot.Trades);
    }
}
