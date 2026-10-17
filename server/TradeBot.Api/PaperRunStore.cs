using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TradeBot.Api;

// Signal bot runs in the workspace database (table signal_runs). The name stays from the removed paper simulation: test
// factories replace this store with a temporary database, so every run a test makes stays out of the real one.
public sealed class PaperRunStore
{
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PaperRunStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS signal_runs (id TEXT PRIMARY KEY, bot_id TEXT NOT NULL, started TEXT NOT NULL, body TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS signal_runs_bot ON signal_runs (bot_id, started);
                """;
            create.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public async Task SaveSignalRunAsync(SignalRun run, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO signal_runs (id, bot_id, started, body) VALUES ($id, $bot, $started, $body)
                ON CONFLICT(id) DO UPDATE SET body = excluded.body;
                """;
            command.Parameters.AddWithValue("$id", run.Id);
            command.Parameters.AddWithValue("$bot", run.BotId);
            command.Parameters.AddWithValue("$started", run.StartedUtc.ToString("O"));
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(run, Json));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { gate.Release(); }
    }

    // Newest first; botId null lists every bot's runs.
    public async Task<SignalRun[]> SignalRunsAsync(string? botId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = botId is null ? "SELECT body FROM signal_runs ORDER BY started DESC"
                : "SELECT body FROM signal_runs WHERE bot_id = $bot ORDER BY started DESC";
            if (botId is not null) command.Parameters.AddWithValue("$bot", botId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<SignalRun>();
            while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<SignalRun>(reader.GetString(0), Json)!);
            return result.ToArray();
        }
        finally { gate.Release(); }
    }
}
