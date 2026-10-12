using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TradeBot.Api;

public sealed record StorageStatus(bool Enabled, DateTimeOffset? CheckedUtc, int Recordings, long RecordingBytes, int CompressedNow, int DeletedNow,
    int RetentionDays, long? FreeBytes, long? TotalBytes, string? LastBackup, string? Error);

// Keeps a machine that records around the clock from filling its disk: finished recordings are compressed (events.ndjson ->
// events.ndjson.gz, read transparently), recordings older than Storage:RetentionDays are deleted (0 keeps everything), and the
// workspace database is copied once a day into backups/ next to it (the last Storage:Backups copies are kept).
// Off unless Storage:Housekeeping is true: it is meant for the server, and must never touch a developer's or a test's data.
public sealed class StorageHousekeeper(IConfiguration configuration, IHostEnvironment environment, ILogger<StorageHousekeeper> logger)
    : BackgroundService
{
    public const string Events = "events.ndjson", Compressed = "events.ndjson.gz";
    private readonly object gate = new();
    private StorageStatus status = new(false, null, 0, 0, 0, 0, 0, null, null, null, null);
    private bool Enabled => configuration.GetValue<bool>("Storage:Housekeeping");
    public StorageStatus Status { get { lock (gate) return status; } }

    private string DataPath(string key, string folder) => Path.GetFullPath(configuration[key] ?? Path.Combine(environment.ContentRootPath, "data", folder));
    private string Database => Path.GetFullPath(configuration["Storage:Path"] ?? Path.Combine(environment.ContentRootPath, "data", "workspace.db"));
    private int RetentionDays => Math.Max(0, configuration.GetValue<int?>("Storage:RetentionDays") ?? 0);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        var interval = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue<int?>("Storage:HousekeepingMinutes") ?? 10, 1, 1440));
        // The first pass waits a minute so it never races the server's own start.
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
        catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Run(() => RunOnce(DateTimeOffset.UtcNow), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning(e, "Storage housekeeping failed"); lock (gate) status = status with { Error = e.Message }; }
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public StorageStatus RunOnce(DateTimeOffset now)
    {
        int recordings = 0, compressed = 0, deleted = 0; long bytes = 0;
        var compress = configuration.GetValue<bool?>("Storage:Compress") ?? true;
        var retention = RetentionDays;
        foreach (var root in new[] { DataPath("Market:Path", "market"), DataPath("Scanner:Path", "scans") }.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                var match = Regex.Match(name, "\\A([0-9]{8}T[0-9]{6}Z)-[a-f0-9]{32}\\z");
                // Only finished recordings are touched: summary.json is written after the last event.
                if (!match.Success || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                var finished = File.Exists(Path.Combine(directory, "summary.json"));
                var started = DateTimeOffset.ParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmss'Z'", null, System.Globalization.DateTimeStyles.AssumeUniversal);
                try
                {
                    if (finished && retention > 0 && started < now.AddDays(-retention))
                    {
                        Directory.Delete(directory, true); deleted++;
                        continue;
                    }
                    if (finished && compress && File.Exists(Path.Combine(directory, Events)) && !File.Exists(Path.Combine(directory, Compressed)))
                    {
                        Compress(directory); compressed++;
                    }
                }
                // A file in use (an analysis reading it) is left for the next pass.
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { logger.LogInformation(e, "Recording {Name} skipped", name); }
                recordings++;
                bytes += Directory.EnumerateFiles(directory).Sum(f => new FileInfo(f).Length);
            }
        }
        var backup = Backup(now);
        long? free = null, total = null;
        try { var drive = new DriveInfo(Path.GetPathRoot(Database)!); free = drive.AvailableFreeSpace; total = drive.TotalSize; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        lock (gate) return status = new(true, now, recordings, bytes, compressed, deleted, retention, free, total, backup ?? status.LastBackup, null);
    }

    private static void Compress(string directory)
    {
        var source = Path.Combine(directory, Events);
        var temporary = Path.Combine(directory, Compressed + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                input.CopyTo(gzip);
            File.Move(temporary, Path.Combine(directory, Compressed));
            File.Delete(source);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // A consistent copy of the live database (VACUUM INTO), at most once per Storage:BackupHours (24; 0 turns it off).
    private string? Backup(DateTimeOffset now)
    {
        var hours = configuration.GetValue<int?>("Storage:BackupHours") ?? 24;
        if (hours <= 0 || !File.Exists(Database)) return null;
        var folder = Path.Combine(Path.GetDirectoryName(Database)!, "backups");
        Directory.CreateDirectory(folder);
        var existing = Directory.GetFiles(folder, "workspace-*.db").Order(StringComparer.Ordinal).ToList();
        if (existing.Count > 0 && File.GetLastWriteTimeUtc(existing[^1]) > now.UtcDateTime.AddHours(-hours)) return null;
        var target = Path.Combine(folder, $"workspace-{now:yyyyMMdd-HHmmss}.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $target";
            command.Parameters.AddWithValue("$target", target);
            command.ExecuteNonQuery();
        }
        existing.Add(target);
        var keep = Math.Max(1, configuration.GetValue<int?>("Storage:Backups") ?? 7);
        foreach (var old in existing.Take(Math.Max(0, existing.Count - keep))) File.Delete(old);
        return Path.GetFileName(target);
    }
}
