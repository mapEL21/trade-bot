using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TradeBot.Api;

public sealed class SqliteWorkspaceStore : IWorkspaceStore, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SqliteWorkspaceStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "PRAGMA user_version";
        var currentVersion = Convert.ToInt32(version.ExecuteScalar());
        if (currentVersion > 1) throw new InvalidOperationException("Database schema is newer than this application.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS bots (id TEXT PRIMARY KEY, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events (sequence INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT NOT NULL);
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public async Task<Workspace> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            var bots = await ReadRowsAsync<Bot>(connection, transaction, "SELECT body FROM bots ORDER BY rowid", cancellationToken);
            var events = await ReadRowsAsync<WorkspaceEvent>(connection, transaction,
                "SELECT body FROM events ORDER BY sequence DESC LIMIT 200", cancellationToken);
            transaction.Commit();
            return new Workspace(1, bots, events);
        }
        finally { _gate.Release(); }
    }

    public Task<StoreResult> CreateAsync(BotConfig config, CancellationToken cancellationToken) =>
        MutateAsync(null, 0, config, cancellationToken);

    public Task<StoreResult> UpdateAsync(string id, long revision, BotConfig config, CancellationToken cancellationToken) =>
        MutateAsync(id, revision, config, cancellationToken);

    public Task<StoreResult> DeleteAsync(string id, long revision, CancellationToken cancellationToken) =>
        MutateAsync(id, revision, null, cancellationToken);

    private async Task<StoreResult> MutateAsync(string? id, long revision, BotConfig? config, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            Bot? existing = null;
            if (id is not null)
            {
                using var lookup = connection.CreateCommand();
                lookup.Transaction = transaction;
                lookup.CommandText = "SELECT body FROM bots WHERE id = $id";
                lookup.Parameters.AddWithValue("$id", id);
                var body = await lookup.ExecuteScalarAsync(cancellationToken) as string;
                if (body is null) return new(StoreStatus.Missing);
                existing = JsonSerializer.Deserialize<Bot>(body, Json)!;
                if (existing.Revision != revision) return new(StoreStatus.Conflict);
            }
            else
            {
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT count(*) FROM bots";
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) >= 100)
                    return new(StoreStatus.Limit);
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            Bot? bot = config is null ? null : new(
                id ?? Guid.NewGuid().ToString(), config.Name.Trim(), config.Exchange, config.Symbol,
                config.Budget, config.LossLimit, config.Strategy, config.StrategyVersion,
                existing?.CreatedAt ?? now, (existing?.Revision ?? 0) + 1);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", bot?.Id ?? id!);
            if (bot is null) command.CommandText = "DELETE FROM bots WHERE id = $id";
            else
            {
                command.CommandText = "INSERT INTO bots (id, body) VALUES ($id, $body) ON CONFLICT(id) DO UPDATE SET body = excluded.body";
                command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(bot, Json));
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
            var entry = new WorkspaceEvent(Guid.NewGuid().ToString(), now, bot?.Id ?? id!, bot?.Name ?? existing!.Name,
                bot is null ? "Экземпляр бота удалён." : existing is null
                    ? "Создан экземпляр стратегии. Запуск недоступен: алгоритм ещё не реализован."
                    : "Настройки экземпляра обновлены.");
            using var journal = connection.CreateCommand();
            journal.Transaction = transaction;
            journal.CommandText = """
                INSERT INTO events (body) VALUES ($body);
                DELETE FROM events WHERE sequence NOT IN (SELECT sequence FROM events ORDER BY sequence DESC LIMIT 200);
                """;
            journal.Parameters.AddWithValue("$body", JsonSerializer.Serialize(entry, Json));
            await journal.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            return new(StoreStatus.Saved, bot);
        }
        finally { _gate.Release(); }
    }

    private static async Task<T[]> ReadRowsAsync<T>(SqliteConnection connection, SqliteTransaction transaction,
        string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return result.ToArray();
    }

    public void Dispose() => _gate.Dispose();
}
