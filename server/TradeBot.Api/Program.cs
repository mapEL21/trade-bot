using Microsoft.AspNetCore.DataProtection;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using TradeBot.Api;
using TradeBot.Recorder;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetValue<int?>("Api:Port") ?? 5080;
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(port);
    options.Limits.MaxRequestBodySize = 32768;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<StrategyCatalog>();
builder.Services.AddSingleton<IScanRunner, ScanRunner>();
builder.Services.AddSingleton<MarketScanner>();
builder.Services.AddHostedService(services => services.GetRequiredService<MarketScanner>());
builder.Services.AddSingleton<StorageHousekeeper>();
builder.Services.AddSingleton<SignalLab>();
builder.Services.AddHostedService(services => services.GetRequiredService<SignalLab>());
builder.Services.AddHostedService(services => services.GetRequiredService<StorageHousekeeper>());
var storagePath = builder.Configuration["Storage:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "workspace.db");
builder.Services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(storagePath));
builder.Services.AddSingleton(_ => new PaperRunStore(storagePath));
builder.Services.AddSingleton<SignalBots>();
builder.Services.AddSingleton<IDepthSource, BinanceDepthSource>();
builder.Services.AddSingleton<ILiveFeed, BinanceLiveFeed>();
builder.Services.AddDataProtection().SetApplicationName("TradeBot");
builder.Services.AddSingleton<ExchangeCredentials>();
builder.Services.AddSingleton(_ => new ExchangeHttp(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
builder.Services.AddSingleton<LiveBots>();
builder.Services.AddHostedService(services => services.GetRequiredService<LiveBots>());
builder.Services.AddSingleton<DensityScreener>();
builder.Services.AddHostedService(services => services.GetRequiredService<DensityScreener>());
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("local", settings =>
    {
        settings.PermitLimit = 300;
        settings.Window = TimeSpan.FromMinutes(1);
        settings.QueueLimit = 0;
        settings.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(statusCode: 429, title: "Слишком много запросов. Повторите через минуту.")
            .ExecuteAsync(context.HttpContext);
    };
});

var app = builder.Build();
var remoteHost = builder.Configuration["Remote:Host"] is { Length: > 0 } host ? host.Trim() : null;
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception is BadHttpRequestException bad ? bad.StatusCode : exception is SqliteException ? 503 : 500;
    await Results.Problem(statusCode: status, title: status < 500 ? "Некорректный запрос." : "Сервер не смог выполнить запрос. Повторите позже.")
        .ExecuteAsync(context);
}));
app.UseStatusCodePages(async context =>
    await Results.Problem(statusCode: context.HttpContext.Response.StatusCode, title: "Запрос не выполнен.")
        .ExecuteAsync(context.HttpContext));
