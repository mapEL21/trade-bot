using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeBot.Api;

public sealed record LabStatus(string State, string Explanation, int Done, int Total, long Lines, string? Current);
// Count: events with a known result; Episodes: independent market moments they form. Mean, win rate and halves are per episode.
public sealed record LabRow(string Type, string Mode, int Horizon, int Count, decimal MeanPct, decimal WinRate, decimal? FirstHalf,
    decimal? SecondHalf, bool Stable, int Episodes = 0);
public sealed record LabCount(string Type, int Count, decimal PerHour, int Episodes = 0);
public sealed record LabReport(int Recordings, decimal Hours, int Events, decimal FeePct, LabCount[] Counts, LabRow[] Rows, string[] Notes,
    long? FromMs = null);
// One signal of the pre-registered hypothesis and where its check stands: "collecting", "confirmed" or "rejected".
public sealed record HypothesisSignal(string Type, string Mode, int Horizon, int Episodes, int Needed, decimal? MeanPct, decimal? WinRate,
    decimal? FirstHalf, decimal? SecondHalf, string Verdict);
public sealed record HypothesisCheck(string Statement, long FromMs, decimal FeePct, decimal Hours, HypothesisSignal[] Signals, string Verdict,
    string Explanation);

// The signal lab: runs every finished scanner recording through SignalDetector once (the result is kept next to the recording
// as signal-lab-v1.json) and then sums up, for every event type, both trading directions and every horizon, what the trade
// would have made after fees — overall and separately on the earlier and later half of the events.
public sealed class SignalLab(IConfiguration configuration, IHostEnvironment environment, ILogger<SignalLab> logger) : IHostedService, IDisposable
{
    public const string File = "signal-lab-v1.json";
    // One LabPath per line, written before File: a recording with File always has its paths.
    public const string PathFile = "signal-path-v1.ndjson";
    public const int MinCount = 100;
    // Events of one type and direction starting within this time of each other, on any coins, are one market moment.
    public const long EpisodeMs = 10_000;

