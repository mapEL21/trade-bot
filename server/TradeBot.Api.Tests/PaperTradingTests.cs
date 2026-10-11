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
    private static List<CaptureEvent> Events(bool makerTrades = false, List<MarketAnalysis.Input>? data = null)
    {
        static string S(decimal n) => n.ToString(CultureInfo.InvariantCulture);
        var events = new List<CaptureEvent>();
        foreach (var input in data ?? StrategyReplayTests.Data())
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
            if (i % 7 == 0) { Assert.False(session.Pump()); session.WaitForEngine(); }
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
            session.Recorded(events[i], i + 1, 0); now = events[i].ReceivedTicks; session.Pump(); session.WaitForEngine();
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

    // The target never catches up after 120.5 s, so without a stop the position would be held until the time limit.
    private static List<CaptureEvent> StuckEvents() => Events(data: StrategyReplayTests.Data().Select(x =>
        x.Ticks >= 120500 && x.Source == "binance-public" && x.Quote is { } q ? x with { Quote = q with { Bid = 119.99m, Ask = 120.01m } } : x).ToList());

    [Fact]
    public void OpenPositionIsReportedWithItsCurrentResultAfterFees()
    {
        PaperSnapshot? seen = null;
        Drive(new("SOL", "hyperliquid", "binance", 600, Depth), StuckEvents(), 121200, s => seen = s.Snapshot());
        var position = seen!.Position!;
        Assert.Equal("long", position.Side);
        Assert.Equal(9, position.Quantity);
        Assert.True(position.MarkNetBps < 0); // bought on the jump, the follower never moved: fees and spread only
        Assert.Equal(position.MarkNetBps / 10000 * 9 * position.EntryPrice, position.MarkNetQuote);
        Assert.True(seen.PositionOpen);
    }

    [Fact]
    public void CloseNowExitsTheOpenPositionAndThenStops()
    {
        var (session, stops) = Drive(new("SOL", "hyperliquid", "binance", 600, Depth), StuckEvents(), 121200, s => s.RequestStop("close"));
        var result = session.Snapshot().Result!;
        var trade = Assert.Single(result.TradeLog);
        Assert.Equal("Остановка пользователем: закрытие позиции", trade.ExitReason);
        Assert.True(trade.ExitSeconds < 121.3); // well before the 2 s hold limit at 122.26 s
        Assert.Equal(0, result.UnresolvedPositions);
        Assert.Equal(1, stops());
        Assert.Null(session.Snapshot().Position);
    }

    [Fact]
    public void ForcedStopLeavesTheOpenPositionUnresolved()
    {
        var events = Events();
        long now = 0; var stops = 0;
        var session = new PaperSession("paper", new("SOL", "hyperliquid", "binance", 600, Depth), () => now) { StopRecording = () => stops++ };
        session.Started(directory);
        var i = 0;
        for (; i < events.Count && events[i].ReceivedTicks <= 120450; i++) { session.Recorded(events[i], i + 1, 0); now = events[i].ReceivedTicks; session.Pump(); session.WaitForEngine(); }
        session.RequestStop(true);
        Assert.Equal(1, stops);
        session.Finished(new(DateTimeOffset.UnixEpoch, "completed", null, 0, i, 0, [], new { }));
        Assert.True(session.Pump());
        Assert.Equal(1, session.Snapshot().Result!.UnresolvedPositions);
    }

    [Fact]
    public void ClosedPaperTradesBecomeBotTradesInUsdt()
    {
        var (session, _) = Drive(new("SOL", "hyperliquid", "binance", 600, Depth, BotId: "bot"), Events(), long.MaxValue, _ => { });
        var run = session.Record();
        var source = Assert.Single(run.TradeLog);
        var trade = Assert.Single(BotTrade.FromRun(run));
        Assert.Equal(source.NetQuote, trade.Gross - trade.Fee);
        Assert.Equal(source.FeesQuote, trade.Fee);
        Assert.Equal((source.EntryPrice, source.ExitPrice, source.Quantity), (trade.Entry, trade.Exit, (decimal?)trade.Quantity));
        Assert.Equal(run.StartedUtc.AddSeconds(source.ExitSeconds).ToString("O"), trade.At);
        Assert.True(trade.Simulated);
        // Instrument, exchanges, timing and size belong to the trade's own run, not to the bot's current settings.
        Assert.Equal("SOLUSDT", trade.Symbol);
        Assert.Equal("Binance", trade.Exchange);
        Assert.Equal("Hyperliquid", trade.Leader);
        Assert.Equal(source.ExitSeconds - source.EntrySeconds, trade.HoldSeconds!.Value, 9);
        Assert.Equal(run.StartedUtc.AddSeconds(source.EntrySeconds).ToString("O"), trade.OpenedAt);
        Assert.Equal(source.Quantity * source.EntryPrice, trade.Notional);
        Assert.Equal(source.ExitReason, trade.ExitReason);
        Assert.NotNull(trade.MaxFavorableBps);
        Assert.Equal((source.MaxFavorableBps, source.MaxAdverseBps), (trade.MaxFavorableBps, trade.MaxAdverseBps));
        // The simplified BBO model has no sizes: they follow from the configured nominal and fees.
        var bbo = run with { Settings = Depth with { ExecutionModel = "bbo" }, TradeLog = [source with { Quantity = null, NetQuote = null, FeesQuote = null }] };
        var estimated = Assert.Single(BotTrade.FromRun(bbo));
        Assert.Equal(Depth.OrderNotional / source.EntryPrice, estimated.Quantity, 12);
        Assert.Equal(source.NetBps / 10000 * Depth.OrderNotional, estimated.Gross - estimated.Fee, 12);
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
    public void LeverageLetsAnOrderExceedTheBalanceUpToBalanceTimesLeverage()
    {
        // About 1080 USDT of SOL: refused with 100 USDT at 10x, sent at 20x.
        var (refused, _) = Drive(new("SOL", "hyperliquid", "binance", 600, Depth, Budget: 100, Leverage: 10), Events(), long.MaxValue, _ => { });
        Assert.Contains(refused.Snapshot().Result!.Journal, x => x.Reason.Contains("с плечом 10x"));
        var (sent, _) = Drive(new("SOL", "hyperliquid", "binance", 600, Depth, Budget: 100, Leverage: 20), Events(), long.MaxValue, _ => { });
        Assert.Equal(1, sent.Snapshot().Result!.OrdersSent);
        Assert.Single(sent.Snapshot().Result!.TradeLog);
    }

    [Fact]
    public void LossNearingTheMarginClosesThePositionAsLiquidationRisk()
    {
        // The follower falls instead of catching up: about -45 b.p. after fees. At 125x the margin is 80 b.p.,
        // minus the assumed 50 b.p. maintenance: the position must be closed before that loss, whatever the stop.
        var falling = Events(data: StrategyReplayTests.Data().Select(x =>
            x.Ticks >= 120500 && x.Source == "binance-public" && x.Quote is { } q ? x with { Quote = q with { Bid = 119.49m, Ask = 119.51m } } : x).ToList());
        var settings = Depth with { StopLossBps = 1000 };
        var (session, _) = Drive(new("SOL", "hyperliquid", "binance", 600, settings, Budget: 100, Leverage: 125), falling, long.MaxValue, _ => { });
        var trade = Assert.Single(session.Snapshot().Result!.TradeLog);
        Assert.StartsWith("Риск ликвидации", trade.ExitReason);
        // Without leverage the same loss is held until another exit rule fires.
        var (plain, _) = Drive(new("SOL", "hyperliquid", "binance", 600, settings, Budget: 2000), falling, long.MaxValue, _ => { });
        Assert.DoesNotContain("ликвидации", Assert.Single(plain.Snapshot().Result!.TradeLog).ExitReason);
    }

    [Fact]
    public void MaximumLeverageIsReadFromPublicExchangeMetadata()
    {
        using var okx = JsonDocument.Parse("""{"data":[{"instId":"SOL-USDT-SWAP","lever":"50"}]}""");
        using var bybit = JsonDocument.Parse("""{"result":{"list":[{"symbol":"SOLUSDT","leverageFilter":{"maxLeverage":"75.00"}}]}}""");
        using var hyperliquid = JsonDocument.Parse("""{"universe":[{"name":"SOL","maxLeverage":20}]}""");
        Assert.Equal(50, ResearchMarkets.MaxLeverage(okx.RootElement, "okx", "SOL"));
        Assert.Equal(75, ResearchMarkets.MaxLeverage(bybit.RootElement, "bybit", "SOL"));
        Assert.Equal(20, ResearchMarkets.MaxLeverage(hyperliquid.RootElement, "hyperliquid", "SOL"));
        Assert.Null(ResearchMarkets.MaxLeverage(okx.RootElement, "okx", "ETH"));
        Assert.Null(ResearchMarkets.MaxLeverage(okx.RootElement, "binance", "SOL"));
    }
    [Fact]
    public void FixedLeaderTradesWithoutTheCalibrationVerdictAndMatchesReplay()
    {
        // An unreachable margin: "auto" finds no leader here; with the leader set, trading does not depend on it.
        var settings = Depth with { LeaderAdvantageBps = 100, Leader = "source" };
        var events = Events();
        var expected = Replay(events, settings, 10);
        Assert.NotEmpty(expected.TradeLog);
        var snapshot = Live(events, settings, 10).Snapshot();
        Assert.Equal(("finished", "hyperliquid"), (snapshot.State, snapshot.Leader));
        Assert.Equal(expected.TradeLog, snapshot.Result!.TradeLog);
        Assert.Equal(expected.Journal, snapshot.Result.Journal);
    }

    [Fact]
    public void BotWithFixedLeaderRunsEvenWhenItsOwnExchangeMovedFirst()
    {
        // The fixture moves Hyperliquid first; a bot on Hyperliquid with a Binance signal still runs with Binance as the leader
        // and is told that the reverse reaction was stronger.
        var request = new PaperRequest("SOL", "binance", "hyperliquid", 600, Depth with { ExecutionModel = "bbo", Leader = "source" });
        string? running = null;
        var (session, _) = Drive(request, Events(), 121000, s => running = s.Snapshot().Explanation);
        var snapshot = session.Snapshot();
        Assert.Equal(("finished", "binance", "hyperliquid"), (snapshot.State, snapshot.Leader, snapshot.ExecutionExchange));
        Assert.Contains("(задан)", running); Assert.Contains("Обратная реакция сильнее", running);
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
