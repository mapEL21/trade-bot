using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

// The live market stream of chosen coins: every message as it arrives ({"stream": "...", "data": {...}}, the same form the
// data recording keeps). Coins are added and removed while it runs.
public interface ILiveFeed
{
    event Action<string>? Message;
    void Watch(string symbol);
    void Unwatch(string symbol);
    Task RunAsync(CancellationToken ct);
}

// Binance futures public streams: the book of 20 levels every 100 ms (public endpoint) and aggregated trades (market
// endpoint), one connection each, subscribed per coin. A dropped connection is opened again with all its coins.
public sealed class BinanceLiveFeed(ILogger<BinanceLiveFeed> logger) : ILiveFeed
{
    public event Action<string>? Message;
    private readonly Socket[] sockets =
    [
        new(new Uri("wss://fstream.binance.com/public/stream"), s => [$"{s}@depth20@100ms"]),
        new(new Uri("wss://fstream.binance.com/market/stream"), s => [$"{s}@aggTrade"]),
    ];

    private sealed class Socket(Uri endpoint, Func<string, string[]> streams)
    {
        public readonly Uri Endpoint = endpoint;
        public readonly Func<string, string[]> Streams = streams;
        public readonly HashSet<string> Symbols = [];
        public readonly SemaphoreSlim Send = new(1, 1);
        public ClientWebSocket? Connection;
        public int Id;
    }

    public void Watch(string symbol) { foreach (var s in sockets) Change(s, symbol.ToLowerInvariant(), true); }
    public void Unwatch(string symbol) { foreach (var s in sockets) Change(s, symbol.ToLowerInvariant(), false); }

    private void Change(Socket socket, string symbol, bool add)
    {
        lock (socket) { if (add ? !socket.Symbols.Add(symbol) : !socket.Symbols.Remove(symbol)) return; }
        _ = Command(socket, add ? "SUBSCRIBE" : "UNSUBSCRIBE", socket.Streams(symbol));
    }

