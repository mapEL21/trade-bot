using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradeBot.Api;
using Xunit;

namespace TradeBot.Api.Tests;

public sealed class SignalBotTests : IDisposable
{
    private const long T0 = 1_760_000_000_000;
    private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "signal-bot-tests-" + Guid.NewGuid());

    // A long-side spike on a coin with a typical minute range of 0.25%: the 2× stop is 0.5% away from the 100.01 entry.
    private static LabEvent Spike(string symbol, long at, decimal typical = .25m, int direction = 1) =>
        new(symbol, "Вынос цены", direction, at, 100, "", 99.99m, 100.01m, null, null, typical);

    // Every second the same book unless a second is given its own (bid, ask); in 0.1 bp from the entry mid 100.
    private static LabPath Path(int index, int seconds = LabPath.Seconds, Dictionary<int, (decimal Bid, decimal Ask)>? at = null)
    {
        var samples = new int[]?[LabPath.Seconds];
        for (var k = 0; k < seconds; k++)
        {
            var (bid, ask) = at?.GetValueOrDefault(k) is { Bid: > 0 } own ? own : (100.19m, 100.21m);
            int b = LabPath.Rel(bid, 100), a = LabPath.Rel(ask, 100);
            samples[k] = [b, a, b, a];
        }
        return new(index, "Вынос цены", samples);
    }

    private static SignalRun Run(SignalParameters? p = null, decimal budget = 100, decimal loss = 30, long from = 0, long? to = null) =>
        new("run", "bot", from, to, DateTimeOffset.UnixEpoch, budget, loss, p ?? new SignalParameters());

    private static (LabRecording, Dictionary<int, LabPath>) Recording(LabEvent[] events, params LabPath[] paths) =>
        (new LabRecording(SignalDetector.Version, "r", T0 - 3_600_000, T0 + 3_600_000, 3, events), paths.ToDictionary(p => p.Index));

    [Fact]
    public void StopLosesTheChosenRiskOfTheDepositIncludingFees()
    {
        // Stop 0.5% below the 100.01 ask (99.51); the bid drops to 99.50 in the third second and the stop fills there.
        var path = Path(0, at: new() { [2] = (99.50m, 99.52m) });
        var result = SignalEngine.Run(Run(), [Recording([Spike("AUSDT", T0)], path)]);
        var trade = Assert.Single(result.Trades);
        // Size from the risk: 1.5 USDT ÷ (0.5% stop + 0.1% fees) = 250 USDT, 2.5× the 100 USDT deposit.
        Assert.Equal((250m, "long", "стоп", 3d), (trade.Notional!.Value, trade.Side, trade.ExitReason, trade.HoldSeconds!.Value));
        // 1.5 USDT, and a cent more for the fill a little below the stop.
        Assert.InRange(trade.Gross - trade.Fee, -1.53m, -1.5m);
        Assert.Equal(100 + trade.Gross - trade.Fee, result.Deposit, 3);
    }

    [Fact]
    public void TimeExitAtTheHoldingTimeAndAMissingSecondMovesItToTheNextBook()
    {
        var hold = new SignalParameters(HoldSeconds: 60);
        var trade = Assert.Single(SignalEngine.Run(Run(hold), [Recording([Spike("AUSDT", T0)], Path(0))]).Trades);
        Assert.Equal(("время", 60d, 100.19m), (trade.ExitReason, trade.HoldSeconds!.Value, trade.Exit));
        Assert.Equal(T0 + 250 + 60_000, DateTimeOffset.Parse(trade.At).ToUnixTimeMilliseconds());
        // No fresh book in the 60th second: the exit waits for the next one instead of taking a price that was not there.
        var gap = Path(0);
        gap.Samples[59] = null;
        var late = Assert.Single(SignalEngine.Run(Run(hold), [Recording([Spike("AUSDT", T0)], gap)]).Trades);
        Assert.Equal(("время (стакан обновился позже)", 61d), (late.ExitReason, late.HoldSeconds!.Value));
    }

    [Fact]
    public void ShortSideFadeAndAPositionCutByTheEndOfTheRecordingIsIncompleteNotClosed()
    {
        var fade = new SignalParameters(Mode: "fade", HoldSeconds: 5);
        var trade = Assert.Single(SignalEngine.Run(Run(fade), [Recording([Spike("AUSDT", T0)], Path(0))]).Trades);
        // Against an upward spike: a short sold at the 99.99 bid, bought back at the 100.21 ask.
        Assert.Equal(("short", 99.99m, 100.21m), (trade.Side, trade.Entry, trade.Exit));
        Assert.True(trade.Gross < 0);
        var cut = SignalEngine.Run(Run(fade), [Recording([Spike("AUSDT", T0)], Path(0, seconds: 3))]);
        Assert.Empty(cut.Trades);
        Assert.Equal((1, 100m), (cut.Incomplete, cut.Deposit));
    }

    [Fact]
    public void TradesAreSkippedWhenFeesWouldEatTheRiskOrLimitsAreReached()
    {
        // BTC-like: a 0.03% typical minute range makes a 0.06% stop; 0.1% fees would be 62% of the risk.
        var calm = SignalEngine.Run(Run(), [Recording([Spike("BTCUSDT", T0, typical: .03m)], Path(0))]);
        Assert.Empty(calm.Trades);
        Assert.Equal(1, calm.Skipped["комиссия заняла бы слишком большую часть риска"]);

        // Four coins at once with three positions allowed; a second signal on a coin already in a trade; one before the start.
        LabEvent[] events = [Spike("AUSDT", T0), Spike("BUSDT", T0 + 1000), Spike("CUSDT", T0 + 2000), Spike("DUSDT", T0 + 3000),
            Spike("AUSDT", T0 + 4000), Spike("EUSDT", T0 - 1000)];
        var result = SignalEngine.Run(Run(from: T0), [Recording(events, Enumerable.Range(0, events.Length).Select(i => Path(i)).ToArray())]);
        Assert.Equal(3, result.Trades.Length);
        Assert.Equal(5, result.Signals);
        Assert.Equal(1, result.Skipped["достигнут предел одновременных сделок"]);
        Assert.Equal(1, result.Skipped["уже в сделке по этой монете"]);
        // The leverage limit caps the size: with 1× the three positions share the 100 USDT deposit.
        var capped = SignalEngine.Run(Run(new SignalParameters(MaxLeverage: 1), from: T0),
            [Recording(events, Enumerable.Range(0, events.Length).Select(i => Path(i)).ToArray())]);
        Assert.Equal(100m, Assert.Single(capped.Trades).Notional!.Value);
        Assert.Equal(3, capped.Skipped["не хватает депозита с учётом плеча"]);
    }

    [Fact]
    public void ABotLimitedToSessionsSkipsSignalsOutsideThem()
    {
        // T0 is 09:06 UTC (Europe); a signal 6 hours later is in the US session.
        Assert.Equal("europe", TradingSessions.Of(T0));
        LabEvent[] events = [Spike("AUSDT", T0), Spike("BUSDT", T0 + 6 * 3_600_000)];
        var only = new SignalParameters(Sessions: ["us"]);
        Assert.Empty(only.Validate());
        Assert.NotEmpty(new SignalParameters(Sessions: ["mars"]).Validate());
        var result = SignalEngine.Run(Run(only), [Recording(events, Path(0), Path(1))]);
        Assert.Equal("BUSDT", Assert.Single(result.Trades).Symbol);
        Assert.Equal(1, result.Skipped["вне выбранных торговых сессий"]);
    }

    [Fact]
    public void LossLimitStopsNewEntriesAndTheDepositCarriesToTheNextRecording()
    {
        // Each trade is stopped out for 1.5% of the current deposit; with a 2 USDT limit the third signal is not traded.
        var stop = new Dictionary<int, (decimal, decimal)> { [0] = (99.50m, 99.52m) };
        var first = Recording([Spike("AUSDT", T0), Spike("BUSDT", T0 + 60_000)], Path(0, at: stop), Path(1, at: stop));
        var second = Recording([Spike("CUSDT", T0 + 7_200_000)], Path(0, at: stop));
        var result = SignalEngine.Run(Run(loss: 2), [first, second]);
        Assert.Equal(2, result.Trades.Length);
        // The second trade is sized from the deposit left after the first.
        Assert.True(result.Trades[1].Notional < result.Trades[0].Notional);
        Assert.Equal(1, result.Skipped["лимит убытка"]);
        Assert.NotNull(result.EntriesBlocked);
    }

    [Fact]
    public void DetectorKeepsThePriceAfterTheEntryAndTheCoinsTypicalRange()
    {
        var events = SignalLabTestsRecording();
        var detector = new SignalDetector(new Dictionary<string, decimal> { ["XUSDT"] = 300_000_000 }, liquidations: true);
        foreach (var e in events) detector.Process(e);
        var result = detector.Result("x");
        var burst = result.Events.Select((e, i) => (e, i)).Single(x => x.e.Type == "Всплеск объёма");
        var path = Assert.Single(detector.Paths(), p => p.Index == burst.i);
        // After the burst the book sits at 100.19/100.21; the entry mid was 100.
        var mid = (burst.e.EntryAsk!.Value + burst.e.EntryBid!.Value) / 2;
        Assert.Equal(100.19m, Math.Round(LabPath.Abs(path.Samples[10]![0], mid), 2));
        Assert.Equal(100.21m, Math.Round(LabPath.Abs(path.Samples[10]![1], mid), 2));
        // Ranges of the flat minutes before are zero: known but no stop can come from them.
        Assert.NotNull(burst.e.TypicalPct);
    }

    // The same recording as SignalLabTests: a flat market, then a burst of buys lifting the price.
    private static List<TradeBot.Recorder.CaptureEvent> SignalLabTestsRecording()
    {
        const long burst = T0 + 2_000_000;
        static string S(decimal v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TradeBot.Recorder.CaptureEvent Trade(long ms, decimal price, decimal q, bool buy) => new(TradeBot.Recorder.ScanRecorder.Market, "c", "market", DateTimeOffset.UnixEpoch, ms,
            $"{{\"stream\":\"xusdt@aggTrade\",\"data\":{{\"e\":\"aggTrade\",\"s\":\"XUSDT\",\"p\":\"{S(price)}\",\"q\":\"{S(q)}\",\"T\":{ms},\"m\":{(!buy).ToString().ToLowerInvariant()}}}}}");
        TradeBot.Recorder.CaptureEvent Book(long ms, decimal bid, decimal ask) => new(TradeBot.Recorder.ScanRecorder.Public, "c", "market", DateTimeOffset.UnixEpoch, ms,
            $"{{\"stream\":\"xusdt@depth20@500ms\",\"data\":{{\"e\":\"depthUpdate\",\"E\":{ms},\"T\":{ms},\"s\":\"XUSDT\",\"b\":[[\"{S(bid)}\",\"10\"]],\"a\":[[\"{S(ask)}\",\"10\"]]}}}}");
        var events = new List<TradeBot.Recorder.CaptureEvent>();
        for (var ms = T0; ms < burst + 400_000; ms += 500)
        {
            var after = ms >= burst + 500;
            events.Add(Book(ms, after ? 100.19m : 99.99m, after ? 100.21m : 100.01m));
            if (ms % 1000 == 0 && (ms < burst || ms >= burst + 2000)) events.Add(Trade(ms, ms < burst ? 100 : 100.2m, 1, ms % 2000 == 0));
        }
        for (var k = 0; k < 20; k++) events.Add(Trade(burst + k * 100, 100.1m + .005m * k, 50, true));
        return events.OrderBy(e => e.ReceivedTicks).ToList();
    }

    [Fact]
    public async Task SignalBotIsCreatedStartedFromAPastMomentShowsItsTradesAndStops()
    {
        var scans = System.IO.Path.Combine(root, "scans");
        var dir = System.IO.Path.Combine(scans, "20261016T000000Z-55555555555555555555555555555555");
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "summary.json"), "{}");
        File.WriteAllText(System.IO.Path.Combine(dir, "manifest.json"), "{}");
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var (recording, paths) = Recording([Spike("AUSDT", T0)], Path(0));
        File.WriteAllLines(System.IO.Path.Combine(dir, SignalLab.PathFile), paths.Values.Select(p => JsonSerializer.Serialize(p, json)));
        File.WriteAllText(System.IO.Path.Combine(dir, SignalLab.File), JsonSerializer.Serialize(recording, json));
        var db = System.IO.Path.Combine(root, "workspace.db");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Scanner:Path", scans);
            builder.ConfigureServices(services =>
            {
                // Never let a test open the real workspace database.
                services.RemoveAll<IWorkspaceStore>(); services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(db));
                services.RemoveAll<PaperRunStore>(); services.AddSingleton(_ => new PaperRunStore(db));

            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        try
        {
            var config = new BotConfig("Рывок", "Binance", "*", 100, 30, StrategyCatalog.SignalId, StrategyCatalog.SignalVersion,
                Signal: new SignalParameters(HoldSeconds: 60));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", config with { Signal = new(RiskPct: 9) })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", config with { Symbol = "ARBUSDT" })).StatusCode);
            var created = await client.PostAsJsonAsync("/api/v1/bots", config);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var bot = (await created.Content.ReadFromJsonAsync<Bot>(json))!;
            Assert.Equal(60, bot.Signal!.HoldSeconds);
            // Only the signal bot is left in the catalog.
            var strategies = (await client.GetFromJsonAsync<StrategyDefinition[]>("/api/v1/strategies", json))!;
            Assert.Equal(StrategyCatalog.SignalId, Assert.Single(strategies).Id);

            var start = await client.PostAsJsonAsync($"/api/v1/bots/{bot.Id}/signal/start", new SignalStart(T0 - 1000));
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/v1/bots/{bot.Id}/signal/start", new SignalStart())).StatusCode);
            var listed = (await client.GetFromJsonAsync<Workspace>("/api/v1/workspace", json))!.Bots.Single();
            Assert.Equal("running", listed.Status);
            var trade = Assert.Single(listed.Trades);
            Assert.Equal(("AUSDT", "время"), (trade.Symbol, trade.ExitReason));
            var runs = (await client.GetFromJsonAsync<SignalRunView[]>($"/api/v1/bots/{bot.Id}/signal", json))!;
            Assert.Equal((1, 1), (runs.Single().Result.Signals, runs.Single().Result.Recordings));

            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/bots/{bot.Id}/signal/stop", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/bots/{bot.Id}/signal/stop", null)).StatusCode);
            listed = (await client.GetFromJsonAsync<Workspace>("/api/v1/workspace", json))!.Bots.Single();
            Assert.Equal("stopped", listed.Status);
            Assert.Single(listed.Trades);
        }
        finally
        {
            factory.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
