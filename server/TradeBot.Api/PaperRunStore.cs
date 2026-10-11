using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TradeBot.Api;

// One live paper run as kept in the workspace database; TradeLog keeps the first 500 closed trades, Journal the last 200 decisions.
public sealed record PaperRunRecord(string Id, string? BotId, string? BotName, string Version, DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc, string State, string Explanation, string Coin, string SignalExchange, string Exchange,
    ReplaySettings Settings, int DelayMs, bool Counter, string? Leader, string? ExecutionExchange, string? RecordingDirectory,
    int Trades, decimal SumNetBps, decimal? ClosedNetQuote, int Unresolved, int LateEvents, string? EntriesBlocked,
    ReplayTrade[] TradeLog, ReplayDecision[] Journal);

public sealed class PaperRunStore
{
    public static readonly string[] ActiveStates = ["starting", "calibrating", "running"];
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
                CREATE TABLE IF NOT EXISTS paper_runs (id TEXT PRIMARY KEY, bot_id TEXT, started TEXT NOT NULL, body TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS paper_runs_bot ON paper_runs (bot_id, started);
                """;
            create.ExecuteNonQuery();
        }
        // A run still active in the database was cut off by a restart: it is never resumed and nothing is closed for it.
        var interrupted = new List<PaperRunRecord>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT body FROM paper_runs";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                if (JsonSerializer.Deserialize<PaperRunRecord>(reader.GetString(0), Json) is { } run && ActiveStates.Contains(run.State))
                    interrupted.Add(run with
                    {
                        State = "interrupted", EndedUtc = run.EndedUtc ?? DateTimeOffset.UtcNow,
                        Explanation = "Приложение было остановлено во время симуляции. Запуск не продолжен; открытая виртуальная позиция или заявка, если была, не оценена."
                    });
        }
        foreach (var run in interrupted) Upsert(connection, transaction, run);
        transaction.Commit();
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, PaperRunRecord run)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO paper_runs (id, bot_id, started, body) VALUES ($id, $bot, $started, $body)
            ON CONFLICT(id) DO UPDATE SET body = excluded.body;
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$bot", (object?)run.BotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", run.StartedUtc.ToString("O"));
        command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(run, Json));
        command.ExecuteNonQuery();
    }

    public async Task SaveAsync(PaperRunRecord run, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            Upsert(connection, transaction, run);
            transaction.Commit();
        }
        finally { gate.Release(); }
    }

    // Newest first; botId null lists runs started without a bot.
    public Task<PaperRunRecord[]> ListAsync(string? botId, int limit, CancellationToken ct) =>
        QueryAsync(botId is null ? "SELECT body FROM paper_runs WHERE bot_id IS NULL ORDER BY started DESC LIMIT $limit"
            : "SELECT body FROM paper_runs WHERE bot_id = $bot ORDER BY started DESC LIMIT $limit", botId, limit, ct);

    public async Task<Dictionary<string, PaperRunRecord>> LatestByBotAsync(CancellationToken ct) =>
        (await QueryAsync("""
            SELECT body FROM paper_runs p WHERE bot_id IS NOT NULL
            AND started = (SELECT max(started) FROM paper_runs q WHERE q.bot_id = p.bot_id)
            """, null, 0, ct)).GroupBy(x => x.BotId!).ToDictionary(x => x.Key, x => x.First());

    private async Task<PaperRunRecord[]> QueryAsync(string sql, string? botId, int limit, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (botId is not null) command.Parameters.AddWithValue("$bot", botId);
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<PaperRunRecord>();
            while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<PaperRunRecord>(reader.GetString(0), Json)!);
            return result.ToArray();
        }
        finally { gate.Release(); }
    }
}
