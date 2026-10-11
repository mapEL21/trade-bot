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
    [Fact]
    public async Task ReplayPersistsSeparatelyAndRejectsInvalidSettings()
    {
        await Seed();
        using (var factory = Factory())
        using (var client = Client(factory))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/research/recordings/{Id}/replay")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/research/recordings/{Id}/replay", new ReplaySettings(CalibrationSeconds: 0))).StatusCode);
            var response = await client.PostAsJsonAsync($"/api/v1/research/recordings/{Id}/replay", new ReplaySettings());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var report = (await response.Content.ReadFromJsonAsync<ReplayReport>())!;
            Assert.Equal("too-short", report.Status); Assert.Empty(report.Scenarios);
            Assert.False(File.Exists(Path.Combine(root, Id, "analysis-v1.json")));
        }
        using var restarted = Factory(); using var reader = Client(restarted);
        var saved = await reader.GetFromJsonAsync<ReplayReport>($"/api/v1/research/recordings/{Id}/replay");
        Assert.Equal("too-short", saved!.Status);
        Assert.Equal(Id, saved.Recording.Id);
    }

    [Fact]
    public async Task DepthReplayLoadsRawBooksAndRecordedRulesThroughApi()
    {
        await SeedDepth();
        using var factory = Factory(); using var client = Client(factory);
        var response = await client.PostAsJsonAsync($"/api/v1/research/recordings/{Id}/replay", new ReplaySettings(OrderNotional: 1200, FeeBps: 1, SlippageBps: 0, MinEdgeBps: 1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = (await response.Content.ReadFromJsonAsync<ReplayReport>())!;
        Assert.Equal("evaluated", report.Status); Assert.Equal("depth", report.Settings.ExecutionModel);
        Assert.Equal(1, report.Instrument!.QuantityStep);
        Assert.All(report.Scenarios, s => Assert.NotNull(Assert.Single(s.TradeLog).NetQuote));
        var saved = (await client.GetFromJsonAsync<ReplayReport>($"/api/v1/research/recordings/{Id}/replay"))!;
        Assert.Equal(report.Scenarios[0].TradeLog, saved.Scenarios[0].TradeLog);
    }

    [Fact]
    public async Task MakerDepthReplayReadsRecordedAggressorTradesFromTheMarketFeed()
    {
        // 300 coins sold into the bid at 120.3 s clear the 100 visible ahead of the 10-coin maker buy.
        await SeedDepth([
            new("binance-market", "t", "connected", DateTimeOffset.UnixEpoch, 0, ""),
            new("binance-market", "t", "market", DateTimeOffset.UnixEpoch, 120300, """{"e":"aggTrade","s":"SOLUSDT","p":"119.99","q":"300","m":true}""")]);
        using var factory = Factory(); using var client = Client(factory);
        var response = await client.PostAsJsonAsync($"/api/v1/research/recordings/{Id}/replay",
            new ReplaySettings(OrderNotional: 1200, FeeBps: 1, SlippageBps: 0, MinEdgeBps: 1, EntryOrder: "maker"));
        var report = (await response.Content.ReadFromJsonAsync<ReplayReport>())!;
        Assert.Equal("evaluated", report.Status);
        var follow = report.Scenarios.Single(x => x.Mode == "follow" && x.DelayMs == 10);
        Assert.Equal(1, follow.MakerFills); Assert.Equal(119.99m, Assert.Single(follow.TradeLog).EntryPrice);
    }

    [Fact]
    public async Task SeriesRecomputesEachRecordingAndPersistsCombinedReport()
    {
        await SeedDepth();
        using (var factory = Factory())
        using (var client = Client(factory))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/research/series/replay")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/research/series/replay", new SeriesRequest([], new ReplaySettings()))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/research/series/replay",
                new SeriesRequest([Id, "20261010T100000Z-22222222222222222222222222222222"], new ReplaySettings()))).StatusCode);
            var response = await client.PostAsJsonAsync("/api/v1/research/series/replay",
                new SeriesRequest([Id], new ReplaySettings(OrderNotional: 1200, FeeBps: 1, SlippageBps: 0, MinEdgeBps: 1)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var series = (await response.Content.ReadFromJsonAsync<SeriesReport>())!;
            Assert.Equal(StrategyReplay.Version, series.Version);
            Assert.Equal("evaluated", Assert.Single(series.Recordings).Status);
            Assert.All(series.Totals, t => { Assert.Equal(1, t.Recordings); Assert.Equal(1, t.OrdersSent); Assert.Equal(1, t.Trades); });
            Assert.True(File.Exists(Path.Combine(root, Id, "strategy-replay-v1.json")));
        }
        using var restarted = Factory(); using var reader = Client(restarted);
        var saved = (await reader.GetFromJsonAsync<SeriesReport>("/api/v1/research/series/replay"))!;
        Assert.Equal(8, saved.Totals.Length); Assert.Equal(1200, saved.Settings.OrderNotional);
    }

    private async Task SeedDepth(IEnumerable<CaptureEvent>? extra = null)
    {
        await Seed(); var dir = Path.Combine(root, Id);
        using var metadata = JsonDocument.Parse("""{"symbols":[{"symbol":"SOLUSDT","baseAsset":"SOL","quoteAsset":"USDT","contractType":"PERPETUAL","filters":[{"filterType":"MARKET_LOT_SIZE","stepSize":"1","minQty":"1","maxQty":"10000"},{"filterType":"MIN_NOTIONAL","notional":"5"},{"filterType":"PRICE_FILTER","tickSize":"0.01"}]}]}""");
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new {
            schemaVersion = 3, startedUtc = DateTimeOffset.UnixEpoch, stopwatchFrequency = 1000,
            options = new { Coin = "SOL" }, exchangeMetadata = metadata.RootElement
        }));
        var events = new List<CaptureEvent>();
        static string S(decimal n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
        events.AddRange(extra ?? []);
        await File.WriteAllLinesAsync(Path.Combine(dir, "events.ndjson"), events.OrderBy(x => x.ReceivedTicks).Select(x => JsonSerializer.Serialize(x)));
    }

    [Fact]
    public async Task LegacySavedReplayIsExplicitlyLabelledBbo()
    {
        await Seed(); using var factory = Factory(); using var client = Client(factory);
        var response = await client.PostAsJsonAsync($"/api/v1/research/recordings/{Id}/replay", new ReplaySettings());
        response.EnsureSuccessStatusCode();
        var path = Path.Combine(root, Id, "strategy-replay-v1.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["version"] = "0.1.0"; json["settings"]!.AsObject().Remove("executionModel");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var saved = (await client.GetFromJsonAsync<ReplayReport>($"/api/v1/research/recordings/{Id}/replay"))!;
        Assert.Equal("bbo", saved.Settings.ExecutionModel);
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
