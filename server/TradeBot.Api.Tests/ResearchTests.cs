using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

public class ResearchTests
{
    private static RecorderOptions Options => new("ARB", "ARBUSDT", 60, "unused", 1000000);
    private const string Hl = """{"channel":"bbo","data":{"coin":"ARB","time":100,"bbo":[{"px":"1.01","sz":"10"},{"px":"1.03","sz":"20"}]}}""";
    private const string Bn = """{"stream":"arbusdt@bookTicker","data":{"e":"bookTicker","s":"ARBUSDT","u":100,"b":"1.00","B":"10","a":"1.02","A":"15"}}""";

    private static CaptureEvent Event(string source, string kind, string raw, long? ticks = null, string connection = "c1") =>
        new(source, connection, kind, DateTimeOffset.UtcNow, ticks ?? Stopwatch.GetTimestamp(), raw);
    private static void Connect(ResearchSession session, string source) => session.Recorded(Event(source, "connected", ""), 1, 1);

    [Fact]
    public void ComparesOnlyFreshBbo_TradesCannotRefreshOldQuote()
    {
        using var session = new ResearchSession("test", Options);
        session.Started("unused");
        Connect(session, "hyperliquid"); Connect(session, "binance-public");
        session.Recorded(Event("hyperliquid", "market", Hl), 2, 2);
        session.Recorded(Event("binance-public", "market", Bn), 3, 3);
        Assert.NotNull(session.Snapshot().RawDifferencePercent);

        // A new connection begins with an old BBO. A fresh trade does not make that price fresh.
        Connect(session, "hyperliquid");
        session.Recorded(Event("hyperliquid", "market", Hl, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 4), 4, 4);
        session.Recorded(Event("hyperliquid", "market", """{"channel":"trades","data":[{"coin":"ARB","px":"1.03"}]}"""), 5, 5);
        var snapshot = session.Snapshot();
        Assert.Null(snapshot.RawDifferencePercent);
        Assert.True(snapshot.Feeds.First(x => x.Id == "hyperliquid").Quote!.Stale);
        Assert.Equal("live", snapshot.Feeds.First(x => x.Id == "hyperliquid").Status);
    }

    [Fact]
    public void GapInvalidatesQuote_AndIgnoresEventsFromPreviousConnection()
    {
        using var session = new ResearchSession("test", Options);
        session.Started("unused"); Connect(session, "hyperliquid");
        session.Recorded(Event("hyperliquid", "market", Hl), 2, 2);
        session.Recorded(Event("hyperliquid", "gap", ""), 3, 3);
        Assert.Null(session.Snapshot().Feeds[0].Quote);
        session.Recorded(Event("hyperliquid", "connecting", "", connection: "c2"), 4, 4);
        session.Recorded(Event("hyperliquid", "market", Hl), 5, 5);
        Assert.Null(session.Snapshot().Feeds[0].Quote);
        Assert.Equal(1, session.Snapshot().Gaps);
    }

    [Fact]
    public void OutOfOrderQuoteCannotReplaceNewerQuote_AndStopDisablesComparison()
    {
        using var session = new ResearchSession("test", Options);
        session.Started("unused"); Connect(session, "hyperliquid"); Connect(session, "binance-public");
        session.Recorded(Event("hyperliquid", "market", Hl), 2, 2);
        session.Recorded(Event("binance-public", "market", Bn), 3, 3);
        session.Recorded(Event("binance-public", "market", Bn.Replace("\"u\":100", "\"u\":99").Replace("1.00", "0.99")), 4, 4);
        Assert.Equal("1.00", session.Snapshot().Feeds.First(x => x.Id == "binance-public").Quote!.Bid);
        session.RequestStop();
        Assert.Null(session.Snapshot().RawDifferencePercent);
        Assert.All(session.Snapshot().Feeds.Where(x => x.Quote is not null), f => Assert.True(f.Quote!.Stale));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("completed-with-gaps")]
    [InlineData("failed")]
    [InlineData("stopped")]
    public async Task TerminalSessionFreezesQuoteAndEventAges(string status)
    {
        using var session = new ResearchSession("test", Options);
        session.Started("unused");
        Connect(session, "hyperliquid"); Connect(session, "binance-public");
        session.Recorded(Event("hyperliquid", "market", Hl), 2, 2);
        session.Recorded(Event("binance-public", "market", Bn), 3, 3);
        if (status == "failed") session.Fail("test failure");
        else if (status == "stopped") session.EndCancelled();
        else session.Finished(new(DateTimeOffset.UtcNow, status, null, 3, 3, 0, [], new { }));

        var first = session.Snapshot();
        await Task.Delay(30);
        var later = session.Snapshot();
        Assert.False(later.Active);
        Assert.Null(later.RawDifferencePercent);
        Assert.Equal(first.ElapsedSeconds, later.ElapsedSeconds);
        Assert.Equal(first.Feeds, later.Feeds);
        Assert.All(later.Feeds.Where(f => f.Quote is not null), f => Assert.True(f.Quote!.Stale));
    }

