using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TradeBot.Recorder;

// A scan watches many Binance USDT perpetuals at once: the most traded ones above a 24-hour quote volume, minus exclusions.
public sealed record ScanOptions(string Output, int Seconds, long MaxBytes, decimal MinQuoteVolume = 150_000_000, int MaxSymbols = 30,
    string[]? Exclude = null, int HistoryMinutes = 240)
{
    public const int MaxMb = 4096;
    public void Validate()
    {
        if (Seconds is < 60 or > 7200) throw new ArgumentException("Длительность сканирования — от 60 до 7200 с.");
        if (MaxBytes is < 1048576 or > MaxMb * 1048576L) throw new ArgumentException($"Предел записи — от 1 до {MaxMb} МБ.");
        if (MinQuoteVolume is < 0 or > 1_000_000_000_000) throw new ArgumentException("Минимальный объём за 24 ч — от 0 до 10¹² USDT.");
        if (MaxSymbols is < 1 or > 60) throw new ArgumentException("Монет в сканере — от 1 до 60.");
        if (HistoryMinutes is < 30 or > 1440) throw new ArgumentException("История свечей — от 30 до 1440 минут.");
        if ((Exclude ?? []).Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x, "\\A[A-Z0-9]{1,20}USDT\\z")))
            throw new ArgumentException("Исключения — символы вида BTCUSDT.");
    }
}

public sealed record ScanSymbol(string Symbol, decimal QuoteVolume, decimal LastPrice);

public interface IScanRunner
{
    Task<int> RunAsync(ScanOptions options, IRecordingObserver observer, CancellationToken ct);
}

public sealed class ScanRunner : IScanRunner
{
    public Task<int> RunAsync(ScanOptions options, IRecordingObserver observer, CancellationToken ct) => ScanRecorder.RunAsync(options, observer, ct);
}

public static class ScanRecorder
{
    public const string Rest = "binance-rest", Public = "binance-public", Market = "binance-market";

