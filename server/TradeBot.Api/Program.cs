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
builder.Services.AddSingleton(_ => new ResearchMarkets(new HttpClient { Timeout = TimeSpan.FromSeconds(8) }));
builder.Services.AddHostedService(services => services.GetRequiredService<MarketResearch>());
builder.Services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(
    builder.Configuration["Storage:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "workspace.db")));
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
    // Local single-user API. Never expose it through a public proxy without authentication.
    if (context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]"))
    {
        await Results.Problem(statusCode: 403, title: "Разрешён только локальный доступ.").ExecuteAsync(context);
        return;
    }
    var origin = context.Request.Headers.Origin.ToString();
    var allowedOrigins = new[] { builder.Configuration["Ui:Origin"] ?? "http://127.0.0.1:5173", "http://localhost:5173", $"http://localhost:{port}", $"http://127.0.0.1:{port}" };
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
api.MapGet("/research", (MarketResearch research) => Results.Ok(research.Snapshot()));
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
api.MapPost("/research", (ResearchRequest request, MarketResearch research) =>
{
    try { return Results.Accepted("/api/v1/research", new { id = research.Start(request) }); }
    catch (ArgumentException e) { return Results.Problem(statusCode: 400, title: e.Message); }
    catch (InvalidOperationException e) { return Results.Problem(statusCode: 409, title: e.Message); }
});
api.MapPost("/research/{id:guid}/stop", (Guid id, MarketResearch research) => research.Stop(id.ToString())
    ? Results.Accepted("/api/v1/research", new { id = id.ToString() })
    : Results.Problem(statusCode: 409, title: "Сессия уже сменилась. Обновите состояние перед остановкой."));
api.MapGet("/research/stream", async (HttpContext context, MarketResearch research) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    try
    {
        while (!context.RequestAborted.IsCancellationRequested)
        {
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(research.Snapshot(), json)}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(1000, context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
});
api.MapGet("/strategies", (StrategyCatalog catalog) => Results.Ok(catalog.Items));
api.MapGet("/workspace", async (IWorkspaceStore store, CancellationToken ct) => Results.Ok(await store.ReadAsync(ct)));
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
api.MapPut("/bots/{id:guid}", async (Guid id, BotConfig config, HttpContext context, StrategyCatalog catalog, IWorkspaceStore store, CancellationToken ct) =>
{
    if (!TryRevision(context, out var revision)) return Results.Problem(statusCode: 428, title: "Нужна версия бота в If-Match.");
    var errors = catalog.Validate(config);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    var result = await store.UpdateAsync(id.ToString(), revision, config, ct);
    return result.Status == StoreStatus.Saved ? Results.Ok(result.Bot) : Failure(result.Status);
});
api.MapDelete("/bots/{id:guid}", async (Guid id, HttpContext context, IWorkspaceStore store, CancellationToken ct) =>
{
    if (!TryRevision(context, out var revision)) return Results.Problem(statusCode: 428, title: "Нужна версия бота в If-Match.");
    var result = await store.DeleteAsync(id.ToString(), revision, ct);
    return result.Status == StoreStatus.Saved ? Results.NoContent() : Failure(result.Status);
});
api.MapPost("/bots/{id:guid}/start", async (Guid id, IWorkspaceStore store, CancellationToken ct) =>
{
    if (!(await store.ReadAsync(ct)).Bots.Any(x => x.Id == id.ToString())) return Failure(StoreStatus.Missing);
    return Results.Problem(statusCode: 409, title: "Алгоритм ещё не реализован. Запуск недоступен.");
});

// Validate the database on startup; never silently replace a damaged workspace.
_ = app.Services.GetRequiredService<IWorkspaceStore>();
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
