using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

public class LevelScannerTests
{
    private const long Start = 28_333_333L * 60000; // a whole minute
    private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);

    // 60 minutes ranging 98.5-99.5 with highs at 100 at minutes 10 and 30 and 99.8 at minute 52: a resistance with 3 touches.
    internal static List<Candle> Range()
    {
        var list = new List<Candle>();
        for (var i = 0; i < 60; i++)
        {
            var high = i is 10 or 30 ? 100m : i == 52 ? 99.8m : 99.5m;
            var close = i >= 45 ? 99.4m : 99m;
            list.Add(new(Start + i * 60000L, 99m, high, 98.5m, close, 1000));
        }
        return list;
    }

    private static CaptureEvent History(string symbol, IEnumerable<Candle> candles)
    {
        // The recorder keeps the still open kline as the last one; the scanner drops it.
        var rows = candles.Append(new(Start + 60 * 60000L, 99.4m, 99.4m, 99.4m, 99.4m, 0)).Select(c =>
            $"[{c.OpenMs},\"{S(c.Open)}\",\"{S(c.High)}\",\"{S(c.Low)}\",\"{S(c.Close)}\",\"1\",{c.OpenMs + 59999},\"{S(c.QuoteVolume)}\",10,\"0\",\"0\",\"0\"]");
        return new(ScanRecorder.Rest, "history", "history", DateTimeOffset.UnixEpoch, 0, $"{{\"symbol\":\"{symbol}\",\"interval\":\"1m\",\"klines\":[{string.Join(',', rows)}]}}");
    }

    private static CaptureEvent Trade(string symbol, long ms, decimal price, decimal quantity, bool buy) =>
        new(ScanRecorder.Market, "c", "market", DateTimeOffset.UnixEpoch, ms,
            $"{{\"stream\":\"{symbol.ToLowerInvariant()}@aggTrade\",\"data\":{{\"e\":\"aggTrade\",\"s\":\"{symbol}\",\"p\":\"{S(price)}\",\"q\":\"{S(quantity)}\",\"T\":{ms},\"m\":{(!buy).ToString().ToLowerInvariant()}}}}}");

    // Three quiet minutes at 99.5, then a burst of large buys that crosses the level, then the given follow-up prices.
    internal static List<CaptureEvent> Breakout(string symbol, params decimal[] after)
    {
        var events = new List<CaptureEvent> { History(symbol, Range()) };
        var t = Start + 60 * 60000L;
        for (var i = 0; i < 180; i++) events.Add(Trade(symbol, t + i * 1000L, 99.5m, 1, i % 2 == 0));
        t += 180_000;
        for (var i = 0; i < 15; i++) events.Add(Trade(symbol, t + i * 300L, 100m, 10, true));
        events.Add(Trade(symbol, t + 5000, 100.06m, 10, true));
        for (var i = 0; i < after.Length; i++) events.Add(Trade(symbol, t + 6000 + i * 1000L, after[i], 1, true));
        return events;
    }

    private static LevelScanner Run(List<CaptureEvent> events, string symbol = "XUSDT")
    {
        var scanner = new LevelScanner(new ScanSettings(), new Dictionary<string, decimal> { [symbol] = 300_000_000 });
        foreach (var e in events) scanner.Process(e);
        return scanner;
    }

    [Fact]
    public void FindsAResistanceZoneFromSeparatedTouchesAndDropsBrokenOrCrowdedOnes()
    {
        var level = Assert.Single(LevelScanner.FindLevels(Range(), new ScanSettings()), l => l.Side == "resistance");
        // The zone follows the coin's own range: 0.3 x the average 1-minute range (about 1) takes in 99.8 as a third touch.
        Assert.Equal((100m, 3, true), (level.Price, level.Touches, level.PeriodExtreme));
        // A close above the zone after the first touch means it is already broken.
        var broken = Range(); broken[40] = broken[40] with { High = 100.6m, Close = 100.5m };
        Assert.DoesNotContain(LevelScanner.FindLevels(broken, new ScanSettings()), l => l.Side == "resistance" && l.Price == 100);
        // One touch is not a level.
        var single = Range().Select(c => c.High == 100 && c.OpenMs != Start + 10 * 60000L || c.High == 99.8m ? c with { High = 99.5m } : c).ToList();
        Assert.DoesNotContain(LevelScanner.FindLevels(single, new ScanSettings()), l => l.Side == "resistance");
        // Touches less than 5 minutes apart do not make a level either.
        Assert.DoesNotContain(LevelScanner.FindLevels(Range(), new ScanSettings(MinTouchGapMinutes: 60)), l => l.Side == "resistance");
    }

    [Fact]
    public void BreakoutWithActiveTapeIsScoredAndReachesItsTarget()
    {
        var scanner = Run(Breakout("XUSDT", 100.5m, 101m, 101.6m));
        var candidate = Assert.Single(scanner.Candidates);
        Assert.Equal(("long", 100m, 100.06m, 3, "target"), (candidate.Side, candidate.Level, candidate.Price, candidate.Touches, candidate.Result));
        Assert.True(candidate.MaxFavorablePct >= 1.5m);
        int Points(string name) => candidate.Items.Single(x => x.Name == name).Points;
        Assert.Equal(20, Points("Активность ленты"));
        Assert.Equal(10, Points("Направление ленты"));
        Assert.Equal(10, Points("Крупный принт"));
        Assert.Equal(18, Points("Касания"));
        Assert.Equal(5, Points("Объём монеты"));
        Assert.True(candidate.Score >= 60, candidate.Score.ToString());
        Assert.Equal(candidate.Items.Sum(x => x.Points), candidate.Score);
        // A level breaks once; it is not drawn again in this session.
        Assert.DoesNotContain(scanner.Detail("XUSDT")!.Levels, l => l.Price == 100);
    }

    [Fact]
    public void BreakoutThatFallsBackBehindTheLevelIsAStopAndAnUnfinishedOneStaysOpen()
    {
        Assert.Equal("stop", Assert.Single(Run(Breakout("XUSDT", 100.2m, 99.8m)).Candidates).Result);
        var open = Assert.Single(Run(Breakout("XUSDT", 100.3m)).Candidates);
        Assert.Equal("open", open.Result); Assert.Null(open.ResolvedMs);
        Assert.Equal(Math.Round((100.3m / 100.06m - 1) * 100, 6), Math.Round(open.LastPct, 6));
    }

    [Fact]
    public void SymbolsAreSelectedByVolumeAndExclusions()
    {
        static string Info(params string[] symbols) => JsonSerializer.Serialize(new
        {
            symbols = symbols.Select(s => new { symbol = s, baseAsset = s[..^4], quoteAsset = "USDT", contractType = "PERPETUAL", status = "TRADING" })
        });
        var tickers = JsonSerializer.Serialize(new[]
        {
            new { symbol = "AUSDT", quoteVolume = "500000000", lastPrice = "1" },
            new { symbol = "BUSDT", quoteVolume = "200000000", lastPrice = "2" },
            new { symbol = "CUSDT", quoteVolume = "100000000", lastPrice = "3" },
            new { symbol = "DUSDT", quoteVolume = "900000000", lastPrice = "4" },
            new { symbol = "EUSDT", quoteVolume = "300000000", lastPrice = "5" }
        });
        var options = new ScanOptions("x", 600, 1048576, MaxSymbols: 2, Exclude: ["EUSDT"]);
        // D is not listed as a trading perpetual, E is excluded, C is below 150 million.
        Assert.Equal(["AUSDT", "BUSDT"], ScanRecorder.SelectSymbols(Info("AUSDT", "BUSDT", "CUSDT", "EUSDT"), tickers, options).Select(x => x.Symbol));
        var symbols = new HashSet<string> { "AUSDT" };
        Assert.Equal("market", ScanRecorder.Classify("""{"stream":"ausdt@aggTrade","data":{"e":"aggTrade","s":"AUSDT"}}""", symbols));
        Assert.Equal("control", ScanRecorder.Classify("""{"result":null,"id":1}""", symbols));
        Assert.Throws<InvalidDataException>(() => ScanRecorder.Classify("""{"stream":"busdt@aggTrade","data":{"e":"aggTrade","s":"BUSDT"}}""", symbols));
        Assert.Throws<ArgumentException>(() => new ScanOptions("x", 30, 1048576).Validate());
    }

    [Fact]
    public async Task ScannerApiRunsOneScanShowsCandidatesAndSavesThemOnStop()
    {
        var directory = Path.Combine(Path.GetTempPath(), "trade-bot-scan-tests-" + Guid.NewGuid());
        var runner = new FakeScanRunner(directory, Breakout("XUSDT", 100.5m, 101.6m));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Scanner:Path", directory);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IScanRunner>();
                services.AddSingleton<IScanRunner>(runner);
                // Never let a test open the real workspace database.
                var db = Path.Combine(directory, "workspace.db");
                services.RemoveAll<IWorkspaceStore>(); services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(db));
                services.RemoveAll<PaperRunStore>(); services.AddSingleton(_ => new PaperRunStore(db));
                services.RemoveAll<BotTemplateStore>(); services.AddSingleton(_ => new BotTemplateStore(db));
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        try
        {
            Assert.Equal("idle", (await client.GetFromJsonAsync<ScanSnapshot>("/api/v1/scanner"))!.State);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/scanner", new ScanStart(Seconds: 10))).StatusCode);
            var start = await client.PostAsJsonAsync("/api/v1/scanner", new ScanStart(Seconds: 600));
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
            var id = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            await runner.Fed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/scanner", new ScanStart(Seconds: 600))).StatusCode);
            var snapshot = (await client.GetFromJsonAsync<ScanSnapshot>("/api/v1/scanner"))!;
            Assert.Equal(("scanning", 0, false), (snapshot.State, snapshot.Ignored, snapshot.ExecutionEnabled));
            Assert.Equal("target", Assert.Single(snapshot.Candidates).Result);
            Assert.Equal("XUSDT", Assert.Single(snapshot.Symbols).Symbol);
            var detail = (await client.GetFromJsonAsync<ScanSymbolDetail>("/api/v1/scanner/symbols/XUSDT"))!;
            Assert.True(detail.Candles.Length > 60);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/scanner/symbols/ZZZUSDT")).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/scanner/{id}/stop", null)).StatusCode);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do snapshot = (await client.GetFromJsonAsync<ScanSnapshot>("/api/v1/scanner", wait.Token))!; while (snapshot.State == "scanning");
            Assert.Equal("stopped", snapshot.State);
            var report = JsonSerializer.Deserialize<ScanReport>(File.ReadAllText(Path.Combine(runner.Recording, "scan-candidates-v1.json")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal("target", Assert.Single(report.Candidates).Result);
        }
        finally
        {
            factory.Dispose();
            // Pooled SQLite connections keep the test database open until the pool is cleared.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class FakeScanRunner(string root, List<CaptureEvent> events) : IScanRunner
    {
        public TaskCompletionSource Fed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Recording { get; } = Path.Combine(root, "scan-1");

        public async Task<int> RunAsync(ScanOptions options, IRecordingObserver observer, CancellationToken ct)
        {
            Directory.CreateDirectory(Recording);
            File.WriteAllText(Path.Combine(Recording, "manifest.json"), JsonSerializer.Serialize(new
            {
                symbols = new[] { new ScanSymbol("XUSDT", 300_000_000, 99.5m) }
            }));
            observer.Started(Recording);
            for (var i = 0; i < events.Count; i++) observer.Recorded(events[i], i + 1, 0);
            Fed.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            observer.Finished(new(DateTimeOffset.UtcNow, "completed", null, 0, events.Count, 0, [], new { }));
            return 0;
        }
    }
}