    // Trading USDT perpetuals with at least the minimum 24-hour quote volume, most traded first.
    public static ScanSymbol[] SelectSymbols(string exchangeInfo, string tickers, ScanOptions options)
    {
        var active = MarketCatalog.ActiveMarkets("binance", exchangeInfo).Select(x => x.Symbol).ToHashSet(StringComparer.Ordinal);
        var excluded = (options.Exclude ?? []).ToHashSet(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(tickers);
        return doc.RootElement.EnumerateArray()
            .Select(t => new ScanSymbol(t.GetProperty("symbol").GetString()!, Number(t, "quoteVolume"), Number(t, "lastPrice")))
            .Where(t => active.Contains(t.Symbol) && !excluded.Contains(t.Symbol) && t.QuoteVolume >= options.MinQuoteVolume && t.LastPrice > 0)
            .OrderByDescending(t => t.QuoteVolume).ThenBy(t => t.Symbol, StringComparer.Ordinal)
            .Take(options.MaxSymbols).ToArray();
    }

    private static decimal Number(JsonElement e, string name) =>
        decimal.Parse(e.GetProperty(name).GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    // Public streams of the chosen symbols: best prices and 20 book levels every 500 ms, and aggregated trades.
    public static Feed[] Feeds(IEnumerable<string> symbols)
    {
        var lower = symbols.Select(s => s.ToLowerInvariant()).ToArray();
        return [
            new(Public, new("wss://fstream.binance.com/public/stream?streams=" + string.Join('/', lower.SelectMany(s => new[] { s + "@bookTicker", s + "@depth20@500ms" }))), []),
            new(Market, new("wss://fstream.binance.com/market/stream?streams=" + string.Join('/', lower.Select(s => s + "@aggTrade"))), [])
        ];
    }

    public static string Classify(string raw, IReadOnlySet<string> symbols)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.TryGetProperty("result", out _) && root.TryGetProperty("id", out _)) return "control";
        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("e", out var e) && e.GetString() is "bookTicker" or "depthUpdate" or "aggTrade"
            && data.TryGetProperty("s", out var s) && s.GetString() is { } symbol && symbols.Contains(symbol)) return "market";
        throw new InvalidDataException("Неизвестное сообщение или чужой инструмент в потоке сканера.");
    }

    public static async Task<int> RunAsync(ScanOptions options, IRecordingObserver observer, CancellationToken ct)
    {
        options.Validate();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var exchangeInfo = await http.GetStringAsync("https://fapi.binance.com/fapi/v1/exchangeInfo", stop.Token);
        var tickers = await http.GetStringAsync("https://fapi.binance.com/fapi/v1/ticker/24hr", stop.Token);
        var selected = SelectSymbols(exchangeInfo, tickers, options);
        if (selected.Length == 0) throw new ArgumentException("Нет монет с таким объёмом за 24 часа. Снизьте порог объёма.");
        var symbols = selected.Select(x => x.Symbol).ToHashSet(StringComparer.Ordinal);

        var session = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(options.Output, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{session}");
        Directory.CreateDirectory(directory);
        using var info = JsonDocument.Parse(exchangeInfo);
        var manifest = new
        {
            schemaVersion = 1,
            kind = "scan",
            session,
            exchange = "binance",
            startedUtc = DateTimeOffset.UtcNow,
            options,
            stopwatchFrequency = Stopwatch.Frequency,
            symbols = selected,
            // Instrument rules of the chosen symbols as they were at the start: a later replay must not use today's API.
            instruments = info.RootElement.GetProperty("symbols").EnumerateArray().Where(x => symbols.Contains(x.GetProperty("symbol").GetString()!)).ToArray(),
            feeds = Feeds(symbols.Order(StringComparer.Ordinal)),
            executionEnabled = false,
            scope = "Raw public Binance capture for the level scanner: 1-minute history, best prices, 20 book levels, aggregated trades. No orders."
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest), stop.Token);
        observer.Started(directory);
        stop.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
        var capture = new CaptureFile(options.MaxBytes, onRecorded: observer.Recorded);
        await using var file = new FileStream(Path.Combine(directory, "events.ndjson"), FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 65536, FileOptions.Asynchronous);
        var writer = Drain();
        var gaps = 0;
        string? failure = null;
        async Task Drain()
        {
            try { await capture.DrainAsync(file); }
            catch { capture.Complete(); stop.Cancel(); throw; }
        }
        try
        {
            // History goes into the recording first, so the live scanner and a later replay start from the same candles.
            // The last kline is still open; it is dropped and rebuilt from trades.
            foreach (var symbol in symbols.Order(StringComparer.Ordinal))
            {
                var klines = await http.GetStringAsync($"https://fapi.binance.com/fapi/v1/klines?symbol={symbol}&interval=1m&limit={options.HistoryMinutes + 1}", stop.Token);
                capture.Publish(new(Rest, "history", "history", DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(),
                    JsonSerializer.Serialize(new { symbol, interval = "1m", klines = JsonSerializer.Deserialize<JsonElement>(klines) })));
            }
            async Task FeedTask(Feed feed)
            {
                try { await RecorderProgram.CaptureAsync(feed, raw => Classify(raw, symbols), [], capture, () => Interlocked.Increment(ref gaps), stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch { stop.Cancel(); throw; }
            }
            await Task.WhenAll(Feeds(symbols.Order(StringComparer.Ordinal)).Select(FeedTask));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) when (e is not OperationCanceledException) { failure = e.Message; }
        finally { capture.Complete(); }
        try { await writer; }
        catch (Exception e) { failure = e.Message; }
        var allReceived = new[] { Public, Market }.All(x => capture.MarketMessages.GetValueOrDefault(x) > 0);
        var summary = new RecordingReport(DateTimeOffset.UtcNow,
            failure is not null ? "failed" : !allReceived ? "no-data" : gaps > 0 ? "completed-with-gaps" : "completed",
            failure, gaps, capture.Written, capture.Bytes, capture.MarketMessages, capture.DelaySummary()) { Buffer = capture.BufferSummary() };
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)), CancellationToken.None);
        observer.Finished(summary);
        return failure is not null || !allReceived ? 1 : 0;
    }
}
