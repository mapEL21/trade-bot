using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeBot.Api;

// Settings of a bot: its strategy and money management only. Entries and exits are the strategy's own logic
// (docs/BOT_LOGIC.md), never a stop or a target set in the form. RiskPct: the share of the deposit lost, fees included, when
// the strategy's cancel level is reached; it sets the size. MaxLeverage caps the notional of all open trades.
public sealed record SignalParameters(string Signal = SignalParameters.Density, decimal RiskPct = 1.5m, decimal FeePct = .1m,
    int MaxLeverage = 20, int MaxPositions = 3, string[]? Sessions = null)
{
    public const string Density = "Плотность", Spike = "Вынос цены";
    public static readonly string[] Strategies = [Density, Spike];
    public const decimal MinNotional = 5;
    public const int MaxLeverageLimit = 125;

    // Sessions: the trading sessions (TradingSessions ids) the bot opens trades in; null or empty means all of them.
    public bool InSession(long ms) => Sessions is not { Length: > 0 } s || s.Contains(TradingSessions.Of(ms));

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (!Strategies.Contains(Signal)) errors["signal"] = ["Выберите стратегию: плотность или вынос цены."];
        if (RiskPct is < .1m or > 5) errors["riskPct"] = ["Риск на сделку — от 0,1 до 5% депозита."];
        if (FeePct is < 0 or > 1) errors["feePct"] = ["Комиссии круга — от 0 до 1%."];
        if (MaxLeverage is < 1 or > MaxLeverageLimit) errors["maxLeverage"] = [$"Плечо — от 1 до {MaxLeverageLimit}."];
        if (MaxPositions is < 1 or > 20) errors["maxPositions"] = ["Одновременных сделок — от 1 до 20."];
        if (Sessions is { } list && (list.Length > TradingSessions.All.Length || list.Distinct().Count() != list.Length || list.Any(x => !TradingSessions.Valid(x) || x is null)))
            errors["sessions"] = ["Неизвестная торговая сессия."];
        return errors;
    }

    // The engine result of a recording depends on everything here except money; the key names its cached file.
    public string Key()
    {
        var text = $"{BotEngine.Version}|{SignalDetector.Version}|{Signal}|{RiskPct}|{FeePct}|{MaxLeverage}|{MaxPositions}|{string.Join(',', (Sessions ?? []).Order())}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}

// One run of a bot: its settings as they were at the start, from which moment it trades and, once stopped, until when.
public sealed record SignalRun(string Id, string BotId, long FromMs, long? ToMs, DateTimeOffset StartedUtc, decimal Budget, decimal LossLimit,
    SignalParameters Parameters);

// Pending: recordings of the run still being computed; their trades are not in the result yet.
public sealed record SignalRunResult(BotTrade[] Trades, Dictionary<string, int> Skipped, int Signals, int Incomplete, decimal Deposit,
    int Recordings, long? LastDataMs, string? EntriesBlocked, int Pending = 0);
public sealed record SignalRunView(SignalRun Run, SignalRunResult Result, string State);
public sealed record SignalStart(long? FromMs = null);

// Bot runs: the engine goes through each finished recording of the run once per settings key (the result is cached next to
// the recording as bot-<key>.json, computed in the background), then the money pass turns the trades into USDT in time order.
public sealed class SignalBots(PaperRunStore store, SignalLab lab, ILogger<SignalBots> logger) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Queue<(string Directory, SignalParameters Parameters)> queue = new();
    private readonly HashSet<string> queued = [];
    private readonly CancellationTokenSource shutdown = new();
    private Task? worker;

    public static string FileName(SignalParameters p) => $"bot-{p.Key()}.json";

    public async Task<SignalRun> StartAsync(Bot bot, SignalStart request, CancellationToken ct)
    {
        if (bot.Signal is null) throw new ArgumentException("У бота нет настроек.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var from = request.FromMs ?? now;
        if (from < 0 || from > now) throw new ArgumentException("Начало — момент в прошлом или «сейчас».");
        if ((await store.SignalRunsAsync(bot.Id, ct)).Any(r => r.ToMs is null)) throw new InvalidOperationException("Бот уже работает.");
        var run = new SignalRun(Guid.NewGuid().ToString(), bot.Id, from, null, DateTimeOffset.UtcNow, bot.Budget, bot.LossLimit, bot.Signal);
        await store.SaveSignalRunAsync(run, ct);
        return run;
    }

    public async Task<bool> IsRunningAsync(string botId, CancellationToken ct) =>
        (await store.SignalRunsAsync(botId, ct)).Any(r => r.ToMs is null);

    public async Task<bool> StopAsync(string botId, CancellationToken ct)
    {
        var active = (await store.SignalRunsAsync(botId, ct)).FirstOrDefault(r => r.ToMs is null);
        if (active is null) return false;
        await store.SaveSignalRunAsync(active with { ToMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, ct);
        return true;
    }

    // Recordings that may hold trades of the run: one starts at most two hours before its end (the longest recording).
    private IEnumerable<string> RecordingsOf(SignalRun run) => lab.Recordings().Where(d =>
    {
        var start = StartOf(d);
        return start is { } s && s >= run.FromMs - 2 * 3_600_000 && (run.ToMs is not { } to || s < to);
    });

    private static long? StartOf(string directory) =>
        DateTimeOffset.TryParseExact(Path.GetFileName(directory)[..16], "yyyyMMdd'T'HHmmss'Z'", null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t.ToUnixTimeMilliseconds() : null;

    private static EngineResult? Cached(string directory, SignalParameters p)
    {
        var path = Path.Combine(directory, FileName(p));
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<EngineResult>(File.ReadAllText(path), Json) is { Version: BotEngine.Version } r ? r : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    public SignalRunResult Result(SignalRun run)
    {
        var results = new List<EngineResult>();
        var pending = 0;
        foreach (var directory in RecordingsOf(run))
        {
            if (Cached(directory, run.Parameters) is { } r) results.Add(r);
            else { pending++; Enqueue(directory, run.Parameters); }
        }
        return Money(run, results, pending);
    }

    private void Enqueue(string directory, SignalParameters p)
    {
        lock (gate)
        {
            if (!queued.Add($"{directory}|{p.Key()}")) return;
            queue.Enqueue((directory, p));
            if (worker is not { IsCompleted: false }) worker = Task.Run(Work);
        }
    }

    private void Work()
    {
        while (true)
        {
            (string Directory, SignalParameters Parameters) job;
            lock (gate) { if (!queue.TryDequeue(out job)) return; }
            try
            {
                var result = BotEngine.Run(job.Directory, job.Parameters, shutdown.Token);
                var path = Path.Combine(job.Directory, FileName(job.Parameters));
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(result, Json));
                File.Move(path + ".tmp", path, true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or KeyNotFoundException or UnauthorizedAccessException)
            {
                logger.LogWarning(e, "Bot run skipped {Recording}", job.Directory);
            }
            finally { lock (gate) queued.Remove($"{job.Directory}|{job.Parameters.Key()}"); }
        }
    }

    // Trades of the run's period in time order: the notional is the trade's share of the deposit at its entry; after the
    // loss limit no trade is opened.
    public static SignalRunResult Money(SignalRun run, IReadOnlyList<EngineResult> results, int pending = 0)
    {
        var deposit = run.Budget;
        var floor = run.Budget - run.LossLimit;
        var skipped = new Dictionary<string, int>();
        foreach (var (reason, count) in results.SelectMany(r => r.Skipped)) skipped[reason] = skipped.GetValueOrDefault(reason) + count;
        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
        var trades = new List<BotTrade>();
        string? blocked = null;
        var list = results.SelectMany(r => r.Trades).Where(t => t.OpenedMs >= run.FromMs && (run.ToMs is not { } to || t.OpenedMs < to))
            .OrderBy(t => t.OpenedMs).ThenBy(t => t.Symbol, StringComparer.Ordinal).ToList();
        var open = new List<(EngineTrade Trade, decimal Notional)>();
        void Realize(long until)
        {
            foreach (var o in open.Where(o => o.Trade.ClosedMs <= until).OrderBy(o => o.Trade.ClosedMs).ToList())
            {
                open.Remove(o);
                var t = o.Trade;
                var quantity = o.Notional / t.Entry;
                var gross = t.Side == "long" ? quantity * (t.Exit - t.Entry) : quantity * (t.Entry - t.Exit);
                var fee = (o.Notional + quantity * t.Exit) * run.Parameters.FeePct / 200;
                deposit += gross - fee;
                trades.Add(new($"{run.Id}-{t.Symbol}-{t.OpenedMs}", DateTimeOffset.FromUnixTimeMilliseconds(t.ClosedMs).ToString("O"), t.Side,
                    Math.Round(gross, 6), Math.Round(fee, 6), t.Entry, t.Exit, quantity, run.Id, true, t.Symbol, "Binance",
                    DateTimeOffset.FromUnixTimeMilliseconds(t.OpenedMs).ToString("O"), (t.ClosedMs - t.OpenedMs) / 1000.0, Math.Round(o.Notional, 2),
                    t.ExitReason, t.BestBps, t.WorstBps, t.EntryReason));
            }
        }
        foreach (var t in list)
        {
            Realize(t.OpenedMs);
            if (deposit <= floor) { blocked ??= "Достигнут лимит убытка: новые сделки не открываются."; Skip("лимит убытка"); continue; }
            var notional = deposit * t.Fraction;
            if (notional < SignalParameters.MinNotional) { Skip("номинал меньше 5 USDT"); continue; }
            open.Add((t, notional));
        }
        Realize(long.MaxValue);
        return new(trades.ToArray(), skipped, results.Sum(r => r.Signals), results.Sum(r => r.Incomplete), Math.Round(deposit, 4), results.Count,
            results.Count == 0 ? null : results.Max(r => r.LastMs), blocked, pending);
    }

    public async Task<SignalRunView[]> RunsAsync(string botId, CancellationToken ct) =>
        (await store.SignalRunsAsync(botId, ct)).Select(r => new SignalRunView(r, Result(r), r.ToMs is null ? "running" : "stopped")).ToArray();

    public void Dispose() { shutdown.Cancel(); shutdown.Dispose(); }
}