    private async Task Command(Socket socket, string method, string[] streams)
    {
        if (socket.Connection is not { State: WebSocketState.Open } connection || streams.Length == 0) return;
        var body = JsonSerializer.Serialize(new { method, @params = streams, id = Interlocked.Increment(ref socket.Id) });
        await socket.Send.WaitAsync();
        try { await connection.SendAsync(Encoding.UTF8.GetBytes(body), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
        finally { socket.Send.Release(); }
    }

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(sockets.Select(s => Run(s, ct)));

    private async Task Run(Socket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool idle;
            lock (socket) idle = socket.Symbols.Count == 0;
            if (idle) { try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; } continue; }
            using var connection = new ClientWebSocket();
            try
            {
                await connection.ConnectAsync(socket.Endpoint, ct);
                socket.Connection = connection;
                string[] all;
                lock (socket) all = socket.Symbols.SelectMany(socket.Streams).ToArray();
                await Command(socket, "SUBSCRIBE", all);
                var buffer = new byte[1 << 16];
                using var message = new MemoryStream();
                while (connection.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var part = await connection.ReceiveAsync(buffer, ct);
                    if (part.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, part.Count);
                    if (!part.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    if (text.StartsWith("{\"stream\"", StringComparison.Ordinal)) Message?.Invoke(text);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or InvalidOperationException)
            {
                logger.LogWarning(e, "Live stream {Endpoint} dropped", socket.Endpoint);
            }
            finally { socket.Connection = null; }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
        }
    }
}

// What a live run shows: the coins the bot watches now (with the screener's wall on each) and the positions it holds.
public sealed record LiveWatch(string Symbol, int Side, decimal Wall, decimal DistancePct);
// Exchange: real positions on the exchange (testnet runs) and why orders cannot go there, if they cannot.
public sealed record LiveView(LiveWatch[] Watching, EnginePosition[] Open, string? Segment, ExchangeHolding[]? Exchange = null, string? ExchangeProblem = null);

// Live density bots: for every running live run the screener's walls near the price (within 1.5%) pick the coins to
// watch; their live stream goes to the run's engine and, line by line, into the run's recording in data/live/<run>/<start>.
// The engine is the same as for recordings, so a run computed again from its recording gives the same trades. Nothing is
// sent to an exchange.
public sealed class LiveBots(PaperRunStore store, DensityScreener screener, IDepthSource source, ILiveFeed feed, IConfiguration configuration,
    IHostEnvironment environment, ExchangeCredentials credentials, ExchangeHttp http, ILogger<LiveBots> logger) : IHostedService, IDisposable
{
    public const decimal WatchPct = 1.5m;
    public const int MaxWatched = 10;
    public static TimeSpan Tick = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class Session(SignalRun run, string directory, StreamWriter writer)
    {
        public readonly SignalRun Run = run;
        public readonly string Directory = directory;
        public readonly StreamWriter Writer = writer;
        public readonly BotEngine Engine = new(run.Parameters, new Dictionary<string, decimal>(), liquidations: false);
        public readonly Dictionary<string, LiveWatch> Watching = new(StringComparer.Ordinal);
        public string Screened = "";
        public readonly object Gate = new();
        // Testnet runs: the engine's decisions also go to the exchange as orders.
        public ExchangeExecutor? Executor;
        public string? ExchangeProblem;

        public void Write(CaptureEvent item)
        {
            lock (Gate)
            {
                Writer.WriteLine(JsonSerializer.Serialize(item));
                Engine.Process(item);
            }
        }
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = [];
    private readonly Dictionary<string, int> subscribed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim syncing = new(1, 1);
    private Task? loop, stream;

    public string Root => configuration["Live:Path"] ?? Path.Combine(environment.ContentRootPath, "data", "live");
    public static string RunDirectory(string root, string runId) => Path.Combine(root, runId);

    public Task StartAsync(CancellationToken ct)
    {
        feed.Message += OnMessage;
        stream = Task.Run(() => feed.RunAsync(shutdown.Token));
        loop = Task.Run(Loop);
        return Task.CompletedTask;
    }

    private async Task Loop()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try { await Sync(shutdown.Token); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return; }
            catch (Exception e) when (e is IOException or HttpRequestException or ArgumentException or JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                logger.LogWarning(e, "Live bots sync failed");
            }
            try { await Task.Delay(Tick, shutdown.Token); } catch (OperationCanceledException) { return; }
        }
    }

    // Live:Enabled — the launcher and the server turn it on; off by default, so tests and other hosts never open the live stream.
    public bool Enabled => configuration.GetValue<bool>("Live:Enabled");

    // Opens a session for every running live run and closes the ones whose run stopped; then each session follows the screener.
    public async Task Sync(CancellationToken ct)
    {
        if (!Enabled) return;
        // One sync at a time: two at once would both subscribe the same coin.
        await syncing.WaitAsync(ct);
        try { await SyncOnce(ct); }
        finally { syncing.Release(); }
    }

    private async Task SyncOnce(CancellationToken ct)
    {
        var running = (await store.SignalRunsAsync(null, ct)).Where(r => r.Live && r.ToMs is null).ToDictionary(r => r.Id);
        List<Session> current, closing;
        lock (gate)
        {
            closing = sessions.Where(x => !running.ContainsKey(x.Key)).Select(x => x.Value).ToList();
            foreach (var session in closing) sessions.Remove(session.Run.Id);
            foreach (var run in running.Values.Where(r => !sessions.ContainsKey(r.Id))) sessions[run.Id] = Open(run);
            current = sessions.Values.ToList();
        }
        // A stopped testnet run closes its positions on the exchange before the session ends.
        foreach (var session in closing)
        {
            if (session.Executor is { } executor) await executor.FlattenAndStopAsync();
            Close(session);
        }
        foreach (var session in current) session.Executor?.Reconcile();
        var walls = screener.Snapshot().Walls;
        foreach (var session in current) await Follow(session, walls, ct);
    }

    private Session Open(SignalRun run)
    {
        var directory = Path.Combine(RunDirectory(Root, run.Id), DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new { live = true, run = run.Id, symbols = Array.Empty<object>(), liquidations = false }));
        var writer = new StreamWriter(Path.Combine(directory, StorageHousekeeper.Events), append: true, new UTF8Encoding(false)) { AutoFlush = true };
        logger.LogInformation("Live session {Run} started in {Directory}", run.Id, directory);
        var session = new Session(run, directory, writer);
        if (run.Execution != SignalRun.Simulation)
        {
            if (!ExchangeModes.Allowed(configuration, run.Execution)) session.ExchangeProblem = "Этот режим исполнения не разрешён: заявки не отправляются.";
            else if (credentials.Load(run.Execution) is not { } keys) session.ExchangeProblem = "Нет API-ключей: заявки не отправляются.";
            else
            {
                var client = new BinanceFuturesClient(http.Client, ExchangeModes.BaseUrl(configuration, run.Execution), keys.Key, keys.Secret);
                session.Executor = new ExchangeExecutor(client, run, store, logger);
                session.Engine.Intent += session.Executor.Enqueue;
            }
        }
        return session;
    }

    private void Close(Session session)
    {
        lock (session.Gate)
        {
            foreach (var symbol in session.Watching.Keys) Release(symbol);
            session.Writer.Dispose();
            session.Executor?.Dispose();
            File.WriteAllText(Path.Combine(session.Directory, "summary.json"), JsonSerializer.Serialize(new { completedUtc = DateTimeOffset.UtcNow }));
        }
    }

