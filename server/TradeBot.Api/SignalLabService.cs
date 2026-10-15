using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeBot.Api;

public sealed record LabStatus(string State, string Explanation, int Done, int Total, long Lines, string? Current);
public sealed record LabRow(string Type, string Mode, int Horizon, int Count, decimal MeanPct, decimal WinRate, decimal? FirstHalf,
    decimal? SecondHalf, bool Stable);
public sealed record LabCount(string Type, int Count, decimal PerHour);
public sealed record LabReport(int Recordings, decimal Hours, int Events, decimal FeePct, LabCount[] Counts, LabRow[] Rows, string[] Notes);

// The signal lab: runs every finished scanner recording through SignalDetector once (the result is kept next to the recording
// as signal-lab-v1.json) and then sums up, for every event type, both trading directions and every horizon, what the trade
// would have made after fees — overall and separately on the earlier and later half of the events.
public sealed class SignalLab(IConfiguration configuration, IHostEnvironment environment, ILogger<SignalLab> logger) : IDisposable
{
    public const string File = "signal-lab-v1.json";
    public const int MinCount = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private LabStatus status = new("idle", "Записи ещё не обрабатывались.", 0, 0, 0, null);
    private Task? task;

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
                var result = SignalDetector.Run(directory, shutdown.Token, lines => { lock (gate) status = status with { Lines = lines }; });
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

    private List<LabRecording> Load() => Recordings().Select(Cached).OfType<LabRecording>().ToList();

    public LabReport Report(decimal feePct)
    {
        if (feePct is < 0 or > 1) throw new ArgumentException("Комиссии круга — от 0 до 1%.");
        var recordings = Load();
        var events = recordings.SelectMany(r => r.Events).OrderBy(e => e.AtMs).ToList();
        var hours = recordings.Sum(r => Math.Max(0, r.LastMs - r.FirstMs)) / 3_600_000m;
        var rows = new List<LabRow>();
        foreach (var type in SignalDetector.Types)
            foreach (var withEvent in new[] { true, false })
                for (var i = 0; i < SignalDetector.Horizons.Length; i++)
                {
                    var values = events.Where(e => e.Type == type).Select(e => e.Move(i, withEvent)).OfType<decimal>().Select(v => v - feePct).ToList();
                    if (values.Count == 0) { rows.Add(new(type, withEvent ? "follow" : "fade", SignalDetector.Horizons[i], 0, 0, 0, null, null, false)); continue; }
                    var half = values.Count / 2;
                    decimal? Mean(IEnumerable<decimal> v) => v.Any() ? Math.Round(v.Average(), 4) : null;
                    var first = Mean(values.Take(half)); var second = Mean(values.Skip(half));
                    rows.Add(new(type, withEvent ? "follow" : "fade", SignalDetector.Horizons[i], values.Count, Math.Round(values.Average(), 4),
                        Math.Round(values.Count(v => v > 0) * 100m / values.Count, 1), first, second,
                        values.Count >= MinCount && first > 0 && second > 0));
                }
        var counts = SignalDetector.Types.Select(t => { var n = events.Count(e => e.Type == t); return new LabCount(t, n, hours > 0 ? Math.Round(n / hours, 1) : 0); }).ToArray();
        string[] notes =
        [
            "Вход через 0,25 с после события по встречной цене стакана (покупка по ask, продажа по bid), выход через выбранное время тоже по встречной цене: спред уже учтён. Из результата вычтены комиссии круга.",
            "«По движению» — сделка в сторону события, «против» — в обратную. Половины — первые и вторые 50% событий по времени; «устойчиво» — от " + MinCount + " событий и плюс на обеих половинах.",
            $"Комбинаций {SignalDetector.Types.Length * 2 * SignalDetector.Horizons.Length}: часть может оказаться в плюсе случайно. Найденное нужно проверить на новых днях, прежде чем строить на нём бота.",
            "Проскальзывание глубже лучшей цены и задержка исполнения больше 0,25 с не учтены: для крупных заявок и медленного подключения результат будет хуже.",
        ];
        return new(recordings.Count, Math.Round(hours, 2), events.Count, feePct, counts, rows.ToArray(), notes);
    }

    // Trades of one signal for the result chart: 100 USDT each, entry and exit at the recorded best prices.
    public BotTrade[] Trades(string type, string mode, int horizon, decimal feePct)
    {
        var i = Array.IndexOf(SignalDetector.Horizons, horizon);
        if (!SignalDetector.Types.Contains(type) || mode is not ("follow" or "fade") || i < 0 || feePct is < 0 or > 1)
            throw new ArgumentException("Неизвестный сигнал, направление или время.");
        var withEvent = mode == "follow";
        return Load().SelectMany(r => r.Events).Where(e => e.Type == type).OrderBy(e => e.AtMs).Select((e, n) =>
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

    public void Dispose() { shutdown.Cancel(); shutdown.Dispose(); }
}
