using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradeBot.Api;
using Xunit;

namespace TradeBot.Api.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "trade-bot-tests-" + Guid.NewGuid());
    private string Database => Path.Combine(_directory, "workspace.db");
    private static BotConfig Valid => new("Мой бот", "Binance", "ARBUSDT", 10m, 1m, "impulse", "0.1.0");

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWorkspaceStore>();
            services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(Database));
        }));

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        return client;
    }

    [Fact]
    public async Task Create_PersistsAcrossServerRestart_WithEmptyTradesAndRegisteredStrategy()
    {
        string id;
        using (var factory = Factory())
        using (var client = Client(factory))
        {
            var strategies = await client.GetFromJsonAsync<StrategyDefinition[]>("/api/v1/strategies");
            Assert.False(Assert.Single(strategies!).CanRun);
            var response = await client.PostAsJsonAsync("/api/v1/bots", Valid);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var bot = (await response.Content.ReadFromJsonAsync<Bot>())!;
            id = bot.Id;
            Assert.Equal($"/api/v1/bots/{id}", response.Headers.Location!.ToString());
            Assert.Equal("draft", bot.Status);
            Assert.Empty(bot.Trades);
        }
        using var restarted = Factory();
        using var reconnected = Client(restarted);
        var workspace = (await reconnected.GetFromJsonAsync<Workspace>("/api/v1/workspace"))!;
        Assert.Equal(id, Assert.Single(workspace.Bots).Id);
        Assert.Equal(id, Assert.Single(workspace.Events).BotId);
    }

    [Fact]
    public async Task Update_RejectsStaleRevision_AndKeepsJournalAttributionAfterRename()
    {
        using var factory = Factory();
        using var client = Client(factory);
        var created = await client.PostAsJsonAsync("/api/v1/bots", Valid);
        var bot = (await created.Content.ReadFromJsonAsync<Bot>())!;
        client.DefaultRequestHeaders.Add("If-Match", "\"1\"");
        var update = await client.PutAsJsonAsync($"/api/v1/bots/{bot.Id}", Valid with { Name = "Новое имя" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(2, (await update.Content.ReadFromJsonAsync<Bot>())!.Revision);
        var stale = await client.PutAsJsonAsync($"/api/v1/bots/{bot.Id}", Valid with { Name = "Устаревшее имя" });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.DeleteAsync($"/api/v1/bots/{bot.Id}")).StatusCode);
        var workspace = (await client.GetFromJsonAsync<Workspace>("/api/v1/workspace"))!;
        Assert.Equal("Новое имя", Assert.Single(workspace.Bots).Name);
        Assert.Equal(2, workspace.Events.Length);
        Assert.All(workspace.Events, entry => Assert.Equal(bot.Id, entry.BotId));
        client.DefaultRequestHeaders.Remove("If-Match");
        client.DefaultRequestHeaders.Add("If-Match", "\"2\"");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/bots/{bot.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/bots/{bot.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"exchange\":\"Binance\",\"symbol\":\"ARBUSDT\",\"budget\":10,\"lossLimit\":1,\"strategy\":\"impulse\",\"strategyVersion\":\"0.1.0\",\"apiKey\":\"secret\"}")]
    [InlineData("{broken")]
    [InlineData("{\"name\":null}")]
    public async Task Create_RejectsInvalidJsonAndUnrecognizedFields(string json)
    {
        using var factory = Factory();
        using var client = Client(factory);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/bots", content)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<Workspace>("/api/v1/workspace"))!.Bots);
    }

    [Theory]
    [InlineData("other", "0.1.0", "Binance", "ARBUSDT", 10, 1)]
    [InlineData("impulse", "9.0.0", "Binance", "ARBUSDT", 10, 1)]
    [InlineData("impulse", "0.1.0", "Unknown", "ARBUSDT", 10, 1)]
    [InlineData("impulse", "0.1.0", "Binance", "ARBUSDT\n", 10, 1)]
    [InlineData("impulse", "0.1.0", "Binance", "ARBUSDT", 0, 1)]
    [InlineData("impulse", "0.1.0", "Binance", "ARBUSDT", 10, 11)]
    public async Task Create_ValidatesStrategyMarketAndRiskOnServer(string strategy, string version, string exchange, string symbol, int budget, int loss)
    {
        using var factory = Factory();
        using var client = Client(factory);
        var config = Valid with { Strategy = strategy, StrategyVersion = version, Exchange = exchange, Symbol = symbol, Budget = budget, LossLimit = loss };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/bots", config)).StatusCode);
    }

    [Fact]
    public async Task Start_IsRejectedForPlannedAlgorithm_AndNoStatusOrTradesAreFabricated()
    {
        using var factory = Factory();
        using var client = Client(factory);
        var created = await client.PostAsJsonAsync("/api/v1/bots", Valid);
        var bot = (await created.Content.ReadFromJsonAsync<Bot>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/v1/bots/{bot.Id}/start", null)).StatusCode);
        var stored = (await client.GetFromJsonAsync<Workspace>("/api/v1/workspace"))!.Bots.Single();
        Assert.Equal("draft", stored.Status);
        Assert.Empty(stored.Trades);
    }

    [Fact]
    public async Task Mutations_RejectCrossSiteRequestsAndMissingPreconditions()
    {
        using var factory = Factory();
        using var client = Client(factory);
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/bots", Valid)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Remove("X-TradeBot-Client");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/bots", Valid)).StatusCode);
        client.DefaultRequestHeaders.Add("X-TradeBot-Client", "web");
        Assert.Equal((HttpStatusCode)428, (await client.PutAsJsonAsync($"/api/v1/bots/{Guid.NewGuid()}", Valid)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentEdits_OnlyOneRevisionWins()
    {
        using var store = new SqliteWorkspaceStore(Database);
        var created = await store.CreateAsync(Valid, CancellationToken.None);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() =>
            store.UpdateAsync(created.Bot!.Id, 1, Valid with { Name = $"Бот {i}" }, CancellationToken.None))));
        Assert.Single(results, result => result.Status == StoreStatus.Saved);
        Assert.Equal(5, results.Count(result => result.Status == StoreStatus.Conflict));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task CapacityLimit_DoesNotPersistAnExtraBotOrEvent()
    {
        using var store = new SqliteWorkspaceStore(Database);
        for (var i = 0; i < 100; i++)
            Assert.Equal(StoreStatus.Saved, (await store.CreateAsync(Valid, CancellationToken.None)).Status);
        Assert.Equal(StoreStatus.Limit, (await store.CreateAsync(Valid, CancellationToken.None)).Status);
        var workspace = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(100, workspace.Bots.Length);
        Assert.Equal(100, workspace.Events.Length);
    }

    [Fact]
    public void DamagedDatabase_IsNotReplacedByAnEmptyWorkspace()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Database, "not a database");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => new SqliteWorkspaceStore(Database));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal("not a database", File.ReadAllText(Database));
    }
}
