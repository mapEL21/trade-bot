using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// About 1 MB a minute per coin: 20 coins for an hour fit well within 4 GB.
// Continuous: when a recording ends on time, the next one starts at once with a fresh choice of coins, until stopped.
public sealed record ScanStart(int Seconds = 3600, decimal MinQuoteVolume = 150_000_000, int MaxSymbols = 20, string[]? Exclude = null,
    int MaxMb = 4096, bool Continuous = false);
public sealed record ScanSnapshot(string? Id, string State, string Explanation, DateTimeOffset? StartedUtc, int Seconds, string? Directory,
    long Events, long Bytes, int Ignored, ScanSymbolView[] Symbols, ScanCandidate[] Candidates, ScanSettings Settings, bool ExecutionEnabled = false,
    bool Continuous = false, int Segment = 1);
public sealed record ScanReport(int Version, string Id, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, string State, ScanSettings Settings,
    ScanSymbolView[] Symbols, ScanCandidate[] Candidates);

// One scan session: the recorder hands it every written event in file order and the level scanner turns them into candidates.
public sealed class ScanSession(string id, ScanStart request, ScanSettings settings, int segment = 1) : IRecordingObserver, IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private LevelScanner? scanner;
    private string state = "starting", explanation = "Загружаем список монет и историю свечей Binance.";
    private string? directory;
    private long events, bytes;
    private int ignored;
    public string Id => id;
    public CancellationToken Token => stop.Token;
    public bool StopRequested => stop.IsCancellationRequested;
    public bool Failed { get { lock (gate) return state is "failed" or "no-data"; } }
    public bool Done { get { lock (gate) return state is "finished" or "stopped" or "failed" or "no-data"; } }

    public void Started(string path)
    {
        lock (gate)
        {
            directory = path;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
            var volumes = manifest.RootElement.GetProperty("symbols").EnumerateArray()
                .ToDictionary(x => x.GetProperty("Symbol").GetString()!, x => x.GetProperty("QuoteVolume").GetDecimal(), StringComparer.Ordinal);
            scanner = new LevelScanner(settings, volumes);
            state = "scanning";
            explanation = $"Сканер следит за {volumes.Count} монетами. Уровни строятся по минутным свечам, каждый пробой оценивается и отслеживается {settings.OutcomeMinutes} минут. Сделок нет.";
        }
    }

    public void Recorded(CaptureEvent item, long written, long totalBytes)
    {
        lock (gate)
        {
            events = written; bytes = totalBytes;
            // A message the scanner cannot read is counted, not hidden; it never stops the recording.
            try { scanner?.Process(item); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
            { ignored++; }
        }
    }

    public void Finished(RecordingReport report)
    {
        lock (gate)
        {
            var stopped = stop.IsCancellationRequested;
            state = report.Status == "failed" ? "failed" : report.Status == "no-data" ? "no-data" : stopped ? "stopped" : "finished";
            explanation = report.Failure is { } failure ? $"Запись завершилась с ошибкой: {failure}" : report.Status == "no-data"
                ? "Биржа не прислала данных по одному из потоков." : $"Сканирование завершено. Кандидатов: {scanner?.Candidates.Count ?? 0}.";
            if (directory is not null && scanner is not null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(directory, "scan-candidates-v1.json"), JsonSerializer.Serialize(new ScanReport(1, id, startedUtc,
                        DateTimeOffset.UtcNow, state, settings, scanner.Symbols(), scanner.Candidates.ToArray()), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                }
                catch (IOException) { explanation += " Журнал кандидатов не сохранён на диск."; }
            }
        }
    }

    public void Fail(string message) { lock (gate) { state = "failed"; explanation = message; } }
    public void RequestStop() => stop.Cancel();

    public ScanSnapshot Snapshot()
    {
        lock (gate)
            return new(id, state, explanation, startedUtc, request.Seconds, directory, events, bytes, ignored, scanner?.Symbols() ?? [],
                scanner?.Candidates.Reverse().Take(300).ToArray() ?? [], settings, false, request.Continuous, segment);
    }

    public ScanSymbolDetail? Detail(string symbol) { lock (gate) return scanner?.Detail(symbol); }
    public void Dispose() => stop.Dispose();
}

