using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace TradeBot.Api;

// Where orders go. Testnet: Binance futures test network, money that is not real. Live (a real account) stays off until
// the user decides on it separately: Exchange:AllowLive is set nowhere in this project.
public static class ExchangeModes
{
    public const string Testnet = "testnet", Live = "live";
    public static string BaseUrl(IConfiguration configuration, string mode) => mode == Live
        ? configuration["Exchange:LiveUrl"] ?? "https://fapi.binance.com"
        // Binance's futures test network is now its Demo Trading (keys from demo.binance.com), docs of October 2026.
        : configuration["Exchange:TestnetUrl"] ?? "https://demo-fapi.binance.com";
    public static bool Allowed(IConfiguration configuration, string mode) =>
        mode == Testnet || mode == Live && configuration.GetValue<bool>("Exchange:AllowLive");
}

public sealed record ExchangeKeyStatus(string Mode, bool Configured, string? KeyEnd, DateTimeOffset? SavedUtc);
public sealed record ExchangeKeysInput(string Mode, string ApiKey, string ApiSecret);

// API keys entered by the user, kept only on this computer: the secret is encrypted with ASP.NET Core Data Protection (on
// Windows its key is protected by the user's account) in data/exchange-keys.json, which never goes to git, and is never sent
// back to the interface. On a server the keys come from Exchange__<Mode>Key / Exchange__<Mode>Secret instead.
public sealed class ExchangeCredentials(IDataProtectionProvider protection, IConfiguration configuration, IHostEnvironment environment)
{
    private sealed record Stored(string ApiKey, string Secret, DateTimeOffset SavedUtc);
    private readonly IDataProtector protector = protection.CreateProtector("TradeBot.ExchangeKeys.v1");
    private readonly object gate = new();
    private string Path => configuration["Exchange:KeysPath"] ?? System.IO.Path.Combine(environment.ContentRootPath, "data", "exchange-keys.json");

    private Dictionary<string, Stored> Read()
    {
        if (!File.Exists(Path)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, Stored>>(File.ReadAllText(Path)) ?? [];
    }

    public void Save(string mode, string apiKey, string secret)
    {
        lock (gate)
        {
            var all = Read();
            all[mode] = new(apiKey.Trim(), protector.Protect(secret.Trim()), DateTimeOffset.UtcNow);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(all));
        }
    }

    public bool Delete(string mode)
    {
        lock (gate)
        {
            var all = Read();
            if (!all.Remove(mode)) return false;
            File.WriteAllText(Path, JsonSerializer.Serialize(all));
            return true;
        }
    }

    public (string Key, string Secret)? Load(string mode)
    {
        var prefix = mode == ExchangeModes.Live ? "Live" : "Testnet";
        if (configuration[$"Exchange:{prefix}Key"] is { Length: > 0 } key && configuration[$"Exchange:{prefix}Secret"] is { Length: > 0 } secret)
            return (key, secret);
        lock (gate)
        {
            if (!Read().TryGetValue(mode, out var stored)) return null;
            try { return (stored.ApiKey, protector.Unprotect(stored.Secret)); }
            catch (CryptographicException) { return null; }
        }
    }

    public ExchangeKeyStatus Status(string mode)
    {
        lock (gate)
        {
            var stored = Read().GetValueOrDefault(mode);
            var key = Load(mode)?.Key;
            return new(mode, key is not null, key is { Length: >= 4 } k ? k[^4..] : null, stored?.SavedUtc);
        }
    }
}

// Order rules of a symbol: quantity step and minimum, price step, minimum notional.
public sealed record SymbolRules(string Symbol, decimal StepSize, decimal MinQty, decimal TickSize, decimal MinNotional);
// An order as the exchange reports it.
public sealed record ExchangeOrder(string ClientId, string Status, decimal ExecutedQty, decimal AvgPrice, string Side, string Type);
// USDT of the futures account: Wallet — the balance with closed trades; Available — what new orders can use;
// Unrealized — the open positions' result now.
public sealed record ExchangeBalance(decimal Wallet, decimal Available, decimal Unrealized);
public sealed record ExchangePosition(string Symbol, decimal Amount, decimal EntryPrice, decimal UnrealizedProfit);

public sealed class ExchangeException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