    [Theory]
    [InlineData("\"1.04\"", "\"1.02\"")]
    [InlineData("\"0\"", "\"1.02\"")]
    [InlineData("null", "\"1.02\"")]
    public void InvalidBboClearsPreviouslyDisplayedPrice(string bid, string ask)
    {
        using var session = new ResearchSession("test", Options);
        session.Started("unused"); Connect(session, "binance-public");
        session.Recorded(Event("binance-public", "market", Bn), 2, 2);
        var bad = Bn.Replace("\"1.00\"", bid).Replace("\"1.02\"", ask);
        session.Recorded(Event("binance-public", "market", bad), 3, 3);
        Assert.Null(session.Snapshot().Feeds.First(x => x.Id == "binance-public").Quote);
    }

    [Fact]
    public async Task ApiOwnsSession_RejectsConcurrentStartsAndStaleStopIds()
    {
        var runner = new ControlledRunner();
        using var factory = Factory(runner);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        var requests = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync("/api/v1/research", new ResearchRequest("ARB", 60, 5))));
        Assert.Single(requests, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(requests, r => r.StatusCode == HttpStatusCode.Conflict);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = (await client.GetFromJsonAsync<ResearchSnapshot>("/api/v1/research"))!;
        Assert.True(snapshot.Active);
        Assert.False(snapshot.ExecutionEnabled);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/research/{Guid.NewGuid()}/stop", null)).StatusCode);
        using var anotherClient = factory.CreateClient();
        Assert.Equal(snapshot.Id, (await anotherClient.GetFromJsonAsync<ResearchSnapshot>("/api/v1/research"))!.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/research/{snapshot.Id}/stop", null)).StatusCode);
        await runner.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await client.GetFromJsonAsync<ResearchSnapshot>("/api/v1/research"))!.Active);
        Assert.Equal(1, runner.Runs);
    }

    [Theory]
    [InlineData("{\"coin\":\"ARB\",\"seconds\":3601,\"maxMb\":5}")]
    [InlineData("{\"coin\":\"ARB\\n\",\"seconds\":60,\"maxMb\":5}")]
    [InlineData("{\"coin\":\"ARB\",\"seconds\":60,\"maxMb\":0}")]
    [InlineData("{\"coin\":null,\"seconds\":60,\"maxMb\":5}")]
    [InlineData("{\"coin\":\"ARB\",\"seconds\":60,\"maxMb\":5,\"output\":\"C:/secret\"}")]
    public async Task ApiRejectsUnsafeParametersWithoutStartingRecorder(string json)
    {
        var runner = new ControlledRunner();
        using var factory = Factory(runner);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        var response = await client.PostAsync("/api/v1/research", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, runner.Runs);
    }

    [Fact]
    public async Task ApiBlocksCrossSiteStart_AndStreamsIdleStateWithoutStartingNetworkFeeds()
    {
        var runner = new ControlledRunner();
        using var factory = Factory(runner);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/research", new ResearchRequest("ARB", 60, 5))).StatusCode);
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/research", new ResearchRequest("ARB", 60, 5))).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.GetAsync("/api/v1/research/stream", HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token));
        var line = await reader.ReadLineAsync(cancel.Token);
        Assert.Contains("\"state\":\"idle\"", line);
        Assert.Contains("\"executionEnabled\":false", line);
        Assert.Equal(0, runner.Runs);
        cancel.Cancel();
    }

    [Fact]
    public async Task PaperApiRunsOneSessionOnTheRecorderAndStopsDuringCalibration()
    {
        var runner = new ControlledRunner(withManifest: true);
        using var factory = Factory(runner);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        var settings = new ReplaySettings(ExecutionModel: "bbo");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/paper", new PaperRequest("ARB", "hyperliquid", "binance", 100, settings))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/paper", new PaperRequest("ARB", "hyperliquid", "binance", 600, settings, DelayMs: 0))).StatusCode);
        Assert.Equal(0, runner.Runs);
        var start = await client.PostAsJsonAsync("/api/v1/paper", new PaperRequest("ARB", "hyperliquid", "binance", 600, settings));
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/paper", new PaperRequest("ARB", "hyperliquid", "binance", 600, settings))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/research", new ResearchRequest("ARB", 60, 5))).StatusCode);
        PaperSnapshot paper;
        do paper = (await client.GetFromJsonAsync<PaperSnapshot>("/api/v1/paper"))!; while (paper.State == "starting");
        Assert.Equal("calibrating", paper.State); Assert.False(paper.ExecutionEnabled);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/paper/{Guid.NewGuid()}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/paper/{paper.Id}/stop", null)).StatusCode);
        await runner.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do paper = (await client.GetFromJsonAsync<PaperSnapshot>("/api/v1/paper", wait.Token))!; while (paper.State == "calibrating");
        Assert.Equal("stopped", paper.State); Assert.Null(paper.Result); Assert.Equal(1, runner.Runs);
        // The final record is written right after the session completes; wait for it before the database is removed.
        var runs = factory.Services.GetRequiredService<PaperRunStore>();
        PaperRunRecord[] saved;
        do saved = await runs.ListAsync(null, 5, wait.Token); while (saved.FirstOrDefault()?.State != "stopped");
        Assert.Equal(paper.Id, saved[0].Id); Assert.NotNull(saved[0].EndedUtc);
    }

    [Fact]
    public async Task LeaderLagBotRunsOnlyInSimulationAndIsLockedWhileRunning()
    {
        var runner = new ControlledRunner(withManifest: true);
        using var factory = Factory(runner);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        var settings = new ReplaySettings(ExecutionModel: "bbo");
        static BotConfig Config(LeaderLagParameters? p) => new("Поводырь", "Binance", "ARBUSDT", 10, 1, "leader-lag", StrategyReplay.Version, p);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", Config(null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", Config(new("Binance", settings)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", Config(new("Hyperliquid", settings with { OrderNotional = 50 })))).StatusCode);
        // 10 USDT at 5x covers a 50 USDT order; leverage outside 1-125 is refused.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/bots", Config(new("Hyperliquid", settings with { OrderNotional = 50 }, Leverage: 5)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", Config(new("Hyperliquid", settings, Leverage: 0)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", Config(new("Hyperliquid", settings, Leverage: 126)))).StatusCode);
        // One-letter tickers from the pair list, such as W, must be accepted.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/bots", Config(new("Hyperliquid", settings)) with { Symbol = "WUSDT" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots",
            new BotConfig("Импульс", "Binance", "ARBUSDT", 10, 1, "impulse", "0.1.0", new("Hyperliquid", settings)))).StatusCode);
        var created = await client.PostAsJsonAsync("/api/v1/bots", Config(new("Hyperliquid", settings, DelayMs: 25)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var bot = (await created.Content.ReadFromJsonAsync<Bot>())!;
        Assert.Equal(25, bot.Parameters!.DelayMs); Assert.Equal("draft", bot.Status);
        var impulse = (await (await client.PostAsJsonAsync("/api/v1/bots", new BotConfig("Импульс", "Binance", "ARBUSDT", 10, 1, "impulse", "0.1.0")))
            .Content.ReadFromJsonAsync<Bot>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/bots/{impulse.Id}/start", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/v1/bots/{bot.Id}/start", new PaperStart(600))).StatusCode);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        async Task<string> Status() => (await client.GetFromJsonAsync<Workspace>("/api/v1/workspace"))!.Bots.Single(x => x.Id == bot.Id).Status;
        Assert.Equal("calibrating", await Status());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/v1/bots/{bot.Id}", Config(new("OKX", settings)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/v1/bots/{bot.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/bots/{bot.Id}/pause", null)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<PaperSnapshot>("/api/v1/paper"))!.Paused);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/bots/{bot.Id}/stop", null)).StatusCode);
        await runner.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        PaperRunRecord[] runs;
        do runs = (await client.GetFromJsonAsync<PaperRunRecord[]>($"/api/v1/bots/{bot.Id}/runs", wait.Token))!; while (runs.FirstOrDefault()?.EndedUtc is null);
        var run = Assert.Single(runs);
        Assert.Equal(("Поводырь", "binance", "hyperliquid", 25), (run.BotName, run.Exchange, run.SignalExchange, run.DelayMs));
        Assert.Equal("stopped", await Status());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/bots/{bot.Id}/stop", null)).StatusCode);
    }

    private static WebApplicationFactory<Program> Factory(IRecordingRunner runner) => new ResearchFactory(runner);

    private sealed class ResearchFactory(IRecordingRunner runner) : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "trade-bot-research-tests-" + Guid.NewGuid());
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRecordingRunner>(); services.AddSingleton(runner);
            services.RemoveAll<IWorkspaceStore>();
            services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(Path.Combine(directory, "workspace.db")));
            // Never let a test open the real workspace database.
            services.RemoveAll<PaperRunStore>();
            services.AddSingleton(_ => new PaperRunStore(Path.Combine(directory, "workspace.db")));
        });
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = Path.Combine(directory, "workspace.db"), DefaultTimeout = 5 }.ToString());
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }

    private sealed class ControlledRunner(bool withManifest = false) : IRecordingRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs;
        public async Task<int> RunAsync(RecorderOptions options, IRecordingObserver observer, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            var directory = "test-data";
            if (withManifest)
            {
                directory = Path.Combine(Path.GetTempPath(), "trade-bot-paper-tests-" + Guid.NewGuid());
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "manifest.json"), "{\"stopwatchFrequency\":1000}");
            }
            observer.Started(directory); Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            observer.Finished(new(DateTimeOffset.UtcNow, "completed", null, 0, 0, 0, [], new { }));
            Finished.TrySetResult();
            return 0;
        }
    }
}
