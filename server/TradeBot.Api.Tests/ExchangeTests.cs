using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradeBot.Api;
using Xunit;

namespace TradeBot.Api.Tests;

// The exchange adapter against a fake Binance futures API: what it sends, in which order, and what it does when the
// exchange refuses, loses an answer or closes a position by itself.
public sealed class ExchangeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "exchange-tests-" + Guid.NewGuid());

    private static readonly SignalRun Run = new("12345678-aaaa-bbbb-cccc-1234567890ab", "bot", 0, null, DateTimeOffset.UnixEpoch, 100, 30,
        new SignalParameters(MaxLeverage: 20), Live: true, Execution: ExchangeModes.Testnet);

    private (ExchangeExecutor Executor, FakeBinance Exchange, PaperRunStore Store) Executor(SignalRun? run = null)
    {
        run ??= Run;
        var exchange = new FakeBinance();
        var client = new BinanceFuturesClient(new HttpClient(exchange), "https://testnet.example", "key", "secret");
        var store = new PaperRunStore(Path.Combine(root, "workspace.db"));
        return (new ExchangeExecutor(client, run, store, NullLogger.Instance), exchange, store);
    }

    // A long worth 2.5× the 100 USDT deposit, cancelled at 99.5.
    private static EngineIntent Entry() => new("entry", "XUSDT", 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 2.5m, 100, 99.5m, "отскок от плотности");

    [Fact]
    public async Task AnEntryGoesToTheExchangeWithAProtectiveStopAndAnExitClosesIt()
    {
        var (executor, exchange, store) = Executor();
        executor.Enqueue(Entry());
        await executor.DrainAsync();
        // 250 USDT at 100: 2.5 coins, isolated margin at 3× (the leverage the size needs), a market buy, then the stop.
        var orders = exchange.Requests.Where(r => r.Path == "/fapi/v1/order" && r.Method == "POST").ToList();
        Assert.Equal(("MARKET", "BUY", "2.500"), (orders[0].Query["type"], orders[0].Query["side"], orders[0].Query["quantity"]));
        Assert.Equal("ISOLATED", exchange.Requests.Single(r => r.Path == "/fapi/v1/marginType").Query["marginType"]);
        Assert.Equal("3", exchange.Requests.Single(r => r.Path == "/fapi/v1/leverage").Query["leverage"]);
        // The stop is 0.3% beyond the 99.5 cancel level, down to the price step: 99.2015 → 99.20.
        Assert.Equal(("STOP_MARKET", "SELL", "99.20", "true"), (orders[1].Query["type"], orders[1].Query["side"], orders[1].Query["stopPrice"], orders[1].Query["closePosition"]));
        var holding = Assert.Single(executor.Holdings());
        Assert.Equal(("long", 2.5m, 99.2m), (holding.Side, holding.Quantity, holding.StopPrice));

        exchange.Price = 101;
        executor.Enqueue(new("exit", "XUSDT", -1, 0, 2.5m, 101, null, "цена отошла от плотности на средний отскок"));
        await executor.DrainAsync();
        Assert.Empty(executor.Holdings());
        Assert.Contains(exchange.Requests, r => r.Method == "DELETE" && r.Query["origClientOrderId"] == orders[1].Query["newClientOrderId"]);
        var exit = exchange.Requests.Last(r => r.Path == "/fapi/v1/order" && r.Method == "POST");
        Assert.Equal(("SELL", "true", "2.500"), (exit.Query["side"], exit.Query["reduceOnly"], exit.Query["quantity"]));
        Assert.Equal(0m, exchange.Position);
        // 2.5 × (101 − 100) − 0.1% of both sides ≈ +2.25 USDT; the deposit for the next trade grows with it.
        Assert.Equal(102.24875m, executor.Deposit);
        var log = await store.ExchangeLogAsync(Run.Id, 10, CancellationToken.None);
        Assert.Equal(["вход", "защитный стоп", "выход"], log.Reverse().Select(e => e.Action));
        Assert.StartsWith("цена отошла от плотности на средний отскок. Результат 2,2488 USDT", log[0].Message);
    }

    [Fact]
    public async Task APositionIsNeverLeftWithoutAStop()
    {
        var (executor, exchange, store) = Executor();
        exchange.RefuseStop = true;
        executor.Enqueue(Entry());
        await executor.DrainAsync();
        Assert.Empty(executor.Holdings());
        Assert.Equal(0m, exchange.Position);
        var log = (await store.ExchangeLogAsync(Run.Id, 10, CancellationToken.None)).Reverse().ToList();
        Assert.Equal(["вход", "ошибка", "выход"], log.Select(e => e.Action));
        Assert.Contains("Защитный стоп не поставлен", log[1].Message);
    }

    [Fact]
    public async Task ALostAnswerIsLookedUpByTheOrderIdInsteadOfSendingTheOrderAgain()
    {
        var (executor, exchange, _) = Executor();
        exchange.LoseNextMarketAnswer = true;
        executor.Enqueue(Entry());
        await executor.DrainAsync();
        // One market order only, found by its client id, and the stop after it.
        Assert.Single(exchange.Requests, r => r.Path == "/fapi/v1/order" && r.Method == "POST" && r.Query["type"] == "MARKET");
        Assert.Contains(exchange.Requests, r => r.Path == "/fapi/v1/order" && r.Method == "GET");
        Assert.Equal(2.5m, exchange.Position);
        Assert.Single(executor.Holdings());
    }

    [Fact]
    public async Task ReconcileNoticesAPositionTheExchangeClosedAndOneTheBotDoesNotKnow()
    {
        var (executor, exchange, store) = Executor();
        executor.Enqueue(Entry());
        await executor.DrainAsync();
        // The protective stop fired: the exchange has no position any more.
        exchange.Position = 0;
        executor.Reconcile();
        await executor.DrainAsync();
        Assert.Empty(executor.Holdings());
        var log = await store.ExchangeLogAsync(Run.Id, 10, CancellationToken.None);
        Assert.Equal("закрыта биржей", log[0].Action);
        // A position opened elsewhere is reported once and left alone.
        exchange.Position = -3;
        executor.Reconcile(); executor.Reconcile();
        await executor.DrainAsync();
        Assert.Single(await store.ExchangeLogAsync(Run.Id, 20, CancellationToken.None), e => e.Action == "внимание");
        Assert.Equal(-3m, exchange.Position);
    }

    [Fact]
    public async Task ALostConnectionIsWrittenOnceAndItsReturnOnce()
    {
        var (executor, exchange, store) = Executor();
        exchange.Offline = true;
        executor.Reconcile(); executor.Reconcile(); executor.Reconcile();
        await executor.DrainAsync();
        exchange.Offline = false;
        executor.Reconcile(); executor.Reconcile();
        await executor.DrainAsync();
        var log = (await store.ExchangeLogAsync(Run.Id, 10, CancellationToken.None)).Reverse().ToList();
        Assert.Equal(["нет связи", "связь"], log.Select(e => e.Action));
        Assert.StartsWith("Биржа не ответила за 10 с.", log[0].Message);
    }

    [Fact]
    public async Task OrdersBelowTheExchangeMinimumAreNotSent()
    {
        var (executor, exchange, store) = Executor();
        executor.Enqueue(Entry() with { Fraction = .04m });
        await executor.DrainAsync();
        Assert.DoesNotContain(exchange.Requests, r => r.Path == "/fapi/v1/order");
        Assert.Contains("меньше минимальной заявки биржи", (await store.ExchangeLogAsync(Run.Id, 1, CancellationToken.None))[0].Message);
    }

    [Fact]
    public async Task AnEntryOnLateDataIsNotSent()
    {
        var (executor, exchange, store) = Executor();
        executor.Enqueue(Entry() with { Ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10_000 });
        await executor.DrainAsync();
        Assert.DoesNotContain(exchange.Requests, r => r.Path == "/fapi/v1/order");
        Assert.StartsWith("Данные опаздывают на", (await store.ExchangeLogAsync(Run.Id, 1, CancellationToken.None))[0].Message);
    }

    [Fact]
    public async Task TheDailyLossLimitHoldsOnTheExchange()
    {
        var (executor, exchange, store) = Executor(Run with { Parameters = new SignalParameters(MaxLeverage: 20, DailyLossPct: 1) });
        executor.Enqueue(Entry());
        await executor.DrainAsync();
        // A 7.5 USDT loss on 2.5 coins is over 1% of the 100 USDT start: the next entry today is not sent.
        exchange.Price = 97;
        executor.Enqueue(new("exit", "XUSDT", -1, 0, 0, 97, null, "выход"));
        executor.Enqueue(Entry());
        await executor.DrainAsync();
        Assert.Single(exchange.Requests, r => r.Path == "/fapi/v1/order" && r.Method == "POST" && r.Query["type"] == "MARKET" && r.Query.GetValueOrDefault("reduceOnly") != "true");
        Assert.StartsWith("Достигнут дневной лимит убытка", (await store.ExchangeLogAsync(Run.Id, 1, CancellationToken.None))[0].Message);
    }

    [Fact]
    public async Task AStopClosesWhatIsLeftOnTheBotsCoinsAndCancelsTheirOrders()
    {
        var (executor, exchange, store) = Executor();
        executor.Enqueue(Entry());
        await executor.DrainAsync();
        // A fill the bot did not see: the exchange holds more than the bot knows.
        exchange.Position += 1;
        await executor.FlattenAndStopAsync();
        Assert.Equal(0m, exchange.Position);
        Assert.Contains(exchange.Requests, r => r.Method == "DELETE" && r.Path == "/fapi/v1/allOpenOrders" && r.Query["symbol"] == "XUSDT");
        Assert.Contains("остаток позиции", (await store.ExchangeLogAsync(Run.Id, 1, CancellationToken.None))[0].Message);
    }

    [Fact]
    public async Task TheBalanceAndTheFeeRateAreReadFromTheAccount()
    {
        var client = new BinanceFuturesClient(new HttpClient(new FakeBinance()), "https://testnet.example", "key", "secret");
        Assert.Equal(new ExchangeBalance(5010.5m, 5000.5m, 1.25m), await client.BalanceAsync(CancellationToken.None));
        Assert.Equal(.0005m, await client.TakerFeeAsync(SignalBots.FeeSymbol, CancellationToken.None));
        // The test network is Binance's Demo Trading now.
        Assert.Equal("https://demo-fapi.binance.com", ExchangeModes.BaseUrl(new ConfigurationBuilder().Build(), ExchangeModes.Testnet));
    }

    [Fact]
    public void KeysAreKeptEncryptedAndNeverStoredInTheClear()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Exchange:KeysPath"] = Path.Combine(root, "keys.json") }).Build();
        var credentials = new ExchangeCredentials(new EphemeralDataProtectionProvider(), config,
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root });
        Assert.False(credentials.Status(ExchangeModes.Testnet).Configured);
        credentials.Save(ExchangeModes.Testnet, "ABCDEFGH1234", "very-secret-value");
        Assert.Equal(("ABCDEFGH1234", "very-secret-value"), credentials.Load(ExchangeModes.Testnet));
        Assert.DoesNotContain("very-secret-value", File.ReadAllText(Path.Combine(root, "keys.json")));
        Assert.Equal((true, "1234"), (credentials.Status(ExchangeModes.Testnet).Configured, credentials.Status(ExchangeModes.Testnet).KeyEnd));
        // A real account stays closed until it is allowed on purpose.
        Assert.False(ExchangeModes.Allowed(config, ExchangeModes.Live));
        Assert.True(credentials.Delete(ExchangeModes.Testnet));
        Assert.Null(credentials.Load(ExchangeModes.Testnet));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
