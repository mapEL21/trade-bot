using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// About 1 MB a minute per coin: 20 coins for an hour fit well within 4 GB.
// Continuous: when a recording ends on time, the next one starts at once with a fresh choice of coins, until stopped.
public sealed record ScanStart(int Seconds = 3600, decimal MinQuoteVolume = 150_000_000, int MaxSymbols = 20, string[]? Exclude = null,
    int MaxMb = 4096, bool Continuous = false);
// What the recording shows while it runs: per coin, how many trades and liquidations have arrived.
public sealed record RecordingSymbol(string Symbol, decimal QuoteVolume, long Trades, long Liquidations);
public sealed record ScanSnapshot(string? Id, string State, string Explanation, DateTimeOffset? StartedUtc, int Seconds, string? Directory,
    long Events, long Bytes, RecordingSymbol[] Symbols, bool ExecutionEnabled = false, bool Continuous = false, int Segment = 1);

// One recording session of the data recorder: it writes the market of the chosen coins for the signal lab and counts what
// arrives per coin. It no longer searches for breakouts live; the lab studies breakouts with the other events on the files.
public sealed class ScanSession(string id, ScanStart request, int segment = 1) : IRecordingObserver, IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, (decimal Volume, long Trades, long Liquidations)> symbols = new(StringComparer.Ordinal);
    private string state = "starting", explanation = "Загружаем список монет и историю свечей Binance.";
    private string? directory;
    private long events, bytes;
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
            foreach (var x in manifest.RootElement.GetProperty("symbols").EnumerateArray())
                symbols[x.GetProperty("Symbol").GetString()!] = (x.GetProperty("QuoteVolume").GetDecimal(), 0, 0);
            state = "scanning";
            explanation = $"Пишем рынок {symbols.Count} монет: сделки, стакан, ликвидации. Сделок нет — это данные для лаборатории сигналов.";
        }
    }

    // Counting only reads the stream name at the start of the message ("xxxusdt@aggTrade"); the payload is not parsed.
    public void Recorded(CaptureEvent item, long written, long totalBytes)
    {
        lock (gate)
        {
            events = written; bytes = totalBytes;
            if (item.Kind != "market") return;
            const string prefix = "{\"stream\":\"";
            if (!item.Payload.StartsWith(prefix, StringComparison.Ordinal)) return;
            var end = item.Payload.IndexOf('"', prefix.Length);
            var at = item.Payload.IndexOf('@', prefix.Length);
            if (end < 0 || at < 0 || at > end) return;
            var symbol = item.Payload[prefix.Length..at].ToUpperInvariant();
            var stream = item.Payload[(at + 1)..end];
            if (!symbols.TryGetValue(symbol, out var counts)) return;
            symbols[symbol] = stream == "aggTrade" ? counts with { Trades = counts.Trades + 1 }
                : stream == "forceOrder" ? counts with { Liquidations = counts.Liquidations + 1 } : counts;
        }
    }

    public void Finished(RecordingReport report)
    {
        lock (gate)
        {
            var stopped = stop.IsCancellationRequested;
            state = report.Status == "failed" ? "failed" : report.Status == "no-data" ? "no-data" : stopped ? "stopped" : "finished";
            explanation = report.Failure is { } failure ? $"Запись завершилась с ошибкой: {failure}" : report.Status == "no-data"
                ? "Биржа не прислала данных по одному из потоков." : "Запись завершена; её можно обработать в лаборатории сигналов.";
        }
    }

    public void Fail(string message) { lock (gate) { state = "failed"; explanation = message; } }
    public void RequestStop() => stop.Cancel();

    public ScanSnapshot Snapshot()
    {
        lock (gate)
            return new(id, state, explanation, startedUtc, request.Seconds, directory, events, bytes,
                symbols.Select(x => new RecordingSymbol(x.Key, x.Value.Volume, x.Value.Trades, x.Value.Liquidations))
                    .OrderByDescending(x => x.QuoteVolume).ToArray(), false, request.Continuous, segment);
    }

    public void Dispose() => stop.Dispose();
}

// The data recorder for the signal lab: one recording at a time, public data only, no orders. A continuous run chains
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
        lock (gate) return current?.Snapshot() ?? new(null, "idle", "Запись не идёт.", null, 0, null, 0, 0, []);
    }

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
        return current = new ScanSession(Guid.NewGuid().ToString(), request, segment);
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
