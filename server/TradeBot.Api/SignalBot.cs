namespace TradeBot.Api;

// Settings of a bot that trades one signal of the lab. RiskPct: the share of the current deposit lost when the stop is hit,
// fees included; the position size follows from it: notional = deposit × RiskPct ÷ (stop distance + round-trip fees).
// The stop is StopRanges typical 1-minute ranges of the coin away from the entry. A trade whose fees would take more than
// MaxFeeShare of the risk is skipped: on a coin that barely moves the size, the leverage and the fees grow until they eat it.
public sealed record SignalParameters(string Signal = "Вынос цены", string Mode = "follow", int HoldSeconds = 300, decimal RiskPct = 1.5m,
    decimal StopRanges = 2, decimal MaxFeeShare = .3m, decimal FeePct = .1m, int MaxLeverage = 20, int MaxPositions = 3,
    string[]? Sessions = null)
{
    // Sessions: the trading sessions (TradingSessions ids) the bot opens trades in; null or empty means all of them.
    public bool InSession(long ms) => Sessions is not { Length: > 0 } s || s.Contains(TradingSessions.Of(ms));
    public const decimal MinNotional = 5;
    public const int MaxLeverageLimit = 125;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (!SignalDetector.Types.Contains(Signal)) errors["signal"] = ["Выберите сигнал из лаборатории."];
        if (Mode is not ("follow" or "fade")) errors["mode"] = ["Направление — по движению или против."];
        if (HoldSeconds is < 5 or > LabPath.Seconds) errors["holdSeconds"] = [$"Удержание — от 5 до {LabPath.Seconds} с."];
        if (RiskPct is < .1m or > 5) errors["riskPct"] = ["Риск на сделку — от 0,1 до 5% депозита."];
        if (StopRanges is < .5m or > 20) errors["stopRanges"] = ["Стоп — от 0,5 до 20 обычных минутных свечей."];
        if (MaxFeeShare is < .05m or > .9m) errors["maxFeeShare"] = ["Доля комиссии в риске — от 5 до 90%."];
        if (FeePct is < 0 or > 1) errors["feePct"] = ["Комиссии круга — от 0 до 1%."];
        if (MaxLeverage is < 1 or > MaxLeverageLimit) errors["maxLeverage"] = [$"Плечо — от 1 до {MaxLeverageLimit}."];
        if (MaxPositions is < 1 or > 20) errors["maxPositions"] = ["Одновременных сделок — от 1 до 20."];
        if (Sessions is { } list && (list.Length > TradingSessions.All.Length || list.Distinct().Count() != list.Length || list.Any(x => !TradingSessions.Valid(x) || x is null)))
            errors["sessions"] = ["Неизвестная торговая сессия."];
        return errors;
    }
}

// One run of a signal bot: its settings as they were at the start, from which moment it trades and, once stopped, until when.
// A run started "from now" follows new recordings; one started from an earlier moment also replays the recordings since then.
public sealed record SignalRun(string Id, string BotId, long FromMs, long? ToMs, DateTimeOffset StartedUtc, decimal Budget, decimal LossLimit,
    SignalParameters Parameters);

public sealed record SignalRunResult(BotTrade[] Trades, Dictionary<string, int> Skipped, int Signals, int Incomplete, decimal Deposit,
    int Recordings, long? LastDataMs, string? EntriesBlocked);
public sealed record SignalRunView(SignalRun Run, SignalRunResult Result, string State);
public sealed record SignalStart(long? FromMs = null);

// The bot's decisions and simulated execution over processed recordings. Every input is in a recording; the result depends
// only on the recordings and the settings, so a run read twice gives the same trades, and a run "now" is the same as the
// check of the same recordings later. Entry: 250 ms after the event at the opposite best price (as in the lab). Exit: the
// stop when the exit-side best price of a second reaches it (filled at the worst price of that second), otherwise at the
// end of the holding time; a second without a fresh book moves the time exit to the next second that has one. A position
// whose recording ended first is left out of the results and counted as incomplete. Funding and depth beyond the best
// price are not modelled.
public static class SignalEngine
{
    public const long Latency = 250;

    private sealed record Open(string Symbol, long ExitMs, decimal Notional, BotTrade? Trade);