app.Use(async (context, next) =>
{
    // Local single-user API. Never expose it through a public proxy without authentication. On a server it is reached
    // only through a private network (Tailscale) under one configured host name, Remote:Host.
    if (context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]")
        && !(remoteHost is not null && string.Equals(context.Request.Host.Host, remoteHost, StringComparison.OrdinalIgnoreCase)))
    {
        await Results.Problem(statusCode: 403, title: "Разрешён только локальный доступ.").ExecuteAsync(context);
        return;
    }
    var origin = context.Request.Headers.Origin.ToString();
    string[] allowedOrigins = [builder.Configuration["Ui:Origin"] ?? "http://127.0.0.1:5173", "http://localhost:5173", $"http://localhost:{port}",
        $"http://127.0.0.1:{port}", .. remoteHost is null ? Array.Empty<string>() : [$"https://{remoteHost}"]];
    if ((origin.Length > 0 && !allowedOrigins.Contains(origin)) || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
    {
        await Results.Problem(statusCode: 403, title: "Источник запроса не разрешён.").ExecuteAsync(context);
        return;
    }
    if (context.Request.Method is not ("GET" or "HEAD") && context.Request.Headers["X-TradeBot-Client"] != "web")
    {
        await Results.Problem(statusCode: 403, title: "Отсутствует заголовок клиента.").ExecuteAsync(context);
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseRateLimiter();
app.MapOpenApi();
var api = app.MapGroup("/api/v1").RequireRateLimiting("local");
api.MapGet("/health", () => Results.Ok(new { status = "ok", executionEnabled = false }));
api.MapGet("/storage", (StorageHousekeeper storage) => Results.Ok(storage.Status));
// Data recorder for the signal lab: public Binance data of many coins; it never trades.
api.MapGet("/scanner", (MarketScanner scanner) => Results.Ok(scanner.Snapshot()));
api.MapPost("/scanner", (ScanStart request, MarketScanner scanner) =>
{
    try { return Results.Accepted("/api/v1/scanner", new { id = scanner.Start(request) }); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
    catch (InvalidOperationException e) { return Results.Problem(statusCode: 409, title: e.Message); }
});
api.MapPost("/scanner/{id:guid}/continuous", (Guid id, ScanContinuous request, MarketScanner scanner) =>
    scanner.SetContinuous(id.ToString(), request.Continuous) ? Results.Ok(scanner.Snapshot())
        : Results.Conflict(new { error = "Эта запись уже не идёт." }));
api.MapPost("/scanner/{id:guid}/stop", (Guid id, MarketScanner scanner) => scanner.Stop(id.ToString())
    ? Results.Accepted("/api/v1/scanner", new { id = id.ToString() })
    : Results.Problem(statusCode: 409, title: "Сканер уже сменился. Обновите состояние перед остановкой."));
// Exchange keys: entered by the user, kept encrypted on this computer, never sent back; checked by asking for the balance.
api.MapGet("/exchange/keys", (ExchangeCredentials credentials, IConfiguration configuration) => Results.Ok(new
{
    testnet = credentials.Status(ExchangeModes.Testnet),
    liveAllowed = ExchangeModes.Allowed(configuration, ExchangeModes.Live),
}));
api.MapPost("/exchange/keys", async (ExchangeKeysInput input, ExchangeCredentials credentials, ExchangeHttp http, IConfiguration configuration, CancellationToken ct) =>
{
    if (!ExchangeModes.Allowed(configuration, input.Mode)) return Results.Problem(statusCode: 400, title: "Ключи реального счёта пока не принимаются: сначала тестовая сеть.");
    if (string.IsNullOrWhiteSpace(input.ApiKey) || string.IsNullOrWhiteSpace(input.ApiSecret) || input.ApiKey.Length > 200 || input.ApiSecret.Length > 200)
        return Results.Problem(statusCode: 400, title: "Укажите API-ключ и секрет.");
    try
    {
        var client = new BinanceFuturesClient(http.Client, ExchangeModes.BaseUrl(configuration, input.Mode), input.ApiKey.Trim(), input.ApiSecret.Trim());
        await client.SyncTimeAsync(ct);
        var balance = await client.BalanceAsync(ct);
        credentials.Save(input.Mode, input.ApiKey, input.ApiSecret);
        return Results.Ok(new { status = credentials.Status(input.Mode), balance = balance.Wallet, available = balance.Available });
    }
    catch (ExchangeException e) { return Results.Problem(statusCode: 400, title: $"Ключи не подошли: {e.Message}"); }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return Results.Problem(statusCode: 503, title: "Биржа не отвечает. Ключи не сохранены."); }
});
// The account balance as the exchange reports it now; asked when the page shows it, never stored.
api.MapGet("/exchange/balance/{mode}", async (string mode, ExchangeCredentials credentials, ExchangeHttp http, IConfiguration configuration, CancellationToken ct) =>
{
    if (!ExchangeModes.Allowed(configuration, mode)) return Results.Problem(statusCode: 400, title: "Этот режим не разрешён.");
    if (credentials.Load(mode) is not { } keys) return Results.Problem(statusCode: 404, title: "Ключей нет.");
    try
    {
        var client = new BinanceFuturesClient(http.Client, ExchangeModes.BaseUrl(configuration, mode), keys.Key, keys.Secret);
        await client.SyncTimeAsync(ct);
        var b = await client.BalanceAsync(ct);
        return Results.Ok(new { wallet = b.Wallet, available = b.Available, unrealized = b.Unrealized, atMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }
    catch (ExchangeException e) { return Results.Problem(statusCode: 400, title: $"Биржа отказала: {e.Message}"); }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return Results.Problem(statusCode: 503, title: "Биржа не отвечает."); }
});
api.MapDelete("/exchange/keys/{mode}", (string mode, ExchangeCredentials credentials) =>
    credentials.Delete(mode) ? Results.NoContent() : Results.Problem(statusCode: 404, title: "Ключей нет."));
api.MapGet("/bots/{id:guid}/exchange", async (Guid id, PaperRunStore runs, CancellationToken ct) =>
{
    var run = (await runs.SignalRunsAsync(id.ToString(), ct)).FirstOrDefault();
    return Results.Ok(run is null ? [] : await runs.ExchangeLogAsync(run.Id, 100, ct));
});
// Density screener: walls in the order books of every Binance USDT perpetual and what happens to them; it never trades.
api.MapGet("/screener", (DensityScreener screener) => Results.Ok(screener.Snapshot()));
api.MapPost("/screener/start", (DensityScreener screener) => screener.Start()
    ? Results.Accepted("/api/v1/screener") : Results.Problem(statusCode: 409, title: "Скринер уже работает."));
api.MapPost("/screener/stop", (DensityScreener screener) => screener.Stop()
    ? Results.Accepted("/api/v1/screener") : Results.Problem(statusCode: 409, title: "Скринер не запущен."));
// Signal lab: events found in the scanner recordings and what price did after them; research only, no orders.
api.MapGet("/lab/status", (SignalLab lab) => Results.Ok(lab.Status));
api.MapPost("/lab/run", (SignalLab lab) => lab.Start()
    ? Results.Accepted("/api/v1/lab/status") : Results.Problem(statusCode: 409, title: "Записи уже обрабатываются."));
api.MapGet("/lab/report", (decimal? feePct, long? fromMs, string? session, SignalLab lab) =>
{
    try { return Results.Ok(lab.Report(feePct ?? 0.1m, fromMs, session)); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
});
api.MapGet("/lab/hypothesis", (SignalLab lab) => Results.Ok(lab.Hypothesis()));
api.MapGet("/lab/sessions", (string type, string mode, int horizon, decimal? feePct, long? fromMs, SignalLab lab) =>
{
    try { return Results.Ok(lab.Sessions(type, mode, horizon, feePct ?? 0.1m, fromMs)); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
});
api.MapGet("/lab/spreads", (long? fromMs, SignalLab lab) => Results.Ok(lab.Spreads(fromMs)));
api.MapGet("/lab/trades", (string type, string mode, int horizon, decimal? feePct, long? fromMs, SignalLab lab) =>
{
    try { return Results.Ok(lab.Trades(type, mode, horizon, feePct ?? 0.1m, fromMs)); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
});
api.MapGet("/strategies", (StrategyCatalog catalog) => Results.Ok(catalog.Items));
api.MapGet("/workspace", async (IWorkspaceStore store, PaperRunStore runs, SignalBots signals, StrategyCatalog catalog, CancellationToken ct) =>
{
    var workspace = await store.ReadAsync(ct);
    var signalRuns = (await runs.SignalRunsAsync(null, ct)).GroupBy(r => r.BotId).ToDictionary(g => g.Key, g => g.ToArray());
    return Results.Ok(workspace with
    {
        // Bots that are no longer valid (removed strategies, an older version of the signal bot) stay in the database but are not shown.
        Bots = workspace.Bots.Where(b => catalog.Validate(b).Count == 0).Select(b =>
        {
            var own = signalRuns.GetValueOrDefault(b.Id) ?? [];
            var results = own.Select(signals.Result).ToList();
            return b with
            {
                Status = own.Any(r => r.ToMs is null) ? "running" : own.Length > 0 ? "stopped" : "draft",
                Trades = results.SelectMany(r => r.Trades).OrderBy(t => t.At, StringComparer.Ordinal).TakeLast(10000).ToArray(),
                Unrealized = results.Where(r => r.Unrealized is not null).Select(r => r.Unrealized).FirstOrDefault(),
                Deposit = own.FirstOrDefault()?.Budget
            };
        }).ToArray()
    });
});
api.MapPost("/bots", async (BotConfig config, StrategyCatalog catalog, IWorkspaceStore store, CancellationToken ct) =>
{
    var errors = catalog.Validate(config);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    var result = await store.CreateAsync(config, ct);
    return result.Status == StoreStatus.Saved
        ? Results.Created($"/api/v1/bots/{result.Bot!.Id}", result.Bot)
        : Failure(result.Status);
});
api.MapGet("/bots/{id:guid}", async (Guid id, HttpContext context, IWorkspaceStore store, CancellationToken ct) =>
{
    var bot = (await store.ReadAsync(ct)).Bots.FirstOrDefault(x => x.Id == id.ToString());
    if (bot is null) return Failure(StoreStatus.Missing);
    context.Response.Headers.ETag = $"\"{bot.Revision}\"";
    return Results.Ok(bot);
});
api.MapPut("/bots/{id:guid}", async (Guid id, BotConfig config, HttpContext context, StrategyCatalog catalog, IWorkspaceStore store, SignalBots signals, CancellationToken ct) =>
{
    if (await signals.IsRunningAsync(id.ToString(), ct)) return Results.Problem(statusCode: 409, title: "Бот работает. Остановите его перед изменением настроек.");
    if (!TryRevision(context, out var revision)) return Results.Problem(statusCode: 428, title: "Нужна версия бота в If-Match.");
    var errors = catalog.Validate(config);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    var result = await store.UpdateAsync(id.ToString(), revision, config, ct);
    return result.Status == StoreStatus.Saved ? Results.Ok(result.Bot) : Failure(result.Status);
});
api.MapDelete("/bots/{id:guid}", async (Guid id, HttpContext context, IWorkspaceStore store, SignalBots signals, CancellationToken ct) =>
{
    if (await signals.IsRunningAsync(id.ToString(), ct)) return Results.Problem(statusCode: 409, title: "Бот работает. Остановите его перед удалением.");
    if (!TryRevision(context, out var revision)) return Results.Problem(statusCode: 428, title: "Нужна версия бота в If-Match.");
    var result = await store.DeleteAsync(id.ToString(), revision, ct);
    return result.Status == StoreStatus.Saved ? Results.NoContent() : Failure(result.Status);
});
// Signal bots: a run follows the processed recordings from its start moment; simulation only.
api.MapGet("/bots/{id:guid}/signal", async (Guid id, SignalBots signals, CancellationToken ct) => Results.Ok(await signals.RunsAsync(id.ToString(), ct)));
api.MapPost("/bots/{id:guid}/signal/start", async (Guid id, SignalStart? request, IWorkspaceStore store, StrategyCatalog catalog, SignalBots signals, CancellationToken ct) =>
{
    var bot = (await store.ReadAsync(ct)).Bots.FirstOrDefault(x => x.Id == id.ToString());
    if (bot is null) return Failure(StoreStatus.Missing);
    if (bot.Strategy != StrategyCatalog.SignalId) return Results.Problem(statusCode: 409, title: "Это не бот сигнала.");
    var errors = catalog.Validate(bot);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    try { return Results.Accepted($"/api/v1/bots/{id}/signal", await signals.StartAsync(bot, request ?? new(), ct)); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
    catch (InvalidOperationException e) { return Results.Problem(statusCode: 409, title: e.Message); }
});
api.MapPost("/bots/{id:guid}/signal/stop", async (Guid id, SignalBots signals, CancellationToken ct) =>
    await signals.StopAsync(id.ToString(), ct) ? Results.Accepted($"/api/v1/bots/{id}/signal") : Results.Problem(statusCode: 409, title: "Бот сейчас не работает."));
// Validate the database on startup; never silently replace a damaged workspace.
_ = app.Services.GetRequiredService<IWorkspaceStore>();
_ = app.Services.GetRequiredService<PaperRunStore>();
// On a server the built interface is served by the API itself: one process, one port. The app has no client-side routes,
// so only "/" maps to index.html and unknown API paths stay 404.
if (builder.Configuration["Ui:Path"] is { Length: > 0 } ui && Directory.Exists(ui))
{
    var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(ui));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}
app.Run();

static bool TryRevision(HttpContext context, out long revision) =>
    long.TryParse(context.Request.Headers.IfMatch.ToString().Trim('"'), out revision) && revision > 0;

static IResult Failure(StoreStatus status) => status switch
{
    StoreStatus.Missing => Results.Problem(statusCode: 404, title: "Бот не найден."),
    StoreStatus.Conflict => Results.Problem(statusCode: 412, title: "Настройки изменились в другой вкладке. Обновите данные перед сохранением."),
    StoreStatus.Limit => Results.Problem(statusCode: 409, title: "Можно сохранить до 100 экземпляров ботов."),
    _ => Results.Problem(statusCode: 500, title: "Не удалось сохранить изменения.")
};


public partial class Program;
