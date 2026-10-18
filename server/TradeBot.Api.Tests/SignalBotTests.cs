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

// The bot on a synthetic market: one coin, a book of 20 levels 0.05 apart, a trade every second, an hour of 1% minute
// candles before it (the coin's typical candle). Rules under test: docs/BOT_LOGIC.md.
public sealed class SignalBotTests : IDisposable
{
    private const long T0 = 1_760_000_000_000;
    private readonly string root = Path.Combine(Path.GetTempPath(), "signal-bot-tests-" + Guid.NewGuid());
    private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    private static readonly SignalParameters Density = new(SignalParameters.Density, FeePct: .02m);

    private sealed class Market
    {
        public readonly List<CaptureEvent> Events = [];
        // high: the minute candles' high over a low of 100, so the coin's typical minute candle is high − 100 percent.
        public Market(decimal high = 101)
        {
            var klines = string.Join(',', Enumerable.Range(0, 62).Select(i => $"[{T0 - (62 - i) * 60_000},\"100\",\"{S(high)}\",\"100\",\"100\",\"1\",0,\"100\",1,\"1\",\"100\",\"0\"]"));
            Events.Add(new(ScanRecorder.Rest, "history", "history", DateTimeOffset.UnixEpoch, 0,
                $"{{\"symbol\":\"XUSDT\",\"interval\":\"1m\",\"klines\":[{klines}]}}"));
        }
        // Best bid B, bids B - 0.05k, asks B + 0.05 + 0.05k; walls replace a level's size (1 coin elsewhere, about 100 USDT).
        public void Second(long second, decimal bid, Dictionary<decimal, decimal>? walls = null, decimal? trade = null)
        {
            var ms = T0 + second * 1000;
            string Side(int sign) => string.Join(',', Enumerable.Range(0, 20).Select(k =>
            {
                var price = sign < 0 ? bid - .05m * k : bid + .05m + .05m * k;
                return $"[\"{S(price)}\",\"{S(walls?.GetValueOrDefault(price) is > 0 and var q ? q : 1)}\"]";
            }));
            Events.Add(Trade(ms, trade ?? bid, 1, true));
            Events.Add(new(ScanRecorder.Public, "c", "market", DateTimeOffset.UnixEpoch, ms + 1,
                $"{{\"stream\":\"xusdt@depth20@500ms\",\"data\":{{\"e\":\"depthUpdate\",\"E\":{ms + 1},\"T\":{ms + 1},\"s\":\"XUSDT\",\"b\":[{Side(-1)}],\"a\":[{Side(1)}]}}}}"));
        }
        public static CaptureEvent Trade(long ms, decimal price, decimal quantity, bool buy) => new(ScanRecorder.Market, "c", "market", DateTimeOffset.UnixEpoch, ms,
            $"{{\"stream\":\"xusdt@aggTrade\",\"data\":{{\"e\":\"aggTrade\",\"s\":\"XUSDT\",\"p\":\"{S(price)}\",\"q\":\"{S(quantity)}\",\"T\":{ms},\"m\":{(!buy).ToString().ToLowerInvariant()}}}}}");
        public EngineResult Run(SignalParameters p)
        {
            var engine = new BotEngine(p, new Dictionary<string, decimal> { ["XUSDT"] = 10_000_000 }, liquidations: true);
            foreach (var e in Events) engine.Process(e);
            return engine.Finish("test");
        }
    }

    // A bid wall at 99.95 from the start; the price touches it, goes up 0.575% and comes back: one known bounce.
    private static Market BounceSetup(out Dictionary<decimal, decimal> wall, decimal high = 101)
    {
        var w = new Dictionary<decimal, decimal> { [99.95m] = 100 };
        wall = w;
        var m = new Market(high);
        for (var t = 0; t < 100; t++) m.Second(t, 100, w);
        for (var t = 100; t < 120; t++) m.Second(t, 100.5m, w);
        return m;
    }

