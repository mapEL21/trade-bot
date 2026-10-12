using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TradeBot.Api;

// A saved set of bot settings without the bot's name, exchanges or pair, so one template fits any pair.
public sealed record BotTemplateInput(string Name, string Strategy, decimal Budget, decimal LossLimit, int Leverage, int DelayMs,
    bool Counter, ReplaySettings Settings);
public sealed record BotTemplate(string Id, string Name, string Strategy, decimal Budget, decimal LossLimit, int Leverage, int DelayMs,
    bool Counter, ReplaySettings Settings, string SavedAt);

public sealed class BotTemplateStore
{
    public const int MaxTemplates = 50;
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BotTemplateStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS bot_templates (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE, body TEXT NOT NULL);";
        create.ExecuteNonQuery();
    }

    // The same checks as a bot with these settings; exchanges and pair are placeholders, they are not part of a template.
    public static Dictionary<string, string[]> Validate(BotTemplateInput input, StrategyCatalog catalog)
    {
        if (input.Strategy != "leader-lag")
            return new() { ["strategy"] = ["Шаблоны пока есть только у стратегии «Поводырь»."] };
        var errors = catalog.Validate(new BotConfig(input.Name, "Binance", "BTCUSDT", input.Budget, input.LossLimit, "leader-lag",
            StrategyReplay.Version, new LeaderLagParameters("OKX", input.Settings, input.DelayMs, input.Counter, input.Leverage)));
        errors.Remove("exchange"); errors.Remove("signalExchange"); errors.Remove("symbol");
        return errors;
    }

    public async Task<BotTemplate[]> ListAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT body FROM bot_templates ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<BotTemplate>();
            while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<BotTemplate>(reader.GetString(0), Json)!);
            return result.ToArray();
        }
        finally { gate.Release(); }
    }

    // Saving under an existing name replaces that template; null when the limit of templates is reached.
    public async Task<BotTemplate?> SaveAsync(BotTemplateInput input, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            var name = input.Name.Trim();
            string? id;
            using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT id FROM bot_templates WHERE name = $name";
                find.Parameters.AddWithValue("$name", name);
                id = (string?)await find.ExecuteScalarAsync(ct);
            }
            if (id is null)
            {
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT count(*) FROM bot_templates";
                if ((long)(await count.ExecuteScalarAsync(ct))! >= MaxTemplates) return null;
            }
            var template = new BotTemplate(id ?? Guid.NewGuid().ToString(), name, input.Strategy, input.Budget, input.LossLimit, input.Leverage,
                input.DelayMs, input.Counter, input.Settings, DateTimeOffset.UtcNow.ToString("O"));
            using (var save = connection.CreateCommand())
            {
                save.Transaction = transaction;
                save.CommandText = """
                    INSERT INTO bot_templates (id, name, body) VALUES ($id, $name, $body)
                    ON CONFLICT(id) DO UPDATE SET body = excluded.body;
                    """;
                save.Parameters.AddWithValue("$id", template.Id);
                save.Parameters.AddWithValue("$name", name);
                save.Parameters.AddWithValue("$body", JsonSerializer.Serialize(template, Json));
                await save.ExecuteNonQueryAsync(ct);
            }
            transaction.Commit();
            return template;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM bot_templates WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }
        finally { gate.Release(); }
    }
}
