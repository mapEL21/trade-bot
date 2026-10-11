using System.Diagnostics;
using System.Globalization;
using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record ResearchRequest(string Coin, int Seconds, int MaxMb, string Exchange = "binance", string SourceExchange = "hyperliquid");
public sealed record QuoteView(string Bid, string Ask, string BidSize, string AskSize, string Mid,
    decimal SpreadBps, DateTimeOffset ReceivedUtc, double AgeMs, bool Stale);
public sealed record FeedView(string Id, string Status, long Messages, int Gaps,
    DateTimeOffset? LastEventUtc, double? EventAgeMs, QuoteView? Quote);
public sealed record ResearchEvent(DateTimeOffset At, string Source, string Text);
public sealed record ResearchSnapshot(string? Id, string State, bool Active, string? Coin, string? Symbol,
    int Seconds, double ElapsedSeconds, long Written, long Bytes, int Gaps, string? Directory,
    string? Error, decimal? RawDifferencePercent, FeedView[] Feeds, ResearchEvent[] Events, bool ExecutionEnabled = false)
{
    public int MaxMb { get; init; }
    public string Exchange { get; init; } = "binance";
    public string SourceExchange { get; init; } = "hyperliquid";
    public string? SourceSymbol { get; init; }
}

// An optional tap (the live paper session) sees the same callbacks after this session's own projection, outside its lock.
public sealed class ResearchSession(string id, RecorderOptions options, IRecordingObserver? tap = null) : IRecordingObserver, IDisposable
{
    private sealed class FeedState
    {
        public string Status = "idle";
        public string? Connection;
        public long Messages;
        public int Gaps;
        public long? LastTicks;
        public DateTimeOffset? LastEventUtc;
        public MarketQuote? Quote;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, FeedState> feeds = options.Feeds.ToDictionary(x => x.Name, _ => new FeedState());
    private readonly List<ResearchEvent> events = [];
    private readonly CancellationTokenSource stop = new();
    private string state = "starting";
    private long? startedTicks;
    private long? endedTicks;
    private long written, bytes;
    private string? directory, error;
    public string Id => id;
    public CancellationToken Token => stop.Token;

    public void Started(string path)
    {
        lock (gate)
        {
            directory = path;
            startedTicks = Stopwatch.GetTimestamp();
            if (state != "stopping") state = "recording";
            Add("recorder", "Запись публичных данных началась.");
        }
        tap?.Started(path);
    }

    public void Recorded(CaptureEvent item, long totalWritten, long totalBytes)
    {
        Project(item, totalWritten, totalBytes);
        tap?.Recorded(item, totalWritten, totalBytes);
    }

    private void Project(CaptureEvent item, long totalWritten, long totalBytes)
    {
        // Projection runs in the file writer, outside WebSocket ingestion.
        lock (gate)
        {
            written = totalWritten;
            bytes = totalBytes;
            var feed = feeds[item.Source];
            if (item.Kind is "connecting" or "connected" or "gap")
            {
                feed.Quote = null;
                feed.LastTicks = null;
                feed.LastEventUtc = null;
                feed.Connection = item.Connection;
                feed.Status = item.Kind == "gap" ? "disconnected" : item.Kind;
                if (item.Kind == "gap") feed.Gaps++;
                Add(item.Source, item.Kind switch
                {
                    "connected" => "Соединение установлено; ожидаем рыночные данные.",
                    "gap" => "Разрыв данных. Регистратор восстанавливает соединение.",
                    _ => "Подключение к публичному потоку."
                });
            }
            if (item.Kind == "invalid")
            {
                feed.Quote = null;
                feed.Status = "invalid";
                Add(item.Source, "Получено сообщение неизвестного формата.");
            }
            if (item.Kind != "market" || feed.Connection != item.Connection) return;
            feed.Messages++;
            feed.Status = "live";
            feed.LastTicks = item.ReceivedTicks;
            feed.LastEventUtc = item.ReceivedUtc;
            if (MarketQuote.TryRead(item, out var quote))
            {
                if (quote is null) feed.Quote = null;
                else if (feed.Quote is null || (quote.SourceOrder >= feed.Quote.SourceOrder && quote.ReceivedTicks >= feed.Quote.ReceivedTicks))
                    feed.Quote = quote;
            }
        }
    }

    public void Finished(RecordingReport report)
    {
        lock (gate)
        {
            state = report.Status;
            written = report.Written;
            bytes = report.Bytes;
            error = report.Failure;
            endedTicks = Stopwatch.GetTimestamp();
            Add("recorder", report.Status == "failed" ? "Запись завершилась с ошибкой." : "Приём остановлен, очередь дописана в файл.");
        }
        tap?.Finished(report);
    }

    public void Fail(string message)
    {
        lock (gate)
        {
            state = "failed";
            error = message;
            endedTicks = Stopwatch.GetTimestamp();
            Add("recorder", message);
        }
        tap?.Finished(new(DateTimeOffset.UtcNow, "failed", message, 0, written, bytes, [], new { }));
    }

    public void EndCancelled()
    {
        lock (gate)
        {
            if (state is not ("starting" or "recording" or "stopping")) return;
            state = "stopped";
            endedTicks = Stopwatch.GetTimestamp();
            Add("recorder", "Сессия остановлена до начала записи.");
        }
        tap?.Finished(new(DateTimeOffset.UtcNow, "stopped", null, 0, 0, 0, [], new { }));
    }

    public void RequestStop()
    {
        lock (gate)
        {
            if (state is not ("starting" or "recording" or "stopping")) return;
            state = "stopping";
            stop.Cancel();
        }
    }

