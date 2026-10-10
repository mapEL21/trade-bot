using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

public sealed class AnalysisApiTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "analysis-tests-" + Guid.NewGuid());
    private const string Id = "20261010T100000Z-11111111111111111111111111111111";
    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Market:Path"] = root, ["Storage:Path"] = Path.Combine(root, "workspace.db") })));
    private async Task Seed(bool finished = true, bool malformed = false, int version = 3)
    {
        var dir = Path.Combine(root, Id); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new {
            schemaVersion = version, startedUtc = DateTimeOffset.UtcNow, stopwatchFrequency = 1000,
            options = new { Coin = "SOL" }
        }));
        if (finished) await File.WriteAllTextAsync(Path.Combine(dir, "summary.json"), "{\"status\":\"completed\"}");
        var events = new[] {
            new CaptureEvent("hyperliquid", "c", "connected", DateTimeOffset.UtcNow, 0, ""),
            new CaptureEvent("binance-public", "c", "connected", DateTimeOffset.UtcNow, 0, ""),
            new CaptureEvent("hyperliquid", "c", "market", DateTimeOffset.UtcNow, 1,
                """{"channel":"bbo","data":{"time":1,"bbo":[{"px":"100","sz":"10"},{"px":"101","sz":"10"}]}}"""),
            new CaptureEvent("binance-public", "c", "market", DateTimeOffset.UtcNow, 1,
                """{"stream":"solusdt@bookTicker","data":{"e":"bookTicker","u":1,"b":"100","B":"10","a":"101","A":"10"}}"""),
            new CaptureEvent("hyperliquid", "c", "control", DateTimeOffset.UtcNow, 2000, "")
        };
        await File.WriteAllLinesAsync(Path.Combine(dir, "events.ndjson"), malformed ? ["broken"] : events.Select(x => JsonSerializer.Serialize(x)));
    }
    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web"); return client;
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ReadsLegacyAndPersistsReportAcrossRestart(int version)
    {
        await Seed(version: version);
        using (var factory = Factory())
        using (var client = Client(factory))
        {
            var list = await client.GetFromJsonAsync<RecordingList>("/api/v1/research/recordings");
            Assert.Equal(Id, Assert.Single(list!.Items).Id);
            var response = await client.PostAsJsonAsync($"/api/v1/research/recordings/{Id}/analysis", new AnalysisSettings());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var report = (await response.Content.ReadFromJsonAsync<AnalysisReport>())!;
            Assert.All(report.Feeds, feed => Assert.Equal(1, feed.Quotes));
            Assert.Equal(5, report.Events);
        }
        using var restarted = Factory(); using var reader = Client(restarted);
        var saved = await reader.GetFromJsonAsync<AnalysisReport>($"/api/v1/research/recordings/{Id}/analysis");
        Assert.Equal(Id, saved!.Recording.Id);
        Assert.True(Assert.Single((await reader.GetFromJsonAsync<RecordingList>("/api/v1/research/recordings"))!.Items).HasReport);
    }
    [Theory]
    [InlineData(false, false, HttpStatusCode.Conflict)]
    [InlineData(true, true, HttpStatusCode.UnprocessableEntity)]
    public async Task RejectsIncompleteOrDamagedFiles(bool finished, bool malformed, HttpStatusCode status)
    {
        await Seed(finished, malformed); using var factory = Factory(); using var client = Client(factory);
        var response = await client.PostAsJsonAsync($"/api/v1/research/recordings/{Id}/analysis", new AnalysisSettings());
        Assert.Equal(status, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(root, Id, "analysis-v1.json")));
    }
    [Fact]
    public async Task RejectsArbitraryIdsAndInvalidSettings()
    {
        await Seed(); using var factory = Factory(); using var client = Client(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/research/recordings/not-a-recording/analysis", new AnalysisSettings())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/research/recordings/{Id}/analysis", new AnalysisSettings(FeeBps: -1))).StatusCode);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
