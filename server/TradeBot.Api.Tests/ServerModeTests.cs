using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

// Running on a server: one configured private host name, the built interface served by the API, a scanner that never stops.
public sealed class ServerModeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "server-mode-tests-" + Guid.NewGuid());

    private WebApplicationFactory<Program> Factory(Dictionary<string, string?> settings, IScanRunner? runner = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Market:Path"] = Path.Combine(root, "market"), ["Scanner:Path"] = Path.Combine(root, "scans"),
                ["Storage:Path"] = Path.Combine(root, "workspace.db")
            }.Concat(settings).ToDictionary()))
            .ConfigureServices(services =>
            {
                var db = Path.Combine(root, "workspace.db");
                services.RemoveAll<IWorkspaceStore>(); services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(db));
                services.RemoveAll<PaperRunStore>(); services.AddSingleton(_ => new PaperRunStore(db));
                services.RemoveAll<BotTemplateStore>(); services.AddSingleton(_ => new BotTemplateStore(db));
                if (runner is not null) { services.RemoveAll<IScanRunner>(); services.AddSingleton(runner); }
            }));

    private static HttpRequestMessage Request(HttpMethod method, string path, string host, string? origin = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Host = host;
        request.Headers.Add("X-TradeBot-Client", "web");
        if (origin is not null) request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task OnlyTheConfiguredPrivateHostIsAcceptedAndTheBuiltInterfaceIsServed()
    {
        var ui = Path.Combine(root, "ui"); Directory.CreateDirectory(ui);
        File.WriteAllText(Path.Combine(ui, "index.html"), "<!doctype html><title>Trade Bot</title>");
        using var factory = Factory(new() { ["Remote:Host"] = "bot.example.ts.net", ["Ui:Path"] = ui });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Get, "/api/v1/health", "bot.example.ts.net"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Get, "/api/v1/health", "evil.example.com"))).StatusCode);
        // A change from the interface opened at the private address is allowed; from another site it is not.
        // (409: the request got through to the scanner, which has no such scan; 403 would mean it was refused at the door.)
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/scanner/{Guid.NewGuid()}/stop",
            "bot.example.ts.net", "https://bot.example.ts.net"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Post, $"/api/v1/scanner/{Guid.NewGuid()}/stop",
            "bot.example.ts.net", "https://evil.example.com"))).StatusCode);
        var page = await client.SendAsync(Request(HttpMethod.Get, "/", "bot.example.ts.net"));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Trade Bot", await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get, "/api/v1/nothing", "bot.example.ts.net"))).StatusCode);
        // Without Remote:Host the server stays local-only.
        using var local = Factory(new());
        Assert.Equal(HttpStatusCode.Forbidden, (await local.CreateClient().SendAsync(Request(HttpMethod.Get, "/api/v1/health", "bot.example.ts.net"))).StatusCode);
    }

    [Fact]
    public async Task ContinuousScannerStartsWithTheServerChainsRecordingsAndStopsOnRequest()
    {
        var runner = new ChainRunner();
        using var factory = Factory(new() { ["Scanner:AutoStart"] = "true", ["Scanner:MaxSymbols"] = "5", ["Scanner:Exclude"] = "BTCUSDT, ETHUSDT" }, runner);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        // Each fake recording ends at once, as if its hour was over; the next one must follow without a request.
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ScanSnapshot snapshot;
        do snapshot = (await client.GetFromJsonAsync<ScanSnapshot>("/api/v1/scanner", wait.Token))!; while (snapshot.Segment < 3);
        Assert.True(snapshot.Continuous);
        Assert.Equal((5, 3600), (runner.Options!.MaxSymbols, runner.Options.Seconds));
        Assert.Equal(["BTCUSDT", "ETHUSDT"], runner.Options.Exclude!);
        runner.Hold = true;
        // Read the snapshot only once the held recording runs: one read earlier may still show the segment before it.
        while (!runner.Holding) await Task.Delay(20, wait.Token);
        do snapshot = (await client.GetFromJsonAsync<ScanSnapshot>("/api/v1/scanner", wait.Token))!; while (snapshot.State != "scanning");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/scanner/{snapshot.Id}/stop", null)).StatusCode);
        do snapshot = (await client.GetFromJsonAsync<ScanSnapshot>("/api/v1/scanner", wait.Token))!; while (snapshot.State == "scanning");
        var runs = runner.Runs;
        await Task.Delay(300);
        Assert.Equal(("stopped", runs), (snapshot.State, runner.Runs));
    }

    private sealed class ChainRunner : IScanRunner
    {
        public volatile bool Hold, Holding;
        public int Runs;
        public ScanOptions? Options;
        public async Task<int> RunAsync(ScanOptions options, IRecordingObserver observer, CancellationToken ct)
        {
            Options = options;
            var dir = Path.Combine(options.Output, $"run-{Interlocked.Increment(ref Runs)}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new { symbols = new[] { new ScanSymbol("XUSDT", 1, 1) } }));
            observer.Started(dir);
            if (Hold)
            {
                Holding = true;
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            }
            else await Task.Delay(50, ct);
            observer.Finished(new(DateTimeOffset.UtcNow, "completed", null, 0, 0, 0, [], new { }));
            return 0;
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