// Binance USDT-M futures REST: signed requests (HMAC-SHA256 of the query with the API secret), the exchange's clock offset
// taken into account. Errors are thrown with the exchange's own message.
public sealed class BinanceFuturesClient(HttpClient http, string baseUrl, string apiKey, string secret)
{
    private long offset;
    private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
    // The exchange's clock, as far as the last time sync knows it.
    public long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + offset;

    public async Task SyncTimeAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Send(HttpMethod.Get, "/fapi/v1/time", null, signed: false, ct));
        offset = doc.RootElement.GetProperty("serverTime").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private async Task<string> Send(HttpMethod method, string path, Dictionary<string, string>? query, bool signed, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>(query ?? []);
        if (signed)
        {
            parameters["recvWindow"] = "5000";
            parameters["timestamp"] = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + offset).ToString(CultureInfo.InvariantCulture);
        }
        var text = string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        if (signed)
        {
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(text)));
            text += $"&signature={signature}";
        }
        using var request = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + path + (text.Length > 0 ? "?" + text : ""));
        request.Headers.Add("X-MBX-APIKEY", apiKey);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return body;
        int? code = null; var message = body;
        try
        {
            using var error = JsonDocument.Parse(body);
            code = error.RootElement.GetProperty("code").GetInt32();
            message = error.RootElement.GetProperty("msg").GetString() ?? body;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
        throw new ExchangeException($"Binance: {message}", code);
    }

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDecimal()
        : decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    public async Task<ExchangeBalance> BalanceAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Send(HttpMethod.Get, "/fapi/v2/balance", null, true, ct));
        decimal Field(JsonElement a, string name) => a.TryGetProperty(name, out var v) ? Dec(v) : 0;
        return doc.RootElement.EnumerateArray().Where(a => a.GetProperty("asset").GetString() == "USDT")
            .Select(a => new ExchangeBalance(Field(a, "balance"), Field(a, "availableBalance"), Field(a, "crossUnPnl")))
            .FirstOrDefault() ?? new(0, 0, 0);
    }

    public async Task<SymbolRules> RulesAsync(string symbol, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Send(HttpMethod.Get, "/fapi/v1/exchangeInfo", null, false, ct));
        var s = doc.RootElement.GetProperty("symbols").EnumerateArray().FirstOrDefault(x => x.GetProperty("symbol").GetString() == symbol);
        if (s.ValueKind != JsonValueKind.Object) throw new ExchangeException($"Binance: инструмента {symbol} нет на этой бирже.");
        decimal Filter(string type, string field) => s.GetProperty("filters").EnumerateArray()
            .Where(f => f.GetProperty("filterType").GetString() == type).Select(f => Dec(f.GetProperty(field))).FirstOrDefault();
        return new(symbol, Filter("MARKET_LOT_SIZE", "stepSize") is > 0 and var step ? step : Filter("LOT_SIZE", "stepSize"),
            Filter("LOT_SIZE", "minQty"), Filter("PRICE_FILTER", "tickSize"), Filter("MIN_NOTIONAL", "notional"));
    }

    public async Task SetupAsync(string symbol, int leverage, CancellationToken ct)
    {
        try { await Send(HttpMethod.Post, "/fapi/v1/marginType", new() { ["symbol"] = symbol, ["marginType"] = "ISOLATED" }, true, ct); }
        // -4046: the margin type is already isolated.
        catch (ExchangeException e) when (e.Code == -4046) { }
        await Send(HttpMethod.Post, "/fapi/v1/leverage", new() { ["symbol"] = symbol, ["leverage"] = leverage.ToString(CultureInfo.InvariantCulture) }, true, ct);
    }

    public async Task<ExchangeOrder> MarketAsync(string symbol, string side, decimal quantity, string clientId, bool reduceOnly, CancellationToken ct) =>
        Order(await Send(HttpMethod.Post, "/fapi/v1/order", new()
        {
            ["symbol"] = symbol, ["side"] = side, ["type"] = "MARKET", ["quantity"] = S(quantity), ["newClientOrderId"] = clientId,
            ["reduceOnly"] = reduceOnly ? "true" : "false", ["newOrderRespType"] = "RESULT",
        }, true, ct));

    // The protective stop: closes the whole position at the market when the mark price reaches the stop price.
    public async Task<ExchangeOrder> StopAsync(string symbol, string side, decimal stopPrice, string clientId, CancellationToken ct) =>
        Order(await Send(HttpMethod.Post, "/fapi/v1/order", new()
        {
            ["symbol"] = symbol, ["side"] = side, ["type"] = "STOP_MARKET", ["stopPrice"] = S(stopPrice), ["closePosition"] = "true",
            ["workingType"] = "MARK_PRICE", ["newClientOrderId"] = clientId,
        }, true, ct));

    // The account's taker fee rate (0.0005 = 0.05%) on a coin.
    public async Task<decimal> TakerFeeAsync(string symbol, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Send(HttpMethod.Get, "/fapi/v1/commissionRate", new() { ["symbol"] = symbol }, true, ct));
        return Dec(doc.RootElement.GetProperty("takerCommissionRate"));
    }

    // Every open order of the coin (stops left behind included).
    public async Task CancelAllAsync(string symbol, CancellationToken ct) =>
        await Send(HttpMethod.Delete, "/fapi/v1/allOpenOrders", new() { ["symbol"] = symbol }, true, ct);

    public async Task<ExchangeOrder?> QueryAsync(string symbol, string clientId, CancellationToken ct)
    {
        try { return Order(await Send(HttpMethod.Get, "/fapi/v1/order", new() { ["symbol"] = symbol, ["origClientOrderId"] = clientId }, true, ct)); }
        // -2013: no such order.
        catch (ExchangeException e) when (e.Code == -2013) { return null; }
    }

    public async Task CancelAsync(string symbol, string clientId, CancellationToken ct)
    {
        try { await Send(HttpMethod.Delete, "/fapi/v1/order", new() { ["symbol"] = symbol, ["origClientOrderId"] = clientId }, true, ct); }
        // -2011: already filled or cancelled.
        catch (ExchangeException e) when (e.Code is -2011 or -2013) { }
    }

    public async Task<ExchangePosition[]> PositionsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Send(HttpMethod.Get, "/fapi/v2/positionRisk", null, true, ct));
        return doc.RootElement.EnumerateArray().Select(p => new ExchangePosition(p.GetProperty("symbol").GetString()!,
            Dec(p.GetProperty("positionAmt")), Dec(p.GetProperty("entryPrice")), Dec(p.GetProperty("unRealizedProfit"))))
            .Where(p => p.Amount != 0).ToArray();
    }

    private static ExchangeOrder Order(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var o = doc.RootElement;
        return new(o.GetProperty("clientOrderId").GetString()!, o.GetProperty("status").GetString()!,
            o.TryGetProperty("executedQty", out var q) ? Dec(q) : 0, o.TryGetProperty("avgPrice", out var p) ? Dec(p) : 0,
            o.GetProperty("side").GetString()!, o.GetProperty("type").GetString()!);
    }
}

