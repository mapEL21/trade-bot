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

    private static WebApplicationFactory<Program> Factory(IRecordingRunner runner) => new ResearchFactory(runner);

    private sealed class ResearchFactory(IRecordingRunner runner) : WebApplicationFactory<Program>
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "trade-bot-research-tests-" + Guid.NewGuid());
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRecordingRunner>(); services.AddSingleton(runner);
            services.RemoveAll<IWorkspaceStore>();
            services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(Path.Combine(directory, "workspace.db")));
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

    private sealed class ControlledRunner : IRecordingRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs;
        public async Task<int> RunAsync(RecorderOptions options, IRecordingObserver observer, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            observer.Started("test-data"); Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            observer.Finished(new(DateTimeOffset.UtcNow, "completed", null, 0, 0, 0, [], new { }));
            Finished.TrySetResult();
            return 0;
        }
    }
}
