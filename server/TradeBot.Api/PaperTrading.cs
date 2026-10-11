using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// For a bot, SourceExchange is its signal exchange and RequireSourceLeader refuses a run in which the bot's own exchange leads.
public sealed record PaperRequest(string Coin, string SourceExchange, string Exchange, int Seconds, ReplaySettings Settings,
    int DelayMs = 50, bool Counter = false, int MaxMb = 256, string? BotId = null, string? BotName = null, decimal? LossLimit = null,
    bool RequireSourceLeader = false, decimal? Budget = null);
public sealed record PaperSnapshot(string? Id, string State, string Explanation, string? Coin, string? SourceExchange, string? Exchange,
    ReplaySettings? Settings, int DelayMs, bool Counter, string? RecordingDirectory, double SessionSeconds, string? Leader,
    string? ExecutionExchange, decimal? NormalBasisBps, ReplayLeader[] Candidates, ReplayDecision[] Decisions, ReplayTrade[] Trades,
    int Decided, decimal SumNetBps, decimal? ClosedNetQuote, int LateEvents, ReplayScenario? Result, string Version, bool ExecutionEnabled = false,
    string? BotId = null, string? BotName = null, bool Paused = false, bool Stopping = false, string? EntriesBlocked = null, bool PositionOpen = false,
    OpenPosition? Position = null, bool Closing = false);
public sealed record PaperRunReport(string Strategy, string Version, string Id, DateTimeOffset EndedUtc, string State, string Explanation,
    string Coin, string SourceExchange, string Exchange, ReplaySettings Settings, int DelayMs, bool Counter, string? Leader,
    string? ExecutionExchange, decimal? NormalBasisBps, ReplayLeader[] Candidates, int LateEvents, ReplayScenario? Result);

// One live paper run: the recording session hands it every written event; Pump() releases them in receive-time order after a
// short hold-back, calibrates exactly as replay does, then drives LeaderLagEngine in virtual time. Nothing is sent to any exchange.
public sealed class PaperSession(string id, PaperRequest request, Func<long> clock, double holdBackSeconds = .5) : IRecordingObserver, IEngineObserver
{
    private readonly object gate = new(), outputGate = new();
    private readonly ConcurrentQueue<(CaptureEvent Item, int Line)> incoming = new();
    private readonly List<(CaptureEvent Item, int Line)> buffer = [];
    private readonly ReplaySettings settings = request.Settings;
    private readonly bool depth = request.Settings.ExecutionModel == "depth";
    private readonly bool makerDepth = request.Settings.ExecutionModel == "depth" && request.Settings.EntryOrder == "maker";
    private readonly RecordingEventParser parser = new(request.Coin, request.SourceExchange, request.Exchange, request.Settings.ExecutionModel == "depth");
    private readonly QuoteNormalizer normalizer = new(request.SourceExchange, request.Exchange);
    private readonly List<MarketAnalysis.Input> inputs = [];
    private readonly List<EngineEvent> quoteEvents = [];
    private readonly List<ReplayBookEvent> books = [];
    private readonly List<ReplayTradeEvent> trades = [];
    private readonly List<ReplayDecision> decisions = [];
    private readonly List<ReplayTrade> closed = [];
    private string state = "starting", explanation = "Подключаемся к публичным потокам бирж.";
    private string? directory, recorderStatus, recorderError, leaderName, executionName;
    private ReplayInstrument?[] instruments = [null, null];
    private long frequency = Stopwatch.Frequency;
    private long? first;
    private long lastTicks, lastReleased;
    private int late, leader, decided;
    private decimal? basis;
    private ReplayLeader[] candidates = [];
    private BlockingCollection<EngineEvent>? feed;
    private Task<ReplayScenario>? engine;
    private double lastClockMs = double.NegativeInfinity;
    private ReplayScenario? result;
    private volatile bool finished, paused, stopping, closeNow, lossHalted, open, dirty = true;
    private double closeFromMs = double.PositiveInfinity;
    private OpenPosition? position;
    private long added, consumed;
    // Virtual time at which a pause or stop was requested: it applies to signals from then on, not to ones already shown.
    private double pausedFromMs = double.PositiveInfinity, stoppingFromMs = double.PositiveInfinity;
    private bool complete, stopPending, stopIssued;
    private long stopRequestedAt;
    private decimal closedQuote;
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? endedUtc;
    public string Id => id;
    public string? BotId => request.BotId;
    public Action? StopRecording { get; set; }
    public bool Done { get { lock (gate) return complete; } }