// The HTTP client the exchange is reached through (replaced by a fake exchange in tests).
public sealed class ExchangeHttp(HttpClient client)
{
    public HttpClient Client => client;
}

// One line of the exchange journal of a run: every order sent, its result or the exchange's error, in words.
public sealed record ExchangeLogEntry(string RunId, long AtMs, string Symbol, string Action, string Side, decimal Quantity, decimal Price,
    string ClientId, string Status, string Message);
public sealed record ExchangeHolding(string Symbol, string Side, decimal Quantity, decimal Entry, decimal StopPrice, long OpenedMs);

// Places the engine's decisions as real orders, one at a time in the order they come: a market entry sized from the run's
// deposit (the bot's allocated money plus what its real trades made) and the engine's share of it, isolated margin with the
// leverage the size needs, then at once a protective stop on the exchange beyond the strategy's cancel level, so the
// position is closed even if this computer, the connection or the application fails. An exit cancels the stop and closes
// with a reduce-only market order. Every order has its own client id: when an answer is lost the order is looked up by it
// instead of being sent again. Reconcile notices positions the exchange closed (the stop) and ones the bot does not know.
public sealed class ExchangeExecutor : IDisposable
{
    // The protective stop sits this far beyond the cancel level: in normal work the bot exits first by its own logic.
    public const decimal StopBufferPct = .3m;
    // An entry decided on data older than this (by the exchange's clock) is not sent: the market may already be elsewhere.
    public const long MaxDelayMs = 3_000;
    private sealed record Holding(string Symbol, int Side, decimal Quantity, decimal Entry, decimal StopPrice, string StopId, long OpenedMs);