    public ResearchSnapshot Snapshot()
    {
        lock (gate)
        {
            var now = endedTicks ?? Stopwatch.GetTimestamp();
            var active = state is "starting" or "recording" or "stopping";
            double Age(long ticks) => Math.Max(0, Stopwatch.GetElapsedTime(ticks, now).TotalMilliseconds);
            string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
            var views = feeds.Select(pair =>
            {
                var f = pair.Value;
                var q = f.Quote;
                var age = f.LastTicks is { } ticks ? (double?)Age(ticks) : null;
                var status = !active ? "stopped" : f.Status == "live" && age > 3000 ? "stale" : f.Status;
                var quote = q is null ? null : new QuoteView(Text(q.Bid), Text(q.Ask), Text(q.BidSize), Text(q.AskSize), Text(q.Mid),
                    q.SpreadBps, q.ReceivedUtc, Age(q.ReceivedTicks), state != "recording" || status != "live" || Age(q.ReceivedTicks) > 3000);
                return new FeedView(pair.Key, status, f.Messages, f.Gaps, f.LastEventUtc, age, quote);
            }).ToArray();
            var hl = views.First(x => x.Id == RecorderOptions.QuoteFeedId(options.SourceExchange)).Quote;
            var bn = views.First(x => x.Id == RecorderOptions.QuoteFeedId(options.Exchange)).Quote;
            decimal? difference = null;
            if (hl is { Stale: false } && bn is { Stale: false })
            {
                try { difference = (decimal.Parse(hl.Mid, CultureInfo.InvariantCulture) / decimal.Parse(bn.Mid, CultureInfo.InvariantCulture) - 1) * 100; }
                catch (OverflowException) { /* Unrepresentable comparison is unavailable, never a signal. */ }
            }
            return new(id, state, active, options.Coin, options.Symbol, options.Seconds,
                startedTicks is { } start ? Math.Max(0, Stopwatch.GetElapsedTime(start, endedTicks ?? now).TotalSeconds) : 0,
                written, bytes, views.Sum(x => x.Gaps), directory, error, difference, views, events.ToArray())
            { MaxMb = (int)(options.MaxBytes / 1048576), Exchange = options.Exchange, SourceExchange = options.SourceExchange, SourceSymbol = options.SourceSymbol };
        }
    }

    private void Add(string source, string text)
    {
        events.Insert(0, new(DateTimeOffset.UtcNow, source, text));
        if (events.Count > 12) events.RemoveAt(events.Count - 1);
    }

    public void Dispose() => stop.Dispose();
}

// The research page drives one manual recording at a time. Live paper sessions each get their own background recording
// (with the session as a tap), so several simulations and a manual recording can run side by side.
public sealed class MarketResearch(IRecordingRunner runner, IConfiguration configuration, IHostEnvironment environment,
    ILogger<MarketResearch> logger) : IHostedService, IDisposable
{
    private readonly object gate = new();
    private ResearchSession? current;
    private Task? task;
    private readonly Dictionary<string, (ResearchSession Session, Task Task)> background = [];
    private bool shuttingDown;

    public ResearchSnapshot Snapshot()
    {
        lock (gate) return current?.Snapshot() ?? new(null, "idle", false, null, null, 0, 0, 0, 0, 0, null, null, null, [], []);
    }

    public string Start(ResearchRequest request, IRecordingObserver? tap = null)
    {
        var root = configuration["Market:Path"] ?? Path.Combine(environment.ContentRootPath, "data", "market");
        var options = RecorderOptions.Parse(["--coin", request.Coin ?? "", "--symbol", MarketCatalog.Symbol(request.Exchange, request.Coin ?? ""), "--exchange", request.Exchange, "--source-exchange", request.SourceExchange,
            "--seconds", request.Seconds.ToString(CultureInfo.InvariantCulture), "--max-mb", request.MaxMb.ToString(CultureInfo.InvariantCulture), "--output", root]);
        lock (gate)
        {
            if (shuttingDown) throw new InvalidOperationException("Сервер останавливается. Новые записи не начинаются.");
            if (tap is null)
            {
                if (task is { IsCompleted: false }) throw new InvalidOperationException("Уже идёт запись или её остановка. Обновите состояние.");
                current?.Dispose();
                current = new ResearchSession(Guid.NewGuid().ToString(), options, null);
                task = Run(current, options);
                return current.Id;
            }
            foreach (var done in background.Where(x => x.Value.Task.IsCompleted).ToList()) { done.Value.Session.Dispose(); background.Remove(done.Key); }
            var session = new ResearchSession(Guid.NewGuid().ToString(), options, tap);
            background[session.Id] = (session, Run(session, options));
            return session.Id;
        }
    }

    private Task Run(ResearchSession session, RecorderOptions options) => Task.Run(async () =>
    {
        try { await runner.RunAsync(options, session, session.Token); }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { session.EndCancelled(); }
        catch (Exception e)
        {
            logger.LogWarning(e, "Market recording failed");
            session.Fail(e is ArgumentException ? e.Message : "Не удалось выполнить запись. Проверьте доступ к площадкам и место на диске. Подробности — в журнале сервера.");
        }
    });

    public bool Stop(string id)
    {
        lock (gate)
        {
            var session = current?.Id == id ? current : background.TryGetValue(id, out var entry) ? entry.Session : null;
            if (session is null) return false;
            session.RequestStop();
            return true;
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken ct)
    {
        List<Task> running = [];
        lock (gate)
        {
            shuttingDown = true;
            current?.RequestStop();
            if (task is not null) running.Add(task);
            foreach (var (session, recording) in background.Values) { session.RequestStop(); running.Add(recording); }
        }
        await Task.WhenAll(running).WaitAsync(ct);
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (task is null || task.IsCompleted) current?.Dispose();
            foreach (var (session, recording) in background.Values) if (recording.IsCompleted) session.Dispose();
        }
    }
}
