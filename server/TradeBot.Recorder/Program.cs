using System.Diagnostics;
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
            Console.WriteLine("Публичная запись: --coin ARB --symbol ARBUSDT [--source-exchange hyperliquid] [--exchange binance] [--seconds 60] [--output data/market] [--max-mb 256]. Выберите две разные биржи: hyperliquid, binance, bybit, okx. --symbol относится к бирже сравнения: ARBUSDT, ARB-USDT-SWAP или ARB. Торговли нет.");
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
        MarketCatalog.ValidatePair(options.SourceExchange, options.Exchange);
        var exchangeTask = MarketCatalog.ExchangeMetadataAsync(http, options.Exchange, stop.Token);
        var sourceTask = MarketCatalog.ExchangeMetadataAsync(http, options.SourceExchange, stop.Token);
        await Task.WhenAll(exchangeTask, sourceTask);
        var exchangeMetadata = await exchangeTask;
        var sourceMetadata = await sourceTask;
        ValidateMarkets(exchangeMetadata, sourceMetadata, options);

        var session = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(options.Output, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{session}");
        Directory.CreateDirectory(directory);
        using var exchangeDoc = JsonDocument.Parse(exchangeMetadata);
        using var sourceDoc = JsonDocument.Parse(sourceMetadata);
        var manifest = new
        {
            schemaVersion = 3,
            session,
            startedUtc = DateTimeOffset.UtcNow,
            options,
            stopwatchFrequency = Stopwatch.Frequency,
            feeds = options.Feeds,
            executionEnabled = false,
            scope = "Raw public feed capture. No full orderbook, signals, fills or verified contract equivalence.",
            exchangeMetadata = exchangeDoc.RootElement,
            sourceMetadata = sourceDoc.RootElement
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
            catch { capture.Complete(); stop.Cancel(); throw; }
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
        // A disk/size failure can cause secondary producer errors; retain the writer's root cause.
        catch (Exception e) { failure = e.Message; }
        var allReceived = options.Feeds.All(x => capture.MarketMessages.GetValueOrDefault(x.Name) > 0);
        var summary = new RecordingReport(DateTimeOffset.UtcNow,
            failure is not null ? "failed" : !allReceived ? "no-data" : gaps > 0 ? "completed-with-gaps" : "completed",
            failure, gaps, capture.Written, capture.Bytes, capture.MarketMessages, capture.DelaySummary())
            { Buffer = capture.BufferSummary() };
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        observer?.Finished(summary);
        if (observer is null) Console.WriteLine(JsonSerializer.Serialize(summary));
        return failure is not null || !allReceived ? 1 : 0;
    }

    public static void ValidateMarkets(string exchangeMetadata, string sourceMetadata, RecorderOptions options)
    {
        if (!MarketCatalog.CommonMarkets(options.Exchange, exchangeMetadata, sourceMetadata, options.SourceExchange).Contains(new(options.Coin, options.Symbol)))
            throw new ArgumentException("Нет активного инструмента на обеих площадках. Подписки не запущены.");
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
            using var connected = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task heartbeat = Task.CompletedTask;
            try
            {
                Record("connecting", feed.Endpoint.ToString());
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(feed.Endpoint, connect.Token);
                }
                Record("connected", "Новая сессия; данные до подключения не восстанавливаются.");
                foreach (var message in MarketWire.SubscriptionMessages(feed, options))
                {
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
                }
                heartbeat = MarketWire.HeartbeatAsync(socket, feed.Name, connected.Token);
                while (!ct.IsCancellationRequested)
                {
                    // No message != no price change. This timeout marks a recording gap, not a trading signal.
                    using var receive = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    receive.CancelAfter(TimeSpan.FromSeconds(30));
                    var raw = await MarketWire.ReceiveTextAsync(socket, receive.Token);
                    var ticks = Stopwatch.GetTimestamp();
                    var utc = DateTimeOffset.UtcNow;
                    string kind;
                    try { kind = MarketWire.Classify(feed.Name, raw, options.Coin, options.SymbolForFeed(feed.Name)); }
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
            finally
            {
                connected.Cancel();
                socket.Abort();
                try { await heartbeat; }
                catch (Exception e) when (e is OperationCanceledException or WebSocketException) { }
            }
        }
    }
}