    // A market-wide jerk fires the same event on many coins at once; counting each would multiply one observation.
    public static List<List<LabEvent>> Episodes(IEnumerable<LabEvent> events)
    {
        var episodes = new List<List<LabEvent>>();
        foreach (var group in events.GroupBy(e => (e.Type, e.Direction)))
        {
            List<LabEvent>? current = null;
            foreach (var e in group.OrderBy(e => e.AtMs))
            {
                if (current is null || e.AtMs - current[0].AtMs > EpisodeMs) episodes.Add(current = []);
                current.Add(e);
            }
        }
        return episodes.OrderBy(e => e[0].AtMs).ToList();
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private LabStatus status = new("idle", "Записи ещё не обрабатывались.", 0, 0, 0, null);
    private Task? task, auto;

    private string Root => Path.GetFullPath(configuration["Scanner:Path"] ?? Path.Combine(environment.ContentRootPath, "data", "scans"));
    public LabStatus Status { get { lock (gate) return status; } }

    // Finished recordings only: summary.json is written after the last event.
    private IEnumerable<string> Recordings() => !Directory.Exists(Root) ? []
        : Directory.EnumerateDirectories(Root).Where(d => Regex.IsMatch(Path.GetFileName(d), "\\A[0-9]{8}T[0-9]{6}Z-[a-f0-9]{32}\\z")
            && System.IO.File.Exists(Path.Combine(d, "summary.json")) && System.IO.File.Exists(Path.Combine(d, "manifest.json"))).Order();

    private static LabRecording? Cached(string directory)
    {
        var path = Path.Combine(directory, File);
        if (!System.IO.File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LabRecording>(System.IO.File.ReadAllText(path), Json) is { Version: SignalDetector.Version } r ? r : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    // With Lab:AutoProcess (the launcher and the server turn it on) new finished recordings are processed on their own every few
    // minutes, so the report is up to date without pressing the button; the button still starts a run at once. Off by default,
    // so tests and other hosts never start reading the real recordings.
    public Task StartAsync(CancellationToken ct)
    {
        if (configuration.GetValue<bool>("Lab:AutoProcess")) auto = Task.Run(AutoLoop);
        return Task.CompletedTask;
    }

    private async Task AutoLoop()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), shutdown.Token);
            while (true)
            {
                bool pending;
                lock (gate) pending = task is not { IsCompleted: false } && Recordings().Any(d => Cached(d) is null);
                if (pending) Start();
                await Task.Delay(TimeSpan.FromMinutes(5), shutdown.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogWarning(e, "Signal lab auto processing stopped"); }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task? running;
        lock (gate)
        {
            if (disposed) return;
            shutdown.Cancel();
            running = task;
        }
        foreach (var t in new[] { auto, running }.OfType<Task>())
            try { await t.WaitAsync(ct); } catch (OperationCanceledException) { }
    }

    public bool Start()
    {
        lock (gate)
        {
            if (task is { IsCompleted: false }) return false;
            var pending = Recordings().Where(d => Cached(d) is null).ToList();
            status = new("running", pending.Count == 0 ? "Все записи уже обработаны." : $"Обрабатываем записи: {pending.Count}.", 0, pending.Count, 0, null);
            task = Task.Run(() => Process(pending));
            return true;
        }
    }

    private void Process(List<string> pending)
    {
        var done = 0;
        foreach (var directory in pending)
        {
            var name = Path.GetFileName(directory);
            lock (gate) status = status with { Current = name, Lines = 0 };
            try
            {
                var (result, paths) = SignalDetector.RunFull(directory, shutdown.Token, lines => { lock (gate) status = status with { Lines = lines }; });
                var pathTemporary = Path.Combine(directory, PathFile + ".tmp");
                System.IO.File.WriteAllLines(pathTemporary, paths.Select(p => JsonSerializer.Serialize(p, Json)));
                System.IO.File.Move(pathTemporary, Path.Combine(directory, PathFile), true);
                var temporary = Path.Combine(directory, File + ".tmp");
                System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(result, Json));
                System.IO.File.Move(temporary, Path.Combine(directory, File), true);
            }
            catch (OperationCanceledException) { lock (gate) status = status with { State = "stopped", Explanation = "Обработка прервана." }; return; }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or KeyNotFoundException or UnauthorizedAccessException)
            {
                logger.LogWarning(e, "Signal lab skipped {Recording}", name);
            }
            done++;
            lock (gate) status = status with { Done = done };
        }
        lock (gate) status = status with { State = "finished", Current = null, Explanation = $"Обработано записей: {done} из {pending.Count}." };
    }

    // Changes whenever a recording is processed: the number of processed recordings and the newest result file.
    public string Fingerprint()
    {
        var files = Recordings().Select(d => new FileInfo(Path.Combine(d, File))).Where(f => f.Exists).ToList();
        return $"{files.Count}|{(files.Count == 0 ? 0 : files.Max(f => f.LastWriteTimeUtc.Ticks))}";
    }

    private List<LabRecording> Load() => Recordings().Select(Cached).OfType<LabRecording>().ToList();

    // Processed recordings with the paths of one event type, oldest first, for the bot: only recordings that may hold events
    // from fromMs on are read, and only path lines of that type are parsed.
    public List<(LabRecording Recording, Dictionary<int, LabPath> Paths)> WithPaths(string type, long fromMs, long? toMs)
    {
        var result = new List<(LabRecording, Dictionary<int, LabPath>)>();
        var marker = JsonSerializer.Serialize(type, Json);
        foreach (var directory in Recordings())
        {
            if (Cached(directory) is not { } recording || recording.LastMs < fromMs || (toMs is { } to && recording.FirstMs >= to)) continue;
            var file = Path.Combine(directory, PathFile);
            var paths = new Dictionary<int, LabPath>();
            if (System.IO.File.Exists(file))
                foreach (var line in System.IO.File.ReadLines(file))
                    if (line.Contains(marker, StringComparison.Ordinal) && JsonSerializer.Deserialize<LabPath>(line, Json) is { } path && path.Type == type)
                        paths[path.Index] = path;
            result.Add((recording, paths));
        }
        return result.OrderBy(x => x.Item1.FirstMs).ToList();
    }

    // fromMs: only events from this moment on, so a hypothesis chosen earlier is checked on data it was not chosen on.
    public LabReport Report(decimal feePct, long? fromMs = null)
    {
        if (feePct is < 0 or > 1) throw new ArgumentException("Комиссии круга — от 0 до 1%.");
        if (fromMs is < 0) throw new ArgumentException("Некорректная дата начала.");
        var from = fromMs ?? 0;
        var recordings = Load().Where(r => r.LastMs >= from).ToList();
        var events = recordings.SelectMany(r => r.Events).Where(e => e.AtMs >= from).OrderBy(e => e.AtMs).ToList();
        // A recording without a known start (FirstMs 0) adds no hours instead of the time since 1970.
        var hours = recordings.Where(r => r.FirstMs > 0).Sum(r => Math.Max(0, r.LastMs - Math.Max(r.FirstMs, from))) / 3_600_000m;
        var episodes = Episodes(events);
        var rows = new List<LabRow>();
        foreach (var type in SignalDetector.Types)
            foreach (var withEvent in new[] { true, false })
                for (var i = 0; i < SignalDetector.Horizons.Length; i++)
                {
                    // One value per episode: the average result of its events with a known exit, after fees.
                    var known = episodes.Where(ep => ep[0].Type == type)
                        .Select(ep => ep.Select(e => e.Move(i, withEvent)).OfType<decimal>().ToList()).Where(m => m.Count > 0).ToList();
                    var values = known.Select(m => m.Average() - feePct).ToList();
                    if (values.Count == 0) { rows.Add(new(type, withEvent ? "follow" : "fade", SignalDetector.Horizons[i], 0, 0, 0, null, null, false)); continue; }
                    var half = values.Count / 2;
                    decimal? Mean(IEnumerable<decimal> v) => v.Any() ? Math.Round(v.Average(), 4) : null;
                    var first = Mean(values.Take(half)); var second = Mean(values.Skip(half));
                    rows.Add(new(type, withEvent ? "follow" : "fade", SignalDetector.Horizons[i], known.Sum(m => m.Count), Math.Round(values.Average(), 4),
                        Math.Round(values.Count(v => v > 0) * 100m / values.Count, 1), first, second,
                        values.Count >= MinCount && first > 0 && second > 0, values.Count));
                }
        var counts = SignalDetector.Types.Select(t =>
        {
            var n = events.Count(e => e.Type == t);
            return new LabCount(t, n, hours > 0 ? Math.Round(n / hours, 1) : 0, episodes.Count(ep => ep[0].Type == t));
        }).ToArray();
        string[] notes =
        [
            "Вход через 0,25 с после события по встречной цене стакана (покупка по ask, продажа по bid), выход через выбранное время тоже по встречной цене: спред уже учтён. Из результата вычтены комиссии круга.",
            "Эпизод — события одного типа и направления, начавшиеся в пределах 10 с друг от друга на любых монетах: общий рывок рынка на десяти монетах — одно наблюдение, а не десять. Средний результат, доля прибыльных и половины считаются по эпизодам.",
            "«По движению» — сделка в сторону события, «против» — в обратную. Половины — первые и вторые 50% эпизодов по времени; «устойчиво» — от " + MinCount + " эпизодов и плюс на обеих половинах.",
            $"Комбинаций {SignalDetector.Types.Length * 2 * SignalDetector.Horizons.Length}: часть может оказаться в плюсе случайно. Найденное нужно проверить на новых днях, прежде чем строить на нём бота.",
            "Проскальзывание глубже лучшей цены и задержка исполнения больше 0,25 с не учтены: для крупных заявок и медленного подключения результат будет хуже.",
        ];
        return new(recordings.Count, Math.Round(hours, 2), events.Count, feePct, counts, rows.ToArray(), notes, fromMs);
    }

    // The hypothesis fixed on 16 October 2026 from the first 2.2 hours, before any of the data it is checked on existed: after a
    // forced price jerk (price spike or liquidation cascade) a trade with the move, held 5 minutes, makes money after 0.1% fees.
    // The verdict rule was fixed at the same time: each signal needs 50 episodes from the start moment; then it is confirmed when
    // the mean after fees is above zero on the whole and on both halves, otherwise rejected. Changing these after seeing the
    // result would make the check worthless.
    public static readonly long HypothesisFromMs = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.FromHours(3)).ToUnixTimeMilliseconds();
    public const int HypothesisEpisodes = 50;
    public const decimal HypothesisFee = 0.1m;
    private static readonly (string Type, string Mode, int Horizon)[] HypothesisSignals = [("Вынос цены", "follow", 300), ("Каскад ликвидаций", "follow", 300)];

    public HypothesisCheck Hypothesis()
    {
        var report = Report(HypothesisFee, HypothesisFromMs);
        var signals = HypothesisSignals.Select(h =>
        {
            var row = report.Rows.Single(r => r.Type == h.Type && r.Mode == h.Mode && r.Horizon == h.Horizon);
            var has = row.Episodes > 0;
            var verdict = row.Episodes < HypothesisEpisodes ? "collecting"
                : row.MeanPct > 0 && row.FirstHalf > 0 && row.SecondHalf > 0 ? "confirmed" : "rejected";
            return new HypothesisSignal(h.Type, h.Mode, h.Horizon, row.Episodes, HypothesisEpisodes, has ? row.MeanPct : null,
                has ? row.WinRate : null, row.FirstHalf, row.SecondHalf, verdict);
        }).ToArray();
        var verdict = signals.Any(s => s.Verdict == "confirmed") ? "confirmed"
            : signals.All(s => s.Verdict == "rejected") ? "rejected" : "collecting";
        var explanation = verdict switch
        {
            "confirmed" => "Хотя бы один сигнал прошёл проверку на новых данных. Следующий шаг — бот в живой симуляции с размером от риска; это ещё не доказательство прибыли на реальном счёте.",
            "rejected" => "Оба сигнала набрали нужные эпизоды и не прошли проверку: гипотеза не подтвердилась. Подгонять пороги под эти данные нельзя — нужна новая гипотеза и новые данные.",
            _ => $"Собираем данные: каждому сигналу нужно {HypothesisEpisodes} эпизодов с начала проверки. Промежуточный результат при малом числе эпизодов почти случаен — выводов по нему не делаем.",
        };
        return new("После выноса цены или каскада ликвидаций сделка по движению с удержанием 5 минут даёт плюс после комиссий 0,1%.",
            HypothesisFromMs, HypothesisFee, report.Hours, signals, verdict, explanation);
    }

    // Trades of one signal for the result chart: 100 USDT each, entry and exit at the recorded best prices.
    public BotTrade[] Trades(string type, string mode, int horizon, decimal feePct, long? fromMs = null)
    {
        var i = Array.IndexOf(SignalDetector.Horizons, horizon);
        if (!SignalDetector.Types.Contains(type) || mode is not ("follow" or "fade") || i < 0 || feePct is < 0 or > 1)
            throw new ArgumentException("Неизвестный сигнал, направление или время.");
        var withEvent = mode == "follow";
        return Load().SelectMany(r => r.Events).Where(e => e.Type == type && e.AtMs >= (fromMs ?? 0)).OrderBy(e => e.AtMs).Select((e, n) =>
        {
            if (e.Move(i, withEvent) is not { } move) return null;
            var longSide = (e.Direction > 0) == withEvent;
            var entry = longSide ? e.EntryAsk!.Value : e.EntryBid!.Value;
            var exit = longSide ? e.ExitBid![i]!.Value : e.ExitAsk![i]!.Value;
            var fee = feePct;
            return new BotTrade($"lab-{n}", DateTimeOffset.FromUnixTimeMilliseconds(e.AtMs + 250 + horizon * 1000L).ToString("O"),
                longSide ? "long" : "short", move, fee, entry, exit, 100 / entry, "lab", true, e.Symbol, "Binance",
                OpenedAt: DateTimeOffset.FromUnixTimeMilliseconds(e.AtMs + 250).ToString("O"), HoldSeconds: horizon, Notional: 100, ExitReason: e.Detail);
        }).OfType<BotTrade>().ToArray();
    }

    private bool disposed;
    public void Dispose()
    {
        // Under the lock, so a stop running at the same time never cancels a disposed source.
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            shutdown.Cancel();
            shutdown.Dispose();
        }
    }
}