    [Fact]
    public void BounceOffAWallEntersOnTheReturnAndExitsAtTheAverageBounce()
    {
        var m = BounceSetup(out var wall);
        for (var t = 120; t < 140; t++) m.Second(t, 100, wall);
        for (var t = 140; t < 150; t++) m.Second(t, 100.55m, wall);
        var result = m.Run(Density);
        var trade = Assert.Single(result.Trades);
        Assert.Equal("long", trade.Side);
        Assert.Equal((100.05m, 100.55m), (trade.Entry, trade.Exit));
        Assert.StartsWith("отскок от плотности 10 тыс. $ на 99,95", trade.EntryReason);
        Assert.Contains("цель 0,57%", trade.EntryReason);
        Assert.Equal("цена отошла от плотности на средний отскок 0,57%", trade.ExitReason);
        // The first touch had no bounce statistics yet: no trade then.
        Assert.Equal(1, result.Skipped["нет статистики отскоков"]);
        // Size from the risk: 1.5% ÷ (0.075% from the mid to the wall + 0.05% slippage + 0.02% fees) ≈ 10.3× the deposit.
        Assert.Equal(10.3462m, trade.Fraction);
    }

    [Fact]
    public void AWallEatenFastByTheTapeIsLeftBeforeItBreaksAndTheTradeTurnsIntoTheBreak()
    {
        var m = BounceSetup(out var wall);
        for (var t = 120; t < 125; t++) m.Second(t, 100, wall);
        // Large sells hit the wall between snapshots: 60 of its 100 coins are gone.
        m.Events.Add(Market.Trade(T0 + 125_300, 99.95m, 60, false));
        // The next snapshot: the wall is gone and the price is through it, then it falls and stalls.
        for (var t = 126; t < 130; t++) m.Second(t, 99.85m);
        for (var t = 130; t < 175; t++) m.Second(t, 99.5m);
        var result = m.Run(Density);
        Assert.Equal(2, result.Trades.Length);
        var bounce = result.Trades[0];
        Assert.Equal(("long", "плотность проедают: осталось 40% — выход до пробоя"), (bounce.Side, bounce.ExitReason));
        // The exit order went out at the first sign and was filled 250 ms later, already through the wall: a loss.
        Assert.Equal(99.85m, bounce.Exit);
        var flip = result.Trades[1];
        Assert.Equal("short", flip.Side);
        Assert.StartsWith("переворот в проедание плотности", flip.EntryReason);
        Assert.Equal("импульс после проедания выдохся: 30 с без нового экстремума", flip.ExitReason);
        Assert.True(flip.Exit < flip.Entry);
    }

    [Fact]
    public void APulledWallEndsTheBounce()
    {
        var m = BounceSetup(out var wall);
        for (var t = 120; t < 125; t++) m.Second(t, 100, wall);
        for (var t = 125; t < 130; t++) m.Second(t, 100);
        var trade = Assert.Single(m.Run(Density).Trades);
        Assert.Equal("плотность сняли", trade.ExitReason);
    }

    [Fact]
    public void BouncesSmallerThanThreeFeesAreNotTraded()
    {
        var m = BounceSetup(out var wall);
        for (var t = 120; t < 140; t++) m.Second(t, 100, wall);
        var result = m.Run(Density with { FeePct = .2m });
        Assert.Empty(result.Trades);
        Assert.Equal(1, result.Skipped["средний отскок меньше трёх комиссий"]);
    }

    [Fact]
    public void ATradeWhoseFeesTakeOverAThirdOfItsRiskIsNotOpened()
    {
        // The bounce risks 0.125% (to the wall plus slippage); 0.06% fees would be 32% of the risk with them.
        var m = BounceSetup(out var wall);
        for (var t = 120; t < 140; t++) m.Second(t, 100, wall);
        var result = m.Run(Density with { FeePct = .06m });
        Assert.Empty(result.Trades);
        Assert.Equal(1, result.Skipped["комиссия больше 30% риска"]);
    }

