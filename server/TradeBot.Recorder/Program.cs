using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TradeBot.Recorder;

public static class RecorderProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("Публичная запись, без торговли: --coin ARB --symbol ARBUSDT [--seconds 60] [--output data/market] [--max-mb 256]. ARB — пример проверки подключения, не выбор стратегии.");
            return 0;
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await RunAsync(RecorderOptions.Parse(args), stop.Token); }
        catch (Exception e) when (e is ArgumentException or IOException or HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Запись не завершена: {e.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    public static async Task<int> RunAsync(RecorderOptions options, CancellationToken ct, IRecordingObserver? observer = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var binanceTask = http.GetStringAsync("https://fapi.binance.com/fapi/v1/exchangeInfo", stop.Token);
        var hlTask = GetHyperliquidMetaAsync(http, stop.Token);
        await Task.WhenAll(binanceTask, hlTask);
        var binance = await binanceTask;
        var hyperliquid = await hlTask;
        ValidateMarkets(binance, hyperliquid, options);

        var session = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(options.Output, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{session}");
        Directory.CreateDirectory(directory);
        using var binanceDoc = JsonDocument.Parse(binance);
        using var hyperliquidDoc = JsonDocument.Parse(hyperliquid);
        var manifest = new
        {
            schemaVersion = 1,
            session,
            startedUtc = DateTimeOffset.UtcNow,
            options,
            stopwatchFrequency = Stopwatch.Frequency,
            feeds = options.Feeds,
            executionEnabled = false,
            scope = "Raw public feed capture. No full orderbook, signals, fills or verified contract equivalence.",
            binanceMetadata = binanceDoc.RootElement,
            hyperliquidMetadata = hyperliquidDoc.RootElement
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest), stop.Token);
        if (observer is null) Console.WriteLine($"Публичная запись {options.Coin}/{options.Symbol}, {options.Seconds} секунд. Каталог: {directory}");
        observer?.Started(directory);
        stop.CancelAfter(TimeSpan.FromSeconds(options.Seconds));
        var capture = new CaptureFile(options.MaxBytes, onRecorded: observer is null ? null : observer.Recorded);
        await using var file = new FileStream(Path.Combine(directory, "events.ndjson"), FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 65536, FileOptions.Asynchronous);
        var gaps = 0;
        string? failure = null;
        async Task Drain()
        {
            try { await capture.DrainAsync(file); }
            catch { stop.Cancel(); throw; }
        }
        async Task FeedTask(Feed feed)
        {
            try { await CaptureAsync(feed, options, capture, () => Interlocked.Increment(ref gaps), stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch { stop.Cancel(); throw; }
        }
        var writer = Drain();
        try { await Task.WhenAll(options.Feeds.Select(FeedTask)); }
        catch (Exception e) { failure = e.Message; }
        finally { capture.Complete(); }
        try { await writer; }
        catch (Exception e) { failure ??= e.Message; }
        var allReceived = options.Feeds.All(x => capture.MarketMessages.GetValueOrDefault(x.Name) > 0);
        var summary = new RecordingReport(DateTimeOffset.UtcNow,
            failure is not null ? "failed" : !allReceived ? "no-data" : gaps > 0 ? "completed-with-gaps" : "completed",
            failure, gaps, capture.Written, capture.Bytes, capture.MarketMessages, capture.DelaySummary());
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        observer?.Finished(summary);
        if (observer is null) Console.WriteLine(JsonSerializer.Serialize(summary));
        return failure is not null || !allReceived ? 1 : 0;
    }

    public static void ValidateMarkets(string binance, string hyperliquid, RecorderOptions options)
    {
        using var b = JsonDocument.Parse(binance);
        using var h = JsonDocument.Parse(hyperliquid);
        var validB = b.RootElement.GetProperty("symbols").EnumerateArray().Any(s =>
            s.GetProperty("symbol").GetString() == options.Symbol && s.GetProperty("baseAsset").GetString() == options.Coin
            && s.GetProperty("quoteAsset").GetString() == "USDT" && s.GetProperty("contractType").GetString() == "PERPETUAL"
            && s.GetProperty("status").GetString() == "TRADING");
        var validH = h.RootElement.GetProperty("universe").EnumerateArray().Any(s =>
            s.GetProperty("name").GetString() == options.Coin && (!s.TryGetProperty("isDelisted", out var delisted) || !delisted.GetBoolean()));
        if (!validB || !validH) throw new ArgumentException("Нет активного инструмента на обеих площадках. Подписки не запущены.");
    }

    private static async Task<string> GetHyperliquidMetaAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "meta" }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task CaptureAsync(Feed feed, RecorderOptions options, CaptureFile capture, Action gap, CancellationToken ct)
    {
        var retry = 0;
        while (!ct.IsCancellationRequested)
        {
            var connection = Guid.NewGuid().ToString("N");
            void Record(string kind, string payload) => capture.Publish(new(feed.Name, connection, kind, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), payload));
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            try
            {
                Record("connecting", feed.Endpoint.ToString());
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(feed.Endpoint, connect.Token);
                }
                Record("connected", "Новая сессия; данные до подключения не восстанавливаются.");
                foreach (var type in feed.Subscriptions)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { method = "subscribe", subscription = new { type, coin = options.Coin } });
                    await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
                }
                while (!ct.IsCancellationRequested)
                {
                    // No message != no price change. This timeout marks a recording gap, not a trading signal.
                    using var receive = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    receive.CancelAfter(TimeSpan.FromSeconds(30));
                    var raw = await MarketWire.ReceiveTextAsync(socket, receive.Token);
                    var ticks = Stopwatch.GetTimestamp();
                    var utc = DateTimeOffset.UtcNow;
                    string kind;
                    try { kind = MarketWire.Classify(feed.Name, raw, options.Coin, options.Symbol); }
                    catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
                    {
                        capture.Publish(new(feed.Name, connection, "invalid", utc, ticks, raw));
                        throw new InvalidDataException("Неожиданный формат потока; исходное сообщение сохранено.", e);
                    }
                    capture.Publish(new(feed.Name, connection, kind, utc, ticks, raw));
                    retry = 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or HttpRequestException)
            {
                gap();
                Record("gap", e is OperationCanceledException ? "Таймаут подключения или 30 секунд без сообщений." : e.Message);
                Console.Error.WriteLine($"{feed.Name}: разрыв; повторное подключение.");
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10000, 500 * Math.Pow(2, Math.Min(retry++, 5))) + Random.Shared.Next(250)), ct);
            }
            finally { socket.Abort(); }
        }
    }
}