    public void Started(string path)
    {
        lock (gate)
        {
            directory = path;
            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
                frequency = manifest.RootElement.GetProperty("stopwatchFrequency").GetInt64();
                instruments = [ReplayInstrument.Read(manifest.RootElement, request.SourceExchange, request.Coin, true),
                    ReplayInstrument.Read(manifest.RootElement, request.Exchange, request.Coin, false)];
                state = "calibrating";
                explanation = $"Первые {settings.CalibrationSeconds} с — выбор поводыря и обычной разницы цен. Сделок в это время нет.";
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Stop("failed", "Не удалось прочитать параметры записи; симуляция остановлена.");
            }
        }
        RequestStopIfNeeded();
    }

    public void Recorded(CaptureEvent item, long written, long bytes) => incoming.Enqueue((item, (int)written));

    public void Finished(RecordingReport report)
    {
        lock (gate) { recorderStatus ??= report.Status; recorderError ??= report.Failure; }
        finished = true;
    }

    public void Decision(ReplayDecision decision)
    {
        lock (outputGate) { decided++; decisions.Add(decision); if (decisions.Count > 200) decisions.RemoveAt(0); }
    }

    public void Trade(ReplayTrade trade)
    {
        lock (outputGate)
        {
            if (closed.Count < 10000) closed.Add(trade);
            // Closed results only, in the settlement currency: open positions are not marked to market here.
            closedQuote += depth && trade.NetQuote is { } cash ? cash : trade.NetBps / 10000 * settings.OrderNotional;
            if (request.LossLimit is { } limit && closedQuote <= -limit) lossHalted = true;
        }
        dirty = true;
    }

    public void Exposure(bool exposed) => open = exposed;

    public void Position(OpenPosition? view) { lock (outputGate) position = view; }

    public bool ExitNow(double ms) => closeNow && ms >= closeFromMs;

    public string? EntryBlock(double ms) =>
        stopping && ms >= stoppingFromMs ? "Остановка: новые входы не отправляются, текущая позиция закрывается по правилам стратегии."
        : lossHalted ? $"Достигнут лимит убытка {request.LossLimit:0.##}: новые входы запрещены до конца запуска."
        : paused && ms >= pausedFromMs ? "Пауза: новые входы не отправляются, текущая позиция ведётся по правилам стратегии." : null;

    private string? CurrentBlock() => EntryBlock(double.PositiveInfinity);

    private double NowMs() => first is null ? 0 : Ms(clock());

    public void Pause(bool value)
    {
        lock (gate) { paused = value; pausedFromMs = value ? NowMs() : double.PositiveInfinity; }
        dirty = true;
    }

    public void RequestStop(bool force) => RequestStop(force ? "force" : "wait");

    // Every stop blocks new entries; with nothing open the recording stops at once. "wait" lets the position close by the
    // strategy rules, "close" exits it now (virtually, through the book) and then stops, "force" stops the recording at once
    // and whatever is still open becomes an unresolved result. A stuck close falls back to force after a timeout.
    public void RequestStop(string mode)
    {
        lock (gate)
        {
            if (!stopping) { stoppingFromMs = NowMs(); stopping = true; stopRequestedAt = clock(); }
            if (mode == "close" && !closeNow) { closeFromMs = NowMs(); closeNow = true; stopRequestedAt = clock(); }
            if (mode == "force" || state != "running" || !open) stopPending = true;
        }
        dirty = true;
        RequestStopIfNeeded();
    }

    public bool TakeDirty() { var was = dirty; dirty = false; return was; }

    private double Ms(long ticks) => (ticks - first!.Value) * 1000.0 / frequency;

    // Called every few milliseconds by the service, and directly by tests. Returns true once the run is complete.
    public bool Pump()
    {
        try { return PumpOnce(); }
        finally { RequestStopIfNeeded(); }
    }

    // Stopping the recorder may run its callbacks inline, so it is never requested while this session holds its lock.
    private void RequestStopIfNeeded()
    {
        bool stop;
        // The recorder needs a moment to finish; the stop is requested once, not on every pump in between.
        lock (gate) { stop = stopPending && !stopIssued; stopPending = false; stopIssued |= stop; }
        if (stop) StopRecording?.Invoke();
    }

    private bool PumpOnce()
    {
        Task<ReplayScenario>? waitFor = null;
        lock (gate)
        {
            if (complete) return true;
            var ending = finished;
            if (directory is null)
            {
                if (ending) { state = "stopped"; explanation = recorderError ?? "Запись не началась; симуляция не запускалась."; complete = true; }
                return ending;
            }
            while (incoming.TryDequeue(out var item)) buffer.Add(item);
            buffer.Sort((a, b) => a.Item.ReceivedTicks != b.Item.ReceivedTicks ? a.Item.ReceivedTicks.CompareTo(b.Item.ReceivedTicks) : a.Line.CompareTo(b.Line));
            // Events wait this long so that ones written slightly out of receive order are still processed in order.
            var watermark = ending ? long.MaxValue : clock() - (long)(holdBackSeconds * frequency);
            var released = 0;
            while (released < buffer.Count && buffer[released].Item.ReceivedTicks <= watermark) Handle(buffer[released].Item, buffer[released++].Line);
            buffer.RemoveRange(0, released);
            if (first is not null)
            {
                var now = ending ? Ms(lastTicks) : Ms(watermark);
                if (state == "calibrating" && now >= settings.CalibrationSeconds * 1000) Calibrate();
                if (state == "running" && !ending && now > lastClockMs) { Feed(new(now, 0, null, false, 0, "", IsClock: true)); lastClockMs = now; }
            }
            if (stopping && !ending && !stopPending && state == "running"
                && (!open || clock() - stopRequestedAt > (long)(((closeNow ? 0 : settings.MaxHoldMs / 1000.0) + 15) * frequency)))
                stopPending = true;
            if (!ending || !incoming.IsEmpty) return false;
            if (state == "running")
            {
                // The session ends at its last received event, as replay ends at the last line of the file.
                Feed(new(Ms(lastTicks), 0, null, false, 0, "", IsClock: true)); feed!.CompleteAdding();
                waitFor = engine;
            }
            else if (state is "calibrating" or "starting")
            {
                state = "stopped"; explanation = "Запись остановлена до окончания выбора поводыря: сделки не моделировались.";
                Save(); complete = true; return true;
            }
            else { Save(); complete = true; return true; }
        }
        ReplayScenario? outcome = null; string? failure = null;
        try { outcome = waitFor!.GetAwaiter().GetResult(); }
        catch (Exception e) { failure = "Ошибка движка симуляции: " + e.Message; }
        lock (gate)
        {
            result = outcome;
            state = failure is not null || recorderStatus == "failed" ? "failed" : "finished";
            explanation = failure ?? (recorderStatus switch
            {
                "failed" => "Запись завершилась с ошибкой: " + recorderError + " Результат ниже неполный.",
                "completed-with-gaps" => "Симуляция завершена. В записи были разрывы данных: соответствующие заявки и позиции отмечены как неопределённые.",
                _ => "Симуляция завершена."
            }) + (late > 0 ? $" {late} событий пришли с опозданием больше {holdBackSeconds:0.#} с и обработаны не по порядку: возможна разница с проверкой этой записи." : "");
            Save(); complete = true;
        }
        return true;
    }

    private void Handle(CaptureEvent item, int line)
    {
        first ??= item.ReceivedTicks;
        if (item.ReceivedTicks < lastReleased) late++;
        lastReleased = Math.Max(lastReleased, item.ReceivedTicks); lastTicks = Math.Max(lastTicks, item.ReceivedTicks);
        var ms = Ms(item.ReceivedTicks);
        var parsed = parser.Parse(item, line);
        if (state == "calibrating")
        {
            if (parsed.Point is { } point) { inputs.Add(point); if (normalizer.Push(point, ms) is { } tick) quoteEvents.Add(tick); }
            if (parsed.Book is { } book) books.Add(book);
            if (parsed.Trade is { } trade) trades.Add(trade);
        }
        else if (state == "running")
        {
            if (parsed.Point is { } point && normalizer.Push(point, ms) is { } tick) Feed(tick);
            if (parsed.Book is { Venue: var venue } book && venue == 1 - leader) Feed(BookEvent(book));
            if (makerDepth && parsed.Trade is { } trade && trade.Venue == 1 - leader) Feed(TradeEvent(trade));
        }
    }

    private void Feed(EngineEvent tick) { feed!.Add(tick); Interlocked.Increment(ref added); }

    // Counts an event as consumed once the engine asks for the next one, i.e. after it has fully processed it.
    private IEnumerable<EngineEvent> Consume()
    {
        foreach (var tick in feed!.GetConsumingEnumerable()) { yield return tick; Interlocked.Increment(ref consumed); }
    }

    // Waits until the engine has processed everything fed so far; lets tests read state deterministically.
    public void WaitForEngine()
    {
        SpinWait.SpinUntil(() => Interlocked.Read(ref consumed) >= Interlocked.Read(ref added) || engine is not { IsCompleted: false }, 5000);
    }

    private EngineEvent BookEvent(ReplayBookEvent x) => new(Ms(x.Ticks), x.Venue, null, false, x.Sequence, x.Connection, true, x.Update);
    private EngineEvent TradeEvent(ReplayTradeEvent x) => new(Ms(x.Ticks), x.Venue, null, false, x.Sequence, x.Connection, IsTrade: true, Prints: x.Prints);

    // Same steps and thresholds as StrategyReplay.Run, on the data received during the calibration window.
    private void Calibrate()
    {
        var cutoff = first!.Value + (long)settings.CalibrationSeconds * frequency;
        var window = inputs.Where(x => x.Ticks < cutoff).ToList();
        var info = new AnalysisRecording(id, request.Coin, request.SourceExchange, request.Exchange, DateTimeOffset.UtcNow, "completed", 0, false);
        var analysis = MarketAnalysis.Analyze(info, new(settings.ImpulseBps, settings.WindowMs, 100, 100, settings.MaxAgeMs, settings.FeeBps,
            settings.SlippageBps), frequency, first.Value, cutoff, window.Count, 0, window, CancellationToken.None);
        candidates = analysis.Directions.Select(d => new ReplayLeader(d.SourceExchange, d.Exchange, d.Horizons[0].Samples, d.Horizons[0].MeanMoveBps)).ToArray();
        if (StrategyReplay.SelectLeader(candidates, settings) is not { } best)
        { Stop("no-leader", "Поводырь не определён: нужны минимум 10 наблюдений в каждом направлении, положительная реакция и достаточное преимущество. Сделки не моделировались."); return; }
        leader = best.SourceExchange == request.SourceExchange ? 0 : 1;
        if (request.RequireSourceLeader && leader != 0)
        { Stop("no-leader", $"Поводырём оказалась биржа бота ({best.SourceExchange}): на ней нечего догонять. Сделки не моделировались."); return; }
        var instrument = depth ? instruments[1 - leader] : null;
        if (depth && (instrument is null || books.All(x => x.Venue != 1 - leader || x.Update is null)))
        { Stop("no-execution-data", "Для отстающей биржи нет правил размера заявки или глубины стакана за время калибровки. Выберите упрощённую модель BBO или другую пару."); return; }
        // Replay checks the whole file for trades; live cannot see ahead, so it only requires the trade stream to be connected.
        if (makerDepth && !trades.Any(x => x.Venue == 1 - leader))
        { Stop("no-execution-data", "Для maker-входа по стакану нужен поток сделок отстающей биржи, а он не подключился за время калибровки."); return; }
        if (StrategyReplay.NormalBasis(quoteEvents, leader, settings, CancellationToken.None) is not { } normal)
        { Stop("no-basis", "Недостаточно совместных свежих котировок для оценки обычной разницы цен."); return; }
        basis = normal; leaderName = best.SourceExchange; executionName = best.Exchange;
        var initial = quoteEvents
            .Concat(depth ? books.Where(x => x.Venue == 1 - leader).Select(BookEvent) : [])
            .Concat(makerDepth ? trades.Where(x => x.Venue == 1 - leader).Select(TradeEvent) : [])
            .OrderBy(x => x.Ms).ThenBy(x => x.Sequence).ToList();
        feed = new();
        foreach (var tick in initial) Feed(tick);
        lastClockMs = initial.Count == 0 ? double.NegativeInfinity : initial[^1].Ms;
        var setup = new EngineSetup(leader, normal, settings, request.DelayMs, request.Counter, instrument, request.Budget);
        engine = Task.Factory.StartNew(() => LeaderLagEngine.Run(Consume(), setup, CancellationToken.None, null, this),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        inputs.Clear(); quoteEvents.Clear(); books.Clear(); trades.Clear();
        state = "running"; dirty = true;
        explanation = $"Поводырь: {best.SourceExchange}, виртуальное исполнение на {best.Exchange}. Решения принимаются по живым данным; реальные заявки не отправляются.";
    }

    private void Stop(string status, string reason) { state = status; explanation = reason; stopPending = true; dirty = true; }

    // Called once, when the run completes.
    private void Save()
    {
        endedUtc = DateTimeOffset.UtcNow; dirty = true;
        if (directory is null) return;
        var report = new PaperRunReport("leader-lag", StrategyReplay.Version, id, DateTimeOffset.UtcNow, state, explanation, request.Coin,
            request.SourceExchange, request.Exchange, settings, request.DelayMs, request.Counter, leaderName, executionName, basis, candidates, late, result);
        try { File.WriteAllText(Path.Combine(directory, "paper-run-v1.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { explanation += " Не удалось сохранить отчёт симуляции рядом с записью."; }
    }

    // Same normalisation as the replay report: per configured notional in the depth model, per trade otherwise.
    private decimal ClosedSumBps() => result?.SumNetBps ?? closed.Sum(t => depth && t.NetQuote is { } cash
        ? cash / (settings.OrderQuantity is null ? settings.OrderNotional : (t.Quantity ?? 0) * t.EntryPrice) * 10000 : t.NetBps);
    private decimal? ClosedNetQuote() => depth ? result?.ClosedNetQuote ?? closed.Sum(t => t.NetQuote ?? 0) : null;

    public PaperSnapshot Snapshot()
    {
        lock (gate) lock (outputGate)
            return new(id, state, explanation, request.Coin, request.SourceExchange, request.Exchange, settings, request.DelayMs, request.Counter,
                directory, first is null ? 0 : Math.Max(0, Ms(lastTicks) / 1000), leaderName, executionName, basis, candidates,
                decisions.AsEnumerable().Reverse().Take(50).ToArray(), closed.TakeLast(100).Reverse().ToArray(), decided,
                ClosedSumBps(), ClosedNetQuote(), late, result, StrategyReplay.Version, false, request.BotId, request.BotName,
                paused, stopping, complete ? null : CurrentBlock(), open, complete ? null : position, closeNow && !complete);
    }

    public PaperRunRecord Record()
    {
        lock (gate) lock (outputGate)
            return new(id, request.BotId, request.BotName, StrategyReplay.Version, startedUtc, endedUtc, state, explanation, request.Coin,
                request.SourceExchange, request.Exchange, settings, request.DelayMs, request.Counter, leaderName, executionName, directory,
                result?.Trades ?? closed.Count, ClosedSumBps(), ClosedNetQuote(), result?.UnresolvedPositions ?? 0, late,
                complete ? null : CurrentBlock(), result?.TradeLog ?? closed.Take(500).ToArray(), decisions.ToArray());
    }

    // What a bot card shows while this session runs.
    public string BotStatus()
    {
        lock (gate)
            return complete ? "stopped" : state is "starting" or "calibrating" ? "calibrating" : stopping ? "stopping" : paused ? "paused" : "running";
    }
}

public sealed record PaperStart(int Seconds = 1800);

// Runs at most one live paper session, on top of the market recorder (which also allows one recording at a time).
public sealed class PaperTrading(MarketResearch research, PaperRunStore runs, ILogger<PaperTrading> logger) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private PaperSession? current;

    public PaperSnapshot Snapshot()
    {
        lock (gate) return current?.Snapshot() ?? new(null, "idle", "Симуляция не запущена.", null, null, null, null, 0, false, null, 0, null, null,
            null, [], [], [], 0, 0, null, 0, null, StrategyReplay.Version);
    }

    // A leader-lag bot trades on its own exchange; the signal exchange must turn out to be the leader.
    public string StartBot(Bot bot, PaperStart options)
    {
        if (bot.Strategy != "leader-lag" || bot.Parameters is not { } p)
            throw new InvalidOperationException("Для этой стратегии запуск недоступен: алгоритм ещё не реализован.");
        return Start(new(bot.Symbol[..^4], p.SignalExchange.ToLowerInvariant(), bot.Exchange.ToLowerInvariant(), options.Seconds, p.Settings,
            p.DelayMs, p.Counter, BotId: bot.Id, BotName: bot.Name, LossLimit: bot.LossLimit, RequireSourceLeader: true, Budget: bot.Budget));
    }

    public string Start(PaperRequest request)
    {
        if (request.Settings is null) throw new ArgumentException("Укажите настройки стратегии.");
        request.Settings.Validate();
        if (request.DelayMs is < 1 or > 1000) throw new ArgumentException("Задержка заявки — от 1 до 1000 мс.");
        if (request.Seconds < request.Settings.CalibrationSeconds + 60 || request.Seconds > 3600)
            throw new ArgumentException($"Длительность — от {request.Settings.CalibrationSeconds + 60} до 3600 с: сначала идёт калибровка {request.Settings.CalibrationSeconds} с.");
        lock (gate)
        {
            if (current is { Done: false }) throw new InvalidOperationException("Симуляция уже идёт. Остановите её перед новым запуском.");
            var session = new PaperSession(Guid.NewGuid().ToString(), request, Stopwatch.GetTimestamp);
            var recording = research.Start(new ResearchRequest(request.Coin, request.Seconds, request.MaxMb, request.Exchange, request.SourceExchange), session);
            session.StopRecording = () => research.Stop(recording);
            current = session;
            _ = Task.Run(() => RunAsync(session));
            return session.Id;
        }
    }

    // Pumps the session and keeps its database record current: on every change and at least every 5 seconds.
    private async Task RunAsync(PaperSession session)
    {
        var saved = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                var done = session.Pump();
                if (session.TakeDirty() || done || Stopwatch.GetElapsedTime(saved) > TimeSpan.FromSeconds(5))
                {
                    try { await runs.SaveAsync(session.Record(), CancellationToken.None); }
                    catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException) { logger.LogWarning(e, "Paper run was not saved"); }
                    saved = Stopwatch.GetTimestamp();
                }
                if (done) return;
                await Task.Delay(10, shutdown.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { logger.LogError(e, "Paper session failed"); }
    }

    private PaperSession? Find(string id) { lock (gate) return current?.Id == id ? current : null; }
    public PaperSession? ForBot(string botId) { lock (gate) return current is { Done: false } s && s.BotId == botId ? s : null; }

    public bool Pause(string id, bool paused)
    {
        if (Find(id) is not { Done: false } session) return false;
        session.Pause(paused); return true;
    }

    public bool Stop(string id, string mode = "wait")
    {
        if (mode is not ("wait" or "close" or "force")) throw new ArgumentException("Режим остановки: wait, close или force.");
        if (Find(id) is not { } session) return false;
        session.RequestStop(mode); return true;
    }

    public string StatusFor(string botId, PaperRunRecord? latest) =>
        ForBot(botId)?.BotStatus() ?? (latest is null ? "draft" : latest.State == "interrupted" ? "interrupted" : "stopped");

    public void Dispose() { shutdown.Cancel(); shutdown.Dispose(); }
}