using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// About 1 MB a minute per coin: 20 coins for an hour fit well within 4 GB.
public sealed record ScanStart(int Seconds = 3600, decimal MinQuoteVolume = 150_000_000, int MaxSymbols = 20, string[]? Exclude = null,
    int MaxMb = 4096);
public sealed record ScanSnapshot(string? Id, string State, string Explanation, DateTimeOffset? StartedUtc, int Seconds, string? Directory,
    long Events, long Bytes, int Ignored, ScanSymbolView[] Symbols, ScanCandidate[] Candidates, ScanSettings Settings, bool ExecutionEnabled = false);
public sealed record ScanReport(int Version, string Id, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, string State, ScanSettings Settings,
    ScanSymbolView[] Symbols, ScanCandidate[] Candidates);

// One scan session: the recorder hands it every written event in file order and the level scanner turns them into candidates.
public sealed class ScanSession(string id, ScanStart request, ScanSettings settings) : IRecordingObserver, IDisposable
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
                scanner?.Candidates.Reverse().Take(300).ToArray() ?? [], settings);
    }

    public ScanSymbolDetail? Detail(string symbol) { lock (gate) return scanner?.Detail(symbol); }
    public void Dispose() => stop.Dispose();
}

// The level scanner on the research side: one scan at a time, public data only, no orders.
public sealed class MarketScanner(IScanRunner runner, IConfiguration configuration, IHostEnvironment environment, ILogger<MarketScanner> logger)
    : IHostedService, IDisposable
{
    private readonly object gate = new();
    private ScanSession? current;
    private Task? task;
    private bool shuttingDown;

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
            current?.Dispose();
            var session = new ScanSession(Guid.NewGuid().ToString(), request, new ScanSettings());
            current = session;
            task = Task.Run(async () =>
            {
                try { await runner.RunAsync(options, session, session.Token); }
                catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Scan failed");
                    session.Fail(e is ArgumentException ? e.Message : "Не удалось запустить сканер. Проверьте доступ к Binance. Подробности — в журнале сервера.");
                }
            });
            return session.Id;
        }
    }

    public bool Stop(string id)
    {
        lock (gate)
        {
            if (current?.Id != id) return false;
            current.RequestStop();
            return true;
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken ct)
    {
        Task? running;
        lock (gate) { shuttingDown = true; current?.RequestStop(); running = task; }
        if (running is not null) await running.WaitAsync(ct);
    }
    public void Dispose() { lock (gate) { if (task is null || task.IsCompleted) current?.Dispose(); } }
}