    [Fact]
    public void ABreakIsNotFollowedWhenTheCoinsTypicalCandleIsUnderThreeFees()
    {
        // Typical minute candle 0.1%, under three 0.05% fees.
        var m = BounceSetup(out var wall, high: 100.1m);
        for (var t = 120; t < 125; t++) m.Second(t, 100, wall);
        m.Events.Add(Market.Trade(T0 + 125_300, 99.95m, 60, false));
        for (var t = 126; t < 130; t++) m.Second(t, 99.85m);
        for (var t = 130; t < 175; t++) m.Second(t, 99.5m);
        var result = m.Run(Density with { FeePct = .05m });
        // The bounce is left before the break, but the break itself is not traded.
        Assert.Equal("плотность проедают: осталось 40% — выход до пробоя", Assert.Single(result.Trades).ExitReason);
        // Twice: the flip from the bounce, then the same eaten wall seen again once the coin is free.
        Assert.Equal(2, result.Skipped["типичная свеча меньше трёх комиссий"]);
    }

    [Fact]
    public void APriceSpikeIsFollowedUntilTheMoveStallsOrIsAbsorbed()
    {
        static Market Spike(decimal after)
        {
            var m = new Market();
            for (var t = 0; t < 400; t++) m.Second(t, 100);
            // 4% in ten seconds: four typical 1% candles.
            for (var t = 400; t < 410; t++) m.Second(t, 100 + .4m * (t - 399));
            for (var t = 410; t < 480; t++) m.Second(t, after);
            return m;
        }
        var stalled = Assert.Single(Spike(104).Run(new SignalParameters(SignalParameters.Spike)).Trades);
        Assert.Equal("long", stalled.Side);
        Assert.StartsWith("вынос цены: Ход за 30 с", stalled.EntryReason);
        Assert.Equal("движение выдохлось: 60 с без нового экстремума", stalled.ExitReason);
        // Back to the middle of the 100 → 103 spike: the impulse was absorbed.
        var absorbed = Assert.Single(Spike(101).Run(new SignalParameters(SignalParameters.Spike)).Trades);
        Assert.Equal("импульс поглощён: цена вернулась к середине выноса", absorbed.ExitReason);
    }

    [Fact]
    public void TheSixtySecondVariantLeavesAMinuteAfterTheEntry()
    {
        var m = new Market();
        for (var t = 0; t < 400; t++) m.Second(t, 100);
        for (var t = 400; t < 410; t++) m.Second(t, 100 + .4m * (t - 399));
        // The price keeps going up: the stall rule would hold on, the variant leaves after 60 s anyway.
        for (var t = 410; t < 520; t++) m.Second(t, 104 + .01m * (t - 410));
        var trade = Assert.Single(m.Run(new SignalParameters(SignalParameters.Spike60)).Trades);
        Assert.Equal("60 с после входа — выход по гипотезе № 2", trade.ExitReason);
        Assert.InRange((trade.ClosedMs - trade.OpenedMs) / 1000, 60, 61);
    }

    [Fact]
    public void MoneyPassSizesFromTheDepositAtEntryAndStopsAtTheLossLimit()
    {
        var run = new SignalRun("r", "b", T0, null, DateTimeOffset.UnixEpoch, 100, 1.5m, new SignalParameters(FeePct: 0));
        EngineTrade Loss(long at) => new("XUSDT", "long", at, at + 1000, 100, 99, 1, "вход", "выход", 0, -100);
        var result = SignalBots.Money(run, [new EngineResult(BotEngine.Version, "x", T0, T0 + 10_000, [Loss(T0), Loss(T0 + 2000), Loss(T0 + 4000)], [], 3, 0)]);
        // 1% loss on a notional equal to the deposit: 100 → 99 → 98.01, then the 1.5 USDT limit stops new trades.
        Assert.Equal(2, result.Trades.Length);
        Assert.Equal(99m, result.Trades[1].Notional);
        Assert.Equal(98.01m, result.Deposit);
        Assert.Equal(1, result.Skipped["лимит убытка"]);
        Assert.Equal(("вход", "выход"), (result.Trades[0].EntryReason, result.Trades[0].ExitReason));
        // Trades before the run's start are not the run's.
        Assert.Empty(SignalBots.Money(run with { FromMs = T0 + 5000 }, [new EngineResult(BotEngine.Version, "x", T0, T0 + 10_000, [Loss(T0)], [], 1, 0)]).Trades);
    }