    private readonly BinanceFuturesClient client;
    private readonly SignalRun run;
    private readonly PaperRunStore store;
    private readonly ILogger logger;
    private readonly System.Threading.Channels.Channel<EngineIntent> queue = System.Threading.Channels.Channel.CreateUnbounded<EngineIntent>();
    private readonly Dictionary<string, Holding> holdings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SymbolRules> rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> leverage = new(StringComparer.Ordinal);
    private readonly HashSet<string> strangers = new(StringComparer.Ordinal);
    // Coins the run has traded: a stop sweeps them for leftover positions and orders.
    private readonly HashSet<string> touched = new(StringComparer.Ordinal);
    private readonly DailyLoss daily;
    private readonly long maxDelayMs;
    private readonly Task worker;
    private decimal realized;
    private int sequence;
    private bool synced;

    public ExchangeExecutor(BinanceFuturesClient client, SignalRun run, PaperRunStore store, ILogger logger, long maxDelayMs = MaxDelayMs)
    {
        this.client = client; this.run = run; this.store = store; this.logger = logger; this.maxDelayMs = maxDelayMs;
        daily = new DailyLoss(run.Budget, run.Parameters.DailyLossPct);
        worker = Task.Run(Work);
    }

    public void Enqueue(EngineIntent intent) => queue.Writer.TryWrite(intent);
    public void Reconcile() => queue.Writer.TryWrite(new("reconcile", "", 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, 0, null, ""));
    public decimal Deposit => run.Budget + realized;

    public ExchangeHolding[] Holdings()
    {
        lock (holdings)
            return holdings.Values.Select(h => new ExchangeHolding(h.Symbol, h.Side > 0 ? "long" : "short", h.Quantity, h.Entry, h.StopPrice, h.OpenedMs)).ToArray();
    }

    // Waits until every order sent so far is done (tests and a stop use it).
    public async Task DrainAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        drains.Enqueue(done);
        queue.Writer.TryWrite(new("drain", "", 0, 0, 0, 0, null, ""));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource> drains = new();

