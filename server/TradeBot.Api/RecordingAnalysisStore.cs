using System.Text.Json;
using System.Text.RegularExpressions;
using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record RecordingList(AnalysisRecording[] Items, int Unavailable);
internal sealed record RecordingData(AnalysisRecording Recording, long Frequency, long First, long Last, int Events, int Gaps, List<MarketAnalysis.Input> Points, ReplayMarketData? Market);

public sealed class RecordingAnalysisStore(IConfiguration configuration, IHostEnvironment environment) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private string Root => Path.GetFullPath(configuration["Market:Path"] ?? Path.Combine(environment.ContentRootPath, "data", "market"));
    private static bool ValidId(string id) => Regex.IsMatch(id, "\\A[0-9]{8}T[0-9]{6}Z-[a-f0-9]{32}\\z");

    private string DirectoryFor(string id)
    {
        if (!ValidId(id)) throw new ArgumentException("Некорректный идентификатор записи.");
        CheckLink(Root);
        var directory = Path.Combine(Root, id);
        if (!Directory.Exists(directory)) throw new FileNotFoundException("Запись не найдена.");
        CheckLink(directory);
        return directory;
    }
    private static void CheckLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки вместо файлов записи не поддерживаются.");
    }
    private static string FileFor(string directory, string name, long limit)
    {
        var path = Path.Combine(directory, name);
        CheckLink(path);
        if (new FileInfo(path).Length > limit) throw new InvalidDataException($"Файл {name} превышает лимит анализатора.");
        return path;
    }

    // Finished recordings on a server are compressed by StorageHousekeeper; both forms are read the same way.
    private static string EventsName(string directory) =>
        File.Exists(Path.Combine(directory, StorageHousekeeper.Events)) ? StorageHousekeeper.Events
        : File.Exists(Path.Combine(directory, StorageHousekeeper.Compressed)) ? StorageHousekeeper.Compressed : StorageHousekeeper.Events;

    private async Task<(AnalysisRecording Recording, long Frequency)> Metadata(string directory, CancellationToken ct)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(FileFor(directory, "manifest.json", 8 * 1048576), ct));
        var root = manifest.RootElement;
        var version = root.GetProperty("schemaVersion").GetInt32();
        if (version is < 1 or > 3) throw new InvalidDataException("Версия записи не поддерживается.");
        var options = root.GetProperty("options");
        var coin = options.GetProperty("Coin").GetString()!;
        var source = options.TryGetProperty("SourceExchange", out var s) ? s.GetString()! : "hyperliquid";
        var target = options.TryGetProperty("Exchange", out var t) ? t.GetString()! : "binance";
        MarketCatalog.ValidatePair(source, target);
        var status = "recording";
        if (File.Exists(Path.Combine(directory, "summary.json")))
        {
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(FileFor(directory, "summary.json", 65536), ct));
            status = summary.RootElement.GetProperty("status").GetString() ?? "unknown";
        }
        var path = FileFor(directory, EventsName(directory), long.MaxValue);
        return (new(Path.GetFileName(directory), coin, source, target, root.GetProperty("startedUtc").GetDateTimeOffset(), status,
            new FileInfo(path).Length, File.Exists(Path.Combine(directory, "analysis-v1.json"))), root.GetProperty("stopwatchFrequency").GetInt64());
    }

    public async Task<RecordingList> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Root)) return new([], 0);
        CheckLink(Root);
        var entries = new List<AnalysisRecording>(); var unavailable = 0;
        foreach (var directory in Directory.EnumerateDirectories(Root).Where(d => ValidId(Path.GetFileName(d))).OrderDescending().Take(100))
        {
            ct.ThrowIfCancellationRequested();
            try { CheckLink(directory); entries.Add((await Metadata(directory, ct)).Recording); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or ArgumentException or InvalidOperationException)
            { unavailable++; }
        }
        return new(entries.ToArray(), unavailable);
    }

    public async Task<AnalysisReport> ReadAsync(string id, CancellationToken ct)
    {
        var directory = DirectoryFor(id);
        return JsonSerializer.Deserialize<AnalysisReport>(await File.ReadAllTextAsync(FileFor(directory, "analysis-v1.json", 1048576), ct), Json)
            ?? throw new InvalidDataException("Не удалось прочитать отчёт.");
    }

    public Task<AnalysisReport> AnalyzeAsync(string id, AnalysisSettings settings, CancellationToken ct)
    {
        settings.Validate();
        return ProcessAsync(id, "analysis-v1.json", (data, token) => MarketAnalysis.Analyze(data.Recording, settings,
            data.Frequency, data.First, data.Last, data.Events, data.Gaps, data.Points, token), ct);
    }

    public Task<ReplayReport> ReplayAsync(string id, ReplaySettings settings, CancellationToken ct)
    {
        settings.Validate();
        return ProcessAsync(id, "strategy-replay-v1.json", (data, token) => StrategyReplay.Run(data.Recording, settings,
            data.Frequency, data.First, data.Last, data.Points, token, data.Market), ct, settings.ExecutionModel == "depth");
    }

    // Recomputes every chosen recording with the same settings, one after another, then saves the combined report.
    public async Task<SeriesReport> SeriesAsync(SeriesRequest request, CancellationToken ct)
    {
        StrategySeries.Validate(request);
        foreach (var id in request.Ids) DirectoryFor(id);
        var items = new List<(SeriesRecording, ReplayReport?)>();
        foreach (var id in request.Ids)
        {
            ct.ThrowIfCancellationRequested();
            try { var report = await ReplayAsync(id, request.Settings, ct); items.Add((StrategySeries.Row(report), report)); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException
                or KeyNotFoundException or FormatException or OverflowException || e is OperationCanceledException && !ct.IsCancellationRequested)
            {
                items.Add((StrategySeries.Failed(id, e is OperationCanceledException ? "Расчёт записи превысил 60 секунд." : e.Message), null));
            }
        }
        var series = StrategySeries.Build(request.Settings, items, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(Root); CheckLink(Root);
        var output = Path.Combine(Root, "strategy-series-v1.json");
        if (File.Exists(output)) CheckLink(output);
        var temporary = Path.Combine(Root, "series-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(series, Json), ct);
            File.Move(temporary, output, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return series;
    }

    public async Task<SeriesReport> ReadSeriesAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Root)) throw new FileNotFoundException();
        CheckLink(Root);
        var path = Path.Combine(Root, "strategy-series-v1.json");
        if (!File.Exists(path)) throw new FileNotFoundException();
        return JsonSerializer.Deserialize<SeriesReport>(await File.ReadAllTextAsync(FileFor(Root, "strategy-series-v1.json", 16 * 1048576), ct), Json)
            ?? throw new InvalidDataException("Не удалось прочитать отчёт по серии записей.");
    }

    public async Task<ReplayReport> ReadReplayAsync(string id, CancellationToken ct)
    {
        var report = JsonSerializer.Deserialize<ReplayReport>(await File.ReadAllTextAsync(FileFor(DirectoryFor(id), "strategy-replay-v1.json", 8 * 1048576), ct), Json)
            ?? throw new InvalidDataException("Не удалось прочитать отчёт стратегии.");
        // A saved v0.1 report predates executionModel. Never relabel its BBO fills as depth fills.
        return report.Version == "0.1.0" ? report with { Settings = report.Settings with { ExecutionModel = "bbo" } } : report;
    }

    private async Task<T> ProcessAsync<T>(string id, string reportFile, Func<RecordingData, CancellationToken, T> calculate, CancellationToken ct, bool depth = false)
    {
        var directory = DirectoryFor(id);
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("Уже выполняется анализ. Дождитесь завершения.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var token = timeout.Token;
            var (recording, frequency) = await Metadata(directory, token);
            if (recording.Status == "recording") throw new InvalidOperationException("Сначала завершите запись. Незавершённые файлы не анализируются.");
            var name = EventsName(directory);
            // A compressed file is about ten times smaller; the event limits below bound the uncompressed size anyway.
            var file = FileFor(directory, name, name.EndsWith(".gz") ? 64L * 1048576 : 256L * 1048576);
            var report = await Task.Run(async () =>
            {
                var points = new List<MarketAnalysis.Input>();
                var parser = new RecordingEventParser(recording.Coin, recording.SourceExchange, recording.Exchange, depth);
                ReplayMarketData? market = null;
                if (depth)
                {
                    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(FileFor(directory, "manifest.json", 8 * 1048576), token));
                    market = new([ReplayInstrument.Read(manifest.RootElement, recording.SourceExchange, recording.Coin, true),
                        ReplayInstrument.Read(manifest.RootElement, recording.Exchange, recording.Coin, false)], [], []);
                }
                long first = long.MaxValue, last = 0;
                int events = 0, gaps = 0, levels = 0;
                using var reader = new StreamReader(name.EndsWith(".gz")
                    ? new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress) : File.OpenRead(file));
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    token.ThrowIfCancellationRequested();
                    if (++events > MarketAnalysis.MaxEvents) throw new InvalidDataException("Лимит анализатора — 1 миллион событий. Используйте более короткую запись.");
                    CaptureEvent item;
                    try { item = JsonSerializer.Deserialize<CaptureEvent>(line, Json) ?? throw new JsonException(); }
                    catch (JsonException) { throw new InvalidDataException($"Повреждённая строка {events}. Анализ прерван, чтобы не скрыть потерю данных."); }
                    if (item.ReceivedTicks < 0 || item.Source is null || item.Connection is null || item.Kind is null || item.Payload is null)
                        throw new InvalidDataException($"Некорректное событие в строке {events}.");
                    first = Math.Min(first, item.ReceivedTicks); last = Math.Max(last, item.ReceivedTicks);
                    if (item.Kind == "gap") gaps++;
                    var parsed = parser.Parse(item, events);
                    if (parsed.Trade is { } trade)
                    {
                        market!.Trades!.Add(trade);
                        levels += trade.Prints?.Length ?? 0;
                    }
                    if (parsed.Book is { } book)
                    {
                        market!.Books.Add(book);
                        levels += (book.Update?.Bids.Length ?? 0) + (book.Update?.Asks.Length ?? 0);
                    }
                    if (market is not null && (market.Books.Count + market.Trades!.Count > 300000 || levels > 2_000_000))
                        throw new InvalidDataException("Лимит симулятора — 300 тысяч сообщений стакана и сделок и 2 миллиона уровней. Используйте более короткую запись.");
                    if (parsed.Point is { } point)
                    {
                        points.Add(point);
                        if (points.Count > MarketAnalysis.MaxPoints) throw new InvalidDataException("Лимит анализатора — 300 тысяч обновлений котировок. Используйте более короткую запись.");
                    }
                }
                if (events == 0) throw new InvalidDataException("В записи нет событий.");
                return calculate(new(recording, frequency, first, last, events, gaps, points, market), token);
            }, token);
            var output = Path.Combine(directory, reportFile);
            if (File.Exists(output)) CheckLink(output);
            var temporary = Path.Combine(directory, "analysis-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, Json), token);
                File.Move(temporary, output, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return report;
        }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}