    [Fact]
    public void TheDailyLossLimitWaitsForTheNextMoscowDay()
    {
        // 10% of the 100 USDT start a day; the run's own limit (50) is far away.
        var run = new SignalRun("r", "b", T0, null, DateTimeOffset.UnixEpoch, 100, 50, new SignalParameters(FeePct: 0));
        EngineTrade Loss(long at) => new("XUSDT", "long", at, at + 1000, 100, 96, 1, "вход", "выход", 0, -400);
        const long Day = 86_400_000;
        var result = SignalBots.Money(run, [new EngineResult(BotEngine.Version, "x", T0, T0 + Day + 10_000,
            [Loss(T0), Loss(T0 + 2000), Loss(T0 + 4000), Loss(T0 + 6000), Loss(T0 + Day)], [], 5, 0)]);
        // −4, −3.84, −3.69: 11.5 USDT lost, the fourth trade of the day waits; the next day trades again.
        Assert.Equal(4, result.Trades.Length);
        Assert.Equal(1, result.Skipped["дневной лимит убытка"]);
        Assert.Equal(T0 + Day, DateTimeOffset.Parse(result.Trades[3].OpenedAt!).ToUnixTimeMilliseconds());
        // The Moscow day starts at 21:00 UTC.
        var midnight = T0 - T0 % Day + 21 * 3_600_000;
        Assert.NotEqual(DailyLoss.Day(midnight - 1), DailyLoss.Day(midnight));
        Assert.Equal(DailyLoss.Day(midnight), DailyLoss.Day(midnight + Day - 1));
    }

    [Fact]
    public async Task ABotStartedFromAPastMomentComputesItsRecordingsInTheBackgroundAndShowsTheTrades()
    {
        var scans = Path.Combine(root, "scans");
        var dir = Path.Combine(scans, $"{DateTimeOffset.FromUnixTimeMilliseconds(T0):yyyyMMdd'T'HHmmss'Z'}-77777777777777777777777777777777");
        Directory.CreateDirectory(dir);
        var m = BounceSetup(out var wall);
        for (var t = 120; t < 140; t++) m.Second(t, 100, wall);
        for (var t = 140; t < 150; t++) m.Second(t, 100.55m, wall);
        File.WriteAllLines(Path.Combine(dir, StorageHousekeeper.Events), m.Events.Select(e => JsonSerializer.Serialize(e)));
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new { liquidations = true, symbols = new[] { new ScanSymbol("XUSDT", 10_000_000, 100) } }));
        File.WriteAllText(Path.Combine(dir, "summary.json"), "{}");
        var db = Path.Combine(root, "workspace.db");
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
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            var config = new BotConfig("Плотность", "Binance", "*", 100, 30, StrategyCatalog.SignalId, StrategyCatalog.SignalVersion, Density);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", config with { Signal = Density with { Signal = "Перекос стакана" } })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", config with { StrategyVersion = "0.1.0" })).StatusCode);
            var bot = (await (await client.PostAsJsonAsync("/api/v1/bots", config)).Content.ReadFromJsonAsync<Bot>(json))!;
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/v1/bots/{bot.Id}/signal/start", new SignalStart(T0 - 1000))).StatusCode);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            SignalRunView run;
            do
            {
                run = (await client.GetFromJsonAsync<SignalRunView[]>($"/api/v1/bots/{bot.Id}/signal", json, wait.Token))!.Single();
                if (run.Result.Pending > 0) await Task.Delay(100, wait.Token);
            } while (run.Result.Pending > 0);
            Assert.Equal(1, run.Result.Recordings);
            Assert.True(File.Exists(Path.Combine(dir, SignalBots.FileName(Density))));
            var listed = (await client.GetFromJsonAsync<Workspace>("/api/v1/workspace", json))!.Bots.Single();
            Assert.Equal("running", listed.Status);
            var trade = Assert.Single(listed.Trades);
            Assert.Equal("цена отошла от плотности на средний отскок 0,57%", trade.ExitReason);
            Assert.StartsWith("отскок от плотности", trade.EntryReason);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/bots/{bot.Id}/signal/stop", null)).StatusCode);
        }
        finally
        {
            factory.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