    // Coins with a screener wall within 1.5% of the price (nearest first, at most 10) plus those with an open position.
    private async Task Follow(Session session, ScreenerWall[] walls, CancellationToken ct)
    {
        var near = walls.Where(w => w.DistancePct <= WatchPct).OrderBy(w => w.DistancePct).GroupBy(w => w.Symbol).Take(MaxWatched)
            .ToDictionary(g => g.Key, g => g.First());
        HashSet<string> held;
        lock (session.Gate) held = session.Engine.Open().Select(p => p.Symbol).ToHashSet();
        var wanted = near.Keys.Concat(held).ToHashSet();
        foreach (var symbol in wanted.Where(s => !session.Watching.ContainsKey(s)))
        {
            if (screener.Market(symbol) is not { } market) continue;
            var history = await source.KlinesAsync(symbol, ct);
            var now = DateTimeOffset.UtcNow;
            session.Write(new("binance-live", "control", "watch", now, Stopwatch.GetTimestamp(),
                JsonSerializer.Serialize(new { symbol, volume = market.QuoteVolume, tickSize = market.TickSize })));
            session.Write(new(ScanRecorder.Rest, "history", "history", now, Stopwatch.GetTimestamp(), history));
            var wall = near.GetValueOrDefault(symbol);
            lock (session.Gate) session.Watching[symbol] = new(symbol, wall?.Side ?? 0, wall?.Price ?? 0, wall?.DistancePct ?? 0);
            lock (gate)
            {
                subscribed[symbol] = subscribed.GetValueOrDefault(symbol) + 1;
                if (subscribed[symbol] == 1) feed.Watch(symbol);
            }
        }
        foreach (var symbol in session.Watching.Keys.Where(s => !wanted.Contains(s)).ToList())
        {
            lock (session.Gate) session.Watching.Remove(symbol);
            session.Write(new("binance-live", "control", "unwatch", DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), JsonSerializer.Serialize(new { symbol })));
            lock (gate) Release(symbol);
        }
        foreach (var (symbol, w) in near.Where(x => session.Watching.ContainsKey(x.Key)))
            lock (session.Gate) session.Watching[symbol] = new(symbol, w.Side, w.Price, w.DistancePct);
        // The screener's walls on the watched coins: the bot trades only these.
        var screened = walls.Where(w => session.Watching.ContainsKey(w.Symbol)).Select(w => new ScreenedWall(w.Symbol, w.Side, w.Price))
            .OrderBy(w => w.Symbol, StringComparer.Ordinal).ThenBy(w => w.Side).ThenBy(w => w.Price).ToList();
        var text = JsonSerializer.Serialize(new { walls = screened }, Json);
        if (text != session.Screened)
        {
            session.Screened = text;
            session.Write(new("binance-live", "control", "screener", DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), text));
        }
    }

    private void Release(string symbol)
    {
        if (!subscribed.TryGetValue(symbol, out var count)) return;
        if (count <= 1) { subscribed.Remove(symbol); feed.Unwatch(symbol); }
        else subscribed[symbol] = count - 1;
    }

    private void OnMessage(string raw)
    {
        var at = raw.IndexOf('@');
        if (at < 12) return;
        var symbol = raw[11..at].ToUpperInvariant();
        List<Session> targets;
        lock (gate) targets = sessions.Values.Where(s => { lock (s.Gate) return s.Watching.ContainsKey(symbol); }).ToList();
        if (targets.Count == 0) return;
        var item = new CaptureEvent("binance-live", raw.Contains("@depth", StringComparison.Ordinal) ? "public" : "market", "market",
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), raw);
        foreach (var session in targets) session.Write(item);
    }

    // The run's current recording, its result so far and what it watches; null when the run has no live session.
    public (string Directory, EngineResult Result, LiveView View)? Current(string runId)
    {
        Session? session;
        lock (gate) session = sessions.GetValueOrDefault(runId);
        if (session is null) return null;
        lock (session.Gate)
            return (session.Directory, session.Engine.Current(Path.GetFileName(session.Directory)),
                new LiveView(session.Watching.Values.OrderBy(w => w.DistancePct).ToArray(), session.Engine.Open(), Path.GetFileName(session.Directory),
                    session.Executor?.Holdings(), session.ExchangeProblem));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Cancellation.Cancel(shutdown);
        List<Session> all;
        lock (gate) { all = sessions.Values.ToList(); sessions.Clear(); }
        // On shutdown real positions keep their protective stops on the exchange; the bot reports them after a restart.
        foreach (var session in all) Close(session);
        foreach (var task in new[] { loop, stream }.OfType<Task>())
            try { await task.WaitAsync(ct); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        feed.Message -= OnMessage;
        shutdown.Dispose();
    }
}
