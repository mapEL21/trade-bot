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
builder.Services.AddSingleton<IRecordingRunner, RecordingRunner>();
builder.Services.AddSingleton<MarketResearch>();
builder.Services.AddSingleton<PaperTrading>();
builder.Services.AddSingleton(_ => new ResearchMarkets(new HttpClient { Timeout = TimeSpan.FromSeconds(8) }));
builder.Services.AddHostedService(services => services.GetRequiredService<MarketResearch>());
builder.Services.AddSingleton<IScanRunner, ScanRunner>();
builder.Services.AddSingleton<MarketScanner>();
builder.Services.AddHostedService(services => services.GetRequiredService<MarketScanner>());
builder.Services.AddSingleton<StorageHousekeeper>();
builder.Services.AddSingleton<SignalLab>();
builder.Services.AddHostedService(services => services.GetRequiredService<StorageHousekeeper>());
var storagePath = builder.Configuration["Storage:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "workspace.db");
builder.Services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(storagePath));
builder.Services.AddSingleton(_ => new PaperRunStore(storagePath));
builder.Services.AddSingleton(_ => new BotTemplateStore(storagePath));
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
api.MapGet("/research/markets", async (string exchange, string? sourceExchange, ResearchMarkets markets, HttpContext context) =>
{
    try { return Results.Ok(await markets.GetAsync(exchange, context.RequestAborted, sourceExchange ?? "hyperliquid")); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
    catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
        || e is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem(statusCode: 503, title: "Не удалось загрузить инструменты с бирж. Повторите загрузку.");
    }
});
api.MapGet("/research/instrument", async (string exchange, string coin, ResearchMarkets markets, HttpContext context) =>
{
    try
    {
        return await markets.InstrumentAsync(exchange, coin, context.RequestAborted) is { } info
            ? Results.Ok(info) : Results.Problem(statusCode: 404, title: "Инструмент не найден среди бессрочных USDT-контрактов биржи.");
    }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
    catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
        || e is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem(statusCode: 503, title: "Не удалось загрузить правила инструмента с биржи. Повторите позже.");
    }
});
// Level scanner: public Binance data only; it finds levels and scores breakouts, it never trades.
api.MapGet("/scanner", (MarketScanner scanner) => Results.Ok(scanner.Snapshot()));
api.MapPost("/scanner", (ScanStart request, MarketScanner scanner) =>
{
    try { return Results.Accepted("/api/v1/scanner", new { id = scanner.Start(request) }); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
    catch (InvalidOperationException e) { return Results.Problem(statusCode: 409, title: e.Message); }
});
api.MapPost("/scanner/{id:guid}/stop", (Guid id, MarketScanner scanner) => scanner.Stop(id.ToString())
    ? Results.Accepted("/api/v1/scanner", new { id = id.ToString() })
    : Results.Problem(statusCode: 409, title: "Сканер уже сменился. Обновите состояние перед остановкой."));
api.MapGet("/scanner/stats", (decimal? feePct, MarketScanner scanner) =>
{
    try { return Results.Ok(scanner.Stats(feePct ?? 0.1m)); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
});
// Signal lab: events found in the scanner recordings and what price did after them; research only, no orders.
api.MapGet("/lab/status", (SignalLab lab) => Results.Ok(lab.Status));
api.MapPost("/lab/run", (SignalLab lab) => lab.Start()
    ? Results.Accepted("/api/v1/lab/status") : Results.Problem(statusCode: 409, title: "Записи уже обрабатываются."));
api.MapGet("/lab/report", (decimal? feePct, SignalLab lab) =>
{
    try { return Results.Ok(lab.Report(feePct ?? 0.1m)); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
});
api.MapGet("/lab/trades", (string type, string mode, int horizon, decimal? feePct, SignalLab lab) =>
{
    try { return Results.Ok(lab.Trades(type, mode, horizon, feePct ?? 0.1m)); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
});
api.MapGet("/scanner/symbols/{symbol}", (string symbol, MarketScanner scanner) => scanner.Detail(symbol) is { } detail
    ? Results.Ok(detail) : Results.Problem(statusCode: 404, title: "Монета не найдена в текущем сканере."));
api.MapGet("/strategies", (StrategyCatalog catalog) => Results.Ok(catalog.Items));
api.MapGet("/paper", (PaperTrading paper) => Results.Ok(paper.Snapshot()));
api.MapGet("/paper/sessions", (PaperTrading paper) => Results.Ok(paper.Active()));
api.MapGet("/bots/{id:guid}/paper", (Guid id, PaperTrading paper) => Results.Ok(paper.SnapshotFor(id.ToString())));
api.MapPost("/paper", (PaperRequest request, PaperTrading paper) =>
{
    try { return Results.Accepted("/api/v1/paper", new { id = paper.Start(request) }); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
    catch (InvalidOperationException e) { return Results.Problem(statusCode: 409, title: e.Message); }
});
api.MapPost("/paper/{id:guid}/stop", (Guid id, bool? force, string? mode, PaperTrading paper) =>
{
    try
    {
        return paper.Stop(id.ToString(), force == true ? "force" : mode ?? "wait")
            ? Results.Accepted("/api/v1/paper", new { id = id.ToString() })
            : Results.Problem(statusCode: 409, title: "Симуляция уже сменилась. Обновите состояние перед остановкой.");
    }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
});
api.MapPost("/paper/{id:guid}/pause", (Guid id, PaperTrading paper) => paper.Pause(id.ToString(), true)
    ? Results.Accepted("/api/v1/paper") : Results.Problem(statusCode: 409, title: "Симуляция уже завершена или сменилась."));
api.MapPost("/paper/{id:guid}/resume", (Guid id, PaperTrading paper) => paper.Pause(id.ToString(), false)
    ? Results.Accepted("/api/v1/paper") : Results.Problem(statusCode: 409, title: "Симуляция уже завершена или сменилась."));
api.MapGet("/workspace", async (IWorkspaceStore store, PaperRunStore runs, PaperTrading paper, CancellationToken ct) =>
{
    var workspace = await store.ReadAsync(ct);
    var byBot = await runs.AllByBotAsync(ct);
    return Results.Ok(workspace with
    {
        Bots = workspace.Bots.Select(b =>
        {
            var botRuns = byBot.GetValueOrDefault(b.Id) ?? [];
            return b with
            {
                Status = paper.StatusFor(b.Id, botRuns.FirstOrDefault()),
                Unrealized = paper.ForBot(b.Id)?.Snapshot().Position?.MarkNetQuote,
                Trades = botRuns.SelectMany(BotTrade.FromRun).OrderBy(t => t.At, StringComparer.Ordinal).TakeLast(10000).ToArray()
            };
        }).ToArray()
    });
});
api.MapGet("/templates", async (BotTemplateStore templates, CancellationToken ct) => Results.Ok(await templates.ListAsync(ct)));
api.MapPost("/templates", async (BotTemplateInput input, StrategyCatalog catalog, BotTemplateStore templates, CancellationToken ct) =>
{
    var errors = BotTemplateStore.Validate(input, catalog);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    return await templates.SaveAsync(input, ct) is { } saved ? Results.Ok(saved)
        : Results.Problem(statusCode: 409, title: $"Сохранено максимальное число шаблонов ({BotTemplateStore.MaxTemplates}). Удалите ненужный.");
});
api.MapDelete("/templates/{id:guid}", async (Guid id, BotTemplateStore templates, CancellationToken ct) =>
    await templates.DeleteAsync(id.ToString(), ct) ? Results.NoContent() : Results.Problem(statusCode: 404, title: "Шаблон не найден."));
api.MapGet("/bots/{id:guid}/runs", async (Guid id, PaperRunStore runs, CancellationToken ct) => Results.Ok(await runs.ListAsync(id.ToString(), 20, ct)));
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
api.MapPut("/bots/{id:guid}", async (Guid id, BotConfig config, HttpContext context, StrategyCatalog catalog, IWorkspaceStore store, PaperTrading paper, CancellationToken ct) =>
{
    if (paper.ForBot(id.ToString()) is not null) return Results.Problem(statusCode: 409, title: "Бот работает в симуляции. Остановите его перед изменением настроек.");
    if (!TryRevision(context, out var revision)) return Results.Problem(statusCode: 428, title: "Нужна версия бота в If-Match.");
    var errors = catalog.Validate(config);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    var result = await store.UpdateAsync(id.ToString(), revision, config, ct);
    return result.Status == StoreStatus.Saved ? Results.Ok(result.Bot) : Failure(result.Status);
});
api.MapDelete("/bots/{id:guid}", async (Guid id, HttpContext context, IWorkspaceStore store, PaperTrading paper, CancellationToken ct) =>
{
    if (paper.ForBot(id.ToString()) is not null) return Results.Problem(statusCode: 409, title: "Бот работает в симуляции. Остановите его перед удалением.");
    if (!TryRevision(context, out var revision)) return Results.Problem(statusCode: 428, title: "Нужна версия бота в If-Match.");
    var result = await store.DeleteAsync(id.ToString(), revision, ct);
    return result.Status == StoreStatus.Saved ? Results.NoContent() : Failure(result.Status);
});
// Starts a live paper run only: no exchange account or order adapter exists.
api.MapPost("/bots/{id:guid}/start", async (Guid id, PaperStart? options, IWorkspaceStore store, StrategyCatalog catalog, PaperTrading paper, CancellationToken ct) =>
{
    var bot = (await store.ReadAsync(ct)).Bots.FirstOrDefault(x => x.Id == id.ToString());
    if (bot is null) return Failure(StoreStatus.Missing);
    if (bot.Strategy != "leader-lag") return Results.Problem(statusCode: 409, title: "Алгоритм ещё не реализован. Запуск недоступен.");
    var errors = catalog.Validate(bot with { StrategyVersion = StrategyReplay.Version });
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    try { return Results.Accepted("/api/v1/paper", new { id = paper.StartBot(bot, options ?? new()) }); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
    catch (InvalidOperationException e) { return Results.Problem(statusCode: 409, title: e.Message); }
});
api.MapPost("/bots/{id:guid}/stop", (Guid id, bool? force, string? mode, PaperTrading paper) =>
{
    try
    {
        return paper.ForBot(id.ToString()) is { } session && paper.Stop(session.Id, force == true ? "force" : mode ?? "wait")
            ? Results.Accepted("/api/v1/paper") : Results.Problem(statusCode: 409, title: "Бот сейчас не работает в симуляции.");
    }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
});
api.MapPost("/bots/{id:guid}/pause", (Guid id, PaperTrading paper) => paper.ForBot(id.ToString()) is { } session && paper.Pause(session.Id, true)
    ? Results.Accepted("/api/v1/paper") : Results.Problem(statusCode: 409, title: "Бот сейчас не работает в симуляции."));
api.MapPost("/bots/{id:guid}/resume", (Guid id, PaperTrading paper) => paper.ForBot(id.ToString()) is { } session && paper.Pause(session.Id, false)
    ? Results.Accepted("/api/v1/paper") : Results.Problem(statusCode: 409, title: "Бот сейчас не работает в симуляции."));

// Validate the database on startup; never silently replace a damaged workspace.
_ = app.Services.GetRequiredService<IWorkspaceStore>();
// Marks runs cut off by the previous shutdown as interrupted before anything reads them.
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