    // Closes every position of the run, then whatever is still open on the coins it traded (positions and orders), and stops
    // taking orders; waits for the queue to finish. Positions on other coins (opened by hand) are left alone.
    public async Task FlattenAndStopAsync()
    {
        queue.Writer.TryWrite(new("flatten", "", 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, 0, null, "бот остановлен"));
        queue.Writer.TryComplete();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(30)); } catch (TimeoutException) { logger.LogWarning("Exchange executor did not finish in time"); }
    }

    private string Id(string kind)
    {
        var prefix = run.Id.Replace("-", "");
        return $"tb{prefix[..Math.Min(10, prefix.Length)]}{Interlocked.Increment(ref sequence):D5}{kind}";
    }
    private static decimal Floor(decimal value, decimal step) => step > 0 ? Math.Floor(value / step) * step : value;
    private static decimal Ceil(decimal value, decimal step) => step > 0 ? Math.Ceiling(value / step) * step : value;
    private static string Side(int side) => side > 0 ? "BUY" : "SELL";
    private static string Money(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture).Replace('.', ',');

    private Task Log(string symbol, string action, string side, decimal quantity, decimal price, string clientId, string status, string message) =>
        store.AppendExchangeLogAsync(new(run.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), symbol, action, side, quantity, price, clientId, status, message),
            CancellationToken.None);

    private async Task Work()
    {
        await foreach (var intent in queue.Reader.ReadAllAsync())
        {
            try
            {
                if (intent.Kind == "drain") { if (drains.TryDequeue(out var done)) done.TrySetResult(); continue; }
                if (!synced) { await client.SyncTimeAsync(CancellationToken.None); synced = true; }
                switch (intent.Kind)
                {
                    case "entry": await Entry(intent); break;
                    case "exit": await Exit(intent.Symbol, intent.Reason); break;
                    case "reconcile": await ReconcileNow(); break;
                    case "flatten":
                        List<string> open;
                        lock (holdings) open = holdings.Keys.ToList();
                        foreach (var symbol in open) await Exit(symbol, intent.Reason);
                        await Sweep();
                        break;
                }
            }
            catch (Exception e) when (e is ExchangeException or HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning(e, "Exchange order failed");
                await Log(intent.Symbol, "ошибка", intent.Side == 0 ? "" : Side(intent.Side), 0, 0, "", "error", e.Message);
            }
            // Anything else must not stop the executor silently: the next orders and the reconciliation still have to run.
            catch (Exception e)
            {
                logger.LogError(e, "Exchange executor error");
                try { await Log(intent.Symbol, "ошибка", intent.Side == 0 ? "" : Side(intent.Side), 0, 0, "", "error", $"Внутренняя ошибка: {e.Message}"); }
                catch (Exception) { }
            }
        }
    }

    // A market order; when the answer is lost the order is looked up by its client id before anything else is sent.
    private async Task<ExchangeOrder> Market(string symbol, string side, decimal quantity, string id, bool reduceOnly)
    {
        try { return await client.MarketAsync(symbol, side, quantity, id, reduceOnly, CancellationToken.None); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return await client.QueryAsync(symbol, id, CancellationToken.None)
                ?? throw new ExchangeException($"Ответ биржи потерян, заявка {id} на бирже не найдена: она не исполнена.");
        }
    }

    private async Task Entry(EngineIntent intent)
    {
        lock (holdings) if (holdings.ContainsKey(intent.Symbol)) return;
        var side = Side(intent.Side);
        var delay = client.Now - intent.Ms;
        if (delay > maxDelayMs)
        {
            await Log(intent.Symbol, "пропуск", side, 0, intent.Price, "", "skipped",
                $"Данные опаздывают на {Money(delay / 1000m)} с: вход не отправлен.");
            return;
        }
        if (Deposit <= run.Budget - run.LossLimit)
        {
            await Log(intent.Symbol, "пропуск", side, 0, intent.Price, "", "skipped", "Достигнут лимит убытка бота: новые сделки на бирже не открываются.");
            return;
        }
        if (daily.Blocked(client.Now))
        {
            await Log(intent.Symbol, "пропуск", side, 0, intent.Price, "", "skipped",
                $"Достигнут дневной лимит убытка ({Money(daily.Limit)} USDT): новые сделки — со следующего дня по Москве.");
            return;
        }
        if (!rules.TryGetValue(intent.Symbol, out var r)) rules[intent.Symbol] = r = await client.RulesAsync(intent.Symbol, CancellationToken.None);
        var notional = Deposit * intent.Fraction;
        var quantity = Floor(notional / intent.Price, r.StepSize);
        if (quantity < r.MinQty || quantity * intent.Price < r.MinNotional)
        {
            await Log(intent.Symbol, "пропуск", side, quantity, intent.Price, "", "skipped",
                $"Размер {Money(quantity * intent.Price)} USDT меньше минимальной заявки биржи ({Money(r.MinNotional)} USDT).");
            return;
        }
        // Isolated margin: the leverage the notional needs out of the deposit, within the bot's limit.
        var needed = (int)Math.Clamp(Math.Ceiling(notional / Deposit), 1, run.Parameters.MaxLeverage);
        if (leverage.GetValueOrDefault(intent.Symbol) != needed)
        {
            await client.SetupAsync(intent.Symbol, needed, CancellationToken.None);
            leverage[intent.Symbol] = needed;
        }
        var id = Id("e");
        touched.Add(intent.Symbol);
        var order = await Market(intent.Symbol, side, quantity, id, false);
        if (order.ExecutedQty <= 0)
        {
            await Log(intent.Symbol, "вход", side, quantity, intent.Price, id, order.Status, "Заявка не исполнилась.");
            return;
        }
        await Log(intent.Symbol, "вход", side, order.ExecutedQty, order.AvgPrice, id, order.Status, intent.Reason);
        var cancel = intent.CancelPrice ?? order.AvgPrice;
        var stopPrice = intent.Side > 0 ? Floor(cancel * (1 - StopBufferPct / 100), r.TickSize) : Ceil(cancel * (1 + StopBufferPct / 100), r.TickSize);
        var stopId = Id("s");
        try
        {
            var stop = await client.StopAsync(intent.Symbol, Side(-intent.Side), stopPrice, stopId, CancellationToken.None);
            await Log(intent.Symbol, "защитный стоп", Side(-intent.Side), order.ExecutedQty, stopPrice, stopId, stop.Status, "Стоп на бирже за уровнем отмены сценария.");
            lock (holdings) holdings[intent.Symbol] = new(intent.Symbol, intent.Side, order.ExecutedQty, order.AvgPrice, stopPrice, stopId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception e) when (e is ExchangeException or HttpRequestException or TaskCanceledException)
        {
            // Never leave a position without protection: close it at once.
            await Log(intent.Symbol, "ошибка", Side(-intent.Side), order.ExecutedQty, stopPrice, stopId, "error", $"Защитный стоп не поставлен ({e.Message}): позиция закрывается.");
            var close = await Market(intent.Symbol, Side(-intent.Side), order.ExecutedQty, Id("x"), true);
            var pnl = intent.Side * order.ExecutedQty * (close.AvgPrice - order.AvgPrice) - order.ExecutedQty * (order.AvgPrice + close.AvgPrice) * run.Parameters.FeePct / 200;
            Realize(pnl);
            await Log(intent.Symbol, "выход", Side(-intent.Side), close.ExecutedQty, close.AvgPrice, close.ClientId, close.Status,
                $"Закрыта: не удалось поставить защитный стоп. Результат {Money(pnl)} USDT.");
        }
    }

    private async Task Exit(string symbol, string reason)
    {
        Holding? h;
        lock (holdings) holdings.Remove(symbol, out h);
        if (h is null) return;
        await client.CancelAsync(symbol, h.StopId, CancellationToken.None);
        var id = Id("x");
        var order = await Market(symbol, Side(-h.Side), h.Quantity, id, true);
        var pnl = h.Side * h.Quantity * (order.AvgPrice - h.Entry) - h.Quantity * (h.Entry + order.AvgPrice) * run.Parameters.FeePct / 200;
        Realize(pnl);
        await Log(symbol, "выход", Side(-h.Side), order.ExecutedQty, order.AvgPrice, id, order.Status, $"{reason}. Результат {Money(pnl)} USDT (комиссия — оценка).");
    }

    private void Realize(decimal pnl)
    {
        realized += pnl;
        daily.Add(client.Now, pnl);
    }

    // After the run's own exits: a position still open on a coin the run traded (an answer lost, a fill the bot did not see)
    // is closed, and every order left on those coins is cancelled.
    private async Task Sweep()
    {
        if (touched.Count == 0) return;
        var positions = await client.PositionsAsync(CancellationToken.None);
        foreach (var p in positions.Where(p => touched.Contains(p.Symbol) && p.Amount != 0))
        {
            var close = await Market(p.Symbol, p.Amount > 0 ? "SELL" : "BUY", Math.Abs(p.Amount), Id("x"), true);
            await Log(p.Symbol, "выход", close.Side, close.ExecutedQty, close.AvgPrice, close.ClientId, close.Status,
                "Остановка: закрыт остаток позиции на монете бота.");
        }
        foreach (var symbol in touched)
        {
            try { await client.CancelAllAsync(symbol, CancellationToken.None); }
            catch (ExchangeException e) { await Log(symbol, "ошибка", "", 0, 0, "", "error", $"Не удалось снять заявки: {e.Message}"); }
        }
    }

    private async Task ReconcileNow()
    {
        var positions = await client.PositionsAsync(CancellationToken.None);
        List<Holding> mine;
        lock (holdings) mine = holdings.Values.ToList();
        foreach (var h in mine.Where(h => positions.All(p => p.Symbol != h.Symbol)))
        {
            // The exchange closed it: the protective stop (or a liquidation).
            lock (holdings) holdings.Remove(h.Symbol);
            var stop = await client.QueryAsync(h.Symbol, h.StopId, CancellationToken.None);
            var price = stop is { AvgPrice: > 0 } ? stop.AvgPrice : h.StopPrice;
            var pnl = h.Side * h.Quantity * (price - h.Entry) - h.Quantity * (h.Entry + price) * run.Parameters.FeePct / 200;
            Realize(pnl);
            await Log(h.Symbol, "закрыта биржей", Side(-h.Side), h.Quantity, price, h.StopId, stop?.Status ?? "unknown",
                $"Позиции на бирже больше нет: сработал защитный стоп. Результат {Money(pnl)} USDT.");
        }
        foreach (var p in positions.Where(p => mine.All(h => h.Symbol != p.Symbol) && strangers.Add(p.Symbol)))
            await Log(p.Symbol, "внимание", p.Amount > 0 ? "BUY" : "SELL", Math.Abs(p.Amount), p.EntryPrice, "", "unknown",
                "На бирже есть позиция, о которой бот не знает (например, открыта до перезапуска или вручную). Бот её не трогает.");
    }

    public void Dispose() => queue.Writer.TryComplete();
}