// The level scanner on the research side: one scan at a time, public data only, no orders. A continuous scan chains
// one-hour recordings; Scanner:AutoStart starts one with the server, for a machine that runs around the clock.
public sealed class MarketScanner(IScanRunner runner, IConfiguration configuration, IHostEnvironment environment, ILogger<MarketScanner> logger)
    : IHostedService, IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private ScanSession? current;
    private Task? task;
    private bool shuttingDown, stopLoop;
    // Cancelled by a stop request, so a pause before retrying a failed segment ends at once.
    private CancellationTokenSource loopStop = new();

    public ScanSnapshot Snapshot()
    {
        lock (gate) return current?.Snapshot() ?? new(null, "idle", "Сканер не запущен.", null, 0, null, 0, 0, 0, [], [], new());
    }

    public ScanSymbolDetail? Detail(string symbol) { lock (gate) return current?.Detail(symbol); }

    public string Start(ScanStart request)
    {
        var root = configuration["Scanner:Path"] ?? Path.Combine(environment.ContentRootPath, "data", "scans");
        var options = new ScanOptions(root, request.Seconds, request.MaxMb * 1048576L, request.MinQuoteVolume, request.MaxSymbols, request.Exclude);
        options.Validate();
        lock (gate)
        {
            if (shuttingDown || task is { IsCompleted: false }) throw new InvalidOperationException("Сканер уже работает. Остановите его перед новым запуском.");
            stopLoop = false;
            loopStop.Dispose(); loopStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            var first = Next(request, 1);
            task = Task.Run(() => Loop(request, options, first));
            return first.Id;
        }
    }

    private ScanSession Next(ScanStart request, int segment)
    {
        current?.Dispose();
        return current = new ScanSession(Guid.NewGuid().ToString(), request, new ScanSettings(), segment);
    }

    private async Task Loop(ScanStart request, ScanOptions options, ScanSession session)
    {
        for (var segment = 1; ; segment++)
        {
            try { await runner.RunAsync(options, session, session.Token); }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
            catch (Exception e)
            {
                logger.LogWarning(e, "Scan failed");
                session.Fail(e is ArgumentException ? e.Message : "Не удалось запустить сканер. Проверьте доступ к Binance. Подробности — в журнале сервера.");
            }
            if (!request.Continuous || session.StopRequested) return;
            // A failure in continuous mode (network, exchange) is retried after a minute instead of ending the scan.
            if (session.Failed)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(1), loopStop.Token); }
                catch (OperationCanceledException) { return; }
            }
            lock (gate)
            {
                if (shuttingDown || stopLoop) return;
                session = Next(request, segment + 1);
            }
        }
    }

    public bool Stop(string id)
    {
        lock (gate)
        {
            if (current?.Id != id) return false;
            stopLoop = true;
            loopStop.Cancel();
            current.RequestStop();
            return true;
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (configuration.GetValue<bool>("Scanner:AutoStart"))
        {
            try
            {
                Start(new ScanStart(3600, configuration.GetValue<decimal?>("Scanner:MinQuoteVolume") ?? 150_000_000,
                    configuration.GetValue<int?>("Scanner:MaxSymbols") ?? 20,
                    (configuration["Scanner:Exclude"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    Continuous: true));
                logger.LogInformation("Continuous scan started with the server");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { logger.LogWarning(e, "Continuous scan was not started"); }
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task? running;
        lock (gate) { shuttingDown = true; stopLoop = true; current?.RequestStop(); running = task; }
        shutdown.Cancel();
        if (running is not null) await running.WaitAsync(ct);
    }
    public void Dispose()
    {
        lock (gate) { if (task is null || task.IsCompleted) current?.Dispose(); }
        loopStop.Dispose();
        shutdown.Dispose();
    }
}