    public static SignalRunResult Run(SignalRun run, IEnumerable<(LabRecording Recording, Dictionary<int, LabPath> Paths)> recordings)
    {
        var p = run.Parameters;
        var follow = p.Mode == "follow";
        var deposit = run.Budget;
        var floor = run.Budget - run.LossLimit;
        var trades = new List<BotTrade>();
        var skipped = new Dictionary<string, int>();
        int signals = 0, incomplete = 0, count = 0;
        long? lastData = null;
        string? blocked = null;
        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;

        foreach (var (recording, paths) in recordings)
        {
            count++;
            lastData = Math.Max(lastData ?? 0, recording.LastMs);
            var open = new List<Open>();
            void Realize(long until)
            {
                foreach (var o in open.Where(o => o.ExitMs <= until).OrderBy(o => o.ExitMs).ToList())
                {
                    open.Remove(o);
                    if (o.Trade is null) { incomplete++; continue; }
                    deposit += o.Trade.Gross - o.Trade.Fee;
                    trades.Add(o.Trade);
                }
            }
            var events = recording.Events.Select((e, i) => (e, i))
                .Where(x => x.e.Type == p.Signal && x.e.AtMs >= run.FromMs && (run.ToMs is not { } to || x.e.AtMs < to))
                .OrderBy(x => x.e.AtMs).ThenBy(x => x.i);
            foreach (var (e, index) in events)
            {
                Realize(e.AtMs);
                signals++;
                if (!p.InSession(e.AtMs)) { Skip("вне выбранных торговых сессий"); continue; }
                if (deposit <= floor) { blocked ??= "Достигнут лимит убытка: новые сделки не открываются."; Skip("лимит убытка"); continue; }
                if (open.Any(o => o.Symbol == e.Symbol)) { Skip("уже в сделке по этой монете"); continue; }
                if (open.Count >= p.MaxPositions) { Skip("достигнут предел одновременных сделок"); continue; }
                if (e.TypicalPct is not { } typical || typical <= 0) { Skip("мало истории монеты для стопа"); continue; }
                var stopPct = p.StopRanges * typical;
                if (p.FeePct / (stopPct + p.FeePct) > p.MaxFeeShare) { Skip("комиссия заняла бы слишком большую часть риска"); continue; }
                var notional = deposit * p.RiskPct / 100 / ((stopPct + p.FeePct) / 100);
                notional = Math.Min(notional, deposit * p.MaxLeverage - open.Sum(o => o.Notional));
                if (notional < SignalParameters.MinNotional) { Skip("не хватает депозита с учётом плеча"); continue; }
                if (e.EntryAsk is not { } ask || e.EntryBid is not { } bid || ask <= 0 || bid <= 0 || !paths.TryGetValue(index, out var path))
                { Skip("нет свежего стакана в момент входа"); continue; }
                var entryMs = e.AtMs + Latency;
                var trade = Simulate(run, e, path, follow == e.Direction > 0, bid, ask, stopPct, Math.Round(notional, 2), entryMs, $"{run.Id}-{e.Symbol}-{e.AtMs}");
                open.Add(new(e.Symbol, trade?.Exit ?? entryMs + p.HoldSeconds * 1000L, Math.Round(notional, 2), trade?.Trade));
            }
            Realize(long.MaxValue);
        }
        return new(trades.ToArray(), skipped, signals, incomplete, Math.Round(deposit, 4), count, lastData, blocked);
    }

    private static (long Exit, BotTrade? Trade)? Simulate(SignalRun run, LabEvent e, LabPath path, bool longSide, decimal bid, decimal ask,
        decimal stopPct, decimal notional, long entryMs, string id)
    {
        var p = run.Parameters;
        var mid = (bid + ask) / 2;
        var entry = longSide ? ask : bid;
        var stop = longSide ? entry * (1 - stopPct / 100) : entry * (1 + stopPct / 100);
        decimal best = 0, worst = 0;
        decimal Move(decimal price) => longSide ? price / entry - 1 : 1 - price / entry;
        for (var k = 0; k < LabPath.Seconds; k++)
        {
            if (path.Samples.ElementAtOrDefault(k) is not { Length: 4 } s) continue;
            var last = LabPath.Abs(longSide ? s[0] : s[1], mid);
            var extreme = LabPath.Abs(longSide ? s[2] : s[3], mid);
            worst = Math.Min(worst, Move(extreme));
            best = Math.Max(best, Move(last));
            var exitMs = entryMs + (k + 1) * 1000L;
            if (longSide ? extreme <= stop : extreme >= stop) return (exitMs, Trade(extreme, "стоп"));
            if (k + 1 >= p.HoldSeconds) return (exitMs, Trade(last, k + 1 == p.HoldSeconds ? "время" : "время (стакан обновился позже)"));

            BotTrade Trade(decimal exit, string reason)
            {
                var quantity = notional / entry;
                var gross = longSide ? quantity * (exit - entry) : quantity * (entry - exit);
                var fee = (notional + quantity * exit) * p.FeePct / 200;
                return new BotTrade(id, DateTimeOffset.FromUnixTimeMilliseconds(exitMs).ToString("O"), longSide ? "long" : "short",
                    Math.Round(gross, 6), Math.Round(fee, 6), entry, exit, quantity, run.Id, true, e.Symbol, "Binance",
                    OpenedAt: DateTimeOffset.FromUnixTimeMilliseconds(entryMs).ToString("O"), HoldSeconds: k + 1, Notional: notional,
                    ExitReason: reason, MaxFavorableBps: Math.Round(best * 10_000, 2), MaxAdverseBps: Math.Round(worst * 10_000, 2));
            }
        }
        // The recording ended before the exit: the result is unknown.
        return null;
    }
}

// Signal bot runs: stored next to the paper runs, results computed from the processed recordings and cached until new
// recordings are processed.
public sealed class SignalBots(PaperRunStore store, SignalLab lab)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string Key, SignalRunResult Result)> cache = [];

    public async Task<SignalRun> StartAsync(Bot bot, SignalStart request, CancellationToken ct)
    {
        if (bot.Signal is null) throw new ArgumentException("У бота нет настроек сигнала.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var from = request.FromMs ?? now;
        if (from < 0 || from > now) throw new ArgumentException("Начало проверки — момент в прошлом или «сейчас».");
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

    public SignalRunResult Result(SignalRun run)
    {
        var key = $"{run.ToMs}|{lab.Fingerprint()}";
        lock (gate) if (cache.TryGetValue(run.Id, out var hit) && hit.Key == key) return hit.Result;
        var result = SignalEngine.Run(run, lab.WithPaths(run.Parameters.Signal, run.FromMs, run.ToMs));
        lock (gate) cache[run.Id] = (key, result);
        return result;
    }

    public async Task<SignalRunView[]> RunsAsync(string botId, CancellationToken ct) =>
        (await store.SignalRunsAsync(botId, ct)).Select(r => new SignalRunView(r, Result(r), r.ToMs is null ? "running" : "stopped")).ToArray();
}
