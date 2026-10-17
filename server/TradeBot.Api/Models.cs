namespace TradeBot.Api;

// A bot trades one signal of the lab on every coin of the data recording; its Symbol is "*" and its settings are in Signal.
public record BotConfig(string Name, string Exchange, string Symbol, decimal Budget,
    decimal LossLimit, string Strategy, string StrategyVersion, SignalParameters? Signal = null);

public sealed record Bot(string Id, string Name, string Exchange, string Symbol, decimal Budget,
    decimal LossLimit, string Strategy, string StrategyVersion, string CreatedAt, long Revision, SignalParameters? Signal = null)
    : BotConfig(Name, Exchange, Symbol, Budget, LossLimit, Strategy, StrategyVersion, Signal)
{
    // draft, running or stopped; filled from the bot's runs when the workspace is read.
    public string Status { get; init; } = "draft";
    // Closed trades of the bot's runs, filled when the workspace is read; never real executions.
    public BotTrade[] Trades { get; init; } = [];
    // Result of an open position if it were closed now, after fees; null without one.
    public decimal? Unrealized { get; init; }
}

// A closed trade in USDT, in the form every page uses for statistics and charts: what was traded where, when, for how
// long and at what size. The instrument comes from the run that made the trade, never from the bot's current settings.
public sealed record BotTrade(string Id, string At, string Side, decimal Gross, decimal Fee, decimal Entry, decimal Exit,
    decimal Quantity, string RunId, bool Simulated = true, string? Symbol = null, string? Exchange = null,
    string? OpenedAt = null, double? HoldSeconds = null, decimal? Notional = null,
    string? ExitReason = null, decimal? MaxFavorableBps = null, decimal? MaxAdverseBps = null, string? EntryReason = null);

public sealed record WorkspaceEvent(string Id, string At, string BotId, string BotName, string Text);
public sealed record Workspace(int SchemaVersion, Bot[] Bots, WorkspaceEvent[] Events);
public sealed record StrategyDefinition(string Id, string Version, string Name, string Description,
    string ImplementationStatus, bool CanCreate, bool CanRun, string[] Exchanges);

// The strategies a bot can run. Bots of strategies removed from the application (leader-lag, impulse) stay in the database
// but are neither shown nor accepted.
public sealed class StrategyCatalog
{
    // 0.2.0: entries and exits by the strategy's own logic (docs/BOT_LOGIC.md); 0.1.0 had a stop and a holding time in the form.
    public const string SignalId = "signal", SignalVersion = "0.2.0";
    public IReadOnlyList<StrategyDefinition> Items { get; } =
    [
        new(SignalId, SignalVersion, "Сигнал лаборатории",
            "Торгует плотности или вынос цены на всех монетах записи данных: вход и выход по логике стратегии, размер — от риска на сделку с учётом комиссий.",
            "paper", true, true, ["Binance"]),
    ];

    public bool Known(string strategy) => Items.Any(x => x.Id == strategy);

    public Dictionary<string, string[]> Validate(BotConfig config)
    {
        var errors = new Dictionary<string, string[]>();
        var strategy = Items.FirstOrDefault(x => x.Id == config.Strategy && x.Version == config.StrategyVersion);
        if (string.IsNullOrWhiteSpace(config.Name) || config.Name.Trim().Length > 40)
            errors["name"] = ["Название должно содержать от 1 до 40 символов."];
        if (strategy is null || !strategy.CanCreate)
            errors["strategy"] = ["Эта версия стратегии не зарегистрирована на сервере."];
        if (strategy is not null && !strategy.Exchanges.Contains(config.Exchange))
            errors["exchange"] = [$"Выберите {string.Join(", ", strategy.Exchanges)}."];
        if (config.Signal is not { } signal) errors["signal"] = ["Укажите настройки сигнала."];
        else foreach (var (key, value) in signal.Validate()) errors[key] = value;
        if (config.Symbol != "*") errors["symbol"] = ["Бот торгует все монеты записи данных."];
        if (config.Budget < 1 || config.Budget > 1_000_000)
            errors["budget"] = ["Депозит бота должен быть от 1 до 1 000 000 USDT."];
        if (config.LossLimit <= 0 || config.LossLimit > config.Budget)
            errors["lossLimit"] = ["Лимит убытка должен быть больше нуля и не больше депозита."];
        return errors;
    }
}

public enum StoreStatus { Saved, Missing, Conflict, Limit }
public sealed record StoreResult(StoreStatus Status, Bot? Bot = null);

public interface IWorkspaceStore
{
    Task<Workspace> ReadAsync(CancellationToken cancellationToken);
    Task<StoreResult> CreateAsync(BotConfig config, CancellationToken cancellationToken);
    Task<StoreResult> UpdateAsync(string id, long revision, BotConfig config, CancellationToken cancellationToken);
    Task<StoreResult> DeleteAsync(string id, long revision, CancellationToken cancellationToken);
}
