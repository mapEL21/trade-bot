using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

// Disk upkeep for a machine that records around the clock: compression, retention and database copies.
public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "storage-tests-" + Guid.NewGuid());
    private const string Id = "20261010T100000Z-11111111111111111111111111111111";

    private async Task Seed()
    {
        var dir = Path.Combine(root, Id); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(dir, "summary.json"), "{\"status\":\"completed\"}");
        await File.WriteAllLinesAsync(Path.Combine(dir, StorageHousekeeper.Events),
            Enumerable.Range(0, 50).Select(i => JsonSerializer.Serialize(new CaptureEvent("binance-market", "c", "market", DateTimeOffset.UnixEpoch, i, "{}"))));
    }

    private StorageHousekeeper Housekeeper(int retentionDays = 0) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Market:Path"] = root, ["Scanner:Path"] = Path.Combine(root, "scans"), ["Storage:Path"] = Path.Combine(root, "workspace.db"),
        ["Storage:Housekeeping"] = "true", ["Storage:RetentionDays"] = retentionDays.ToString(), ["Storage:Backups"] = "2"
    }).Build(), new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root },
        Microsoft.Extensions.Logging.Abstractions.NullLogger<StorageHousekeeper>.Instance);

    [Fact]
    public async Task FinishedRecordingIsCompressedWithoutLosingALine()
    {
        await Seed();
        var original = await File.ReadAllTextAsync(Path.Combine(root, Id, StorageHousekeeper.Events));
        var status = Housekeeper().RunOnce(DateTimeOffset.UtcNow);
        Assert.Equal((1, 1, true), (status.Recordings, status.CompressedNow, status.Enabled));
        Assert.False(File.Exists(Path.Combine(root, Id, StorageHousekeeper.Events)));
        using var gzip = new GZipStream(File.OpenRead(Path.Combine(root, Id, StorageHousekeeper.Compressed)), CompressionMode.Decompress);
        Assert.Equal(original, await new StreamReader(gzip).ReadToEndAsync());
    }

    [Fact]
    public async Task HousekeepingDeletesOnlyOldFinishedRecordingsAndBacksUpTheDatabaseOncePerDay()
    {
        await Seed();
        // An old finished scan, an old recording still being written, and today's recording (from Seed).
        string Make(string id, bool finished)
        {
            var dir = Path.Combine(root, "scans", id); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, StorageHousekeeper.Events), "{}");
            if (finished) File.WriteAllText(Path.Combine(dir, "summary.json"), "{}");
            return dir;
        }
        var old = Make("20260101T000000Z-22222222222222222222222222222222", true);
        var writing = Make("20260101T000000Z-33333333333333333333333333333333", false);
        new PaperRunStore(Path.Combine(root, "workspace.db"));
        var now = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        var housekeeper = Housekeeper(retentionDays: 14);
        var first = housekeeper.RunOnce(now);
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(Path.Combine(writing, StorageHousekeeper.Events)), "a recording still being written is never touched");
        Assert.True(Directory.Exists(Path.Combine(root, Id)));
        Assert.Equal(1, first.DeletedNow);
        Assert.NotNull(first.LastBackup);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "workspace-*.db"));
        // Within a day no second copy is made; later ones keep only the last two.
        housekeeper.RunOnce(now.AddHours(1));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "backups"), "workspace-*.db"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.Combine(root, "backups"))) File.SetLastWriteTimeUtc(file, now.UtcDateTime.AddDays(-2));
        housekeeper.RunOnce(now.AddDays(1));
        foreach (var file in Directory.GetFiles(Path.Combine(root, "backups"))) File.SetLastWriteTimeUtc(file, now.UtcDateTime.AddDays(-2));
        housekeeper.RunOnce(now.AddDays(2));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "backups"), "workspace-*.db").Length);
    }

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
