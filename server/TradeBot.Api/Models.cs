using System.Text.RegularExpressions;

namespace TradeBot.Api;

public record BotConfig(string Name, string Exchange, string Symbol, decimal Budget,
    decimal LossLimit, string Strategy, string StrategyVersion);

public sealed record Bot(string Id, string Name, string Exchange, string Symbol, decimal Budget,
    decimal LossLimit, string Strategy, string StrategyVersion, string CreatedAt, long Revision)
    : BotConfig(Name, Exchange, Symbol, Budget, LossLimit, Strategy, StrategyVersion)
{
    public string Status => "draft";
    public object[] Trades => [];
}

public sealed record WorkspaceEvent(string Id, string At, string BotId, string BotName, string Text);
public sealed record Workspace(int SchemaVersion, Bot[] Bots, WorkspaceEvent[] Events);
public sealed record StrategyDefinition(string Id, string Version, string Name, string Description,
    string ImplementationStatus, bool CanCreate, bool CanRun, string[] Exchanges);

// Catalog metadata is deliberately separate from executable strategies.
public sealed class StrategyCatalog
{
    public IReadOnlyList<StrategyDefinition> Items { get; } = [new(
        "impulse", "0.1.0", "Импульс",
        "Планируемая стратегия краткосрочных ценовых импульсов. Правила входа и выхода ещё не реализованы.",
        "planned", true, false, ["Binance", "Bybit", "OKX"])];

    public Dictionary<string, string[]> Validate(BotConfig config)
    {
        var errors = new Dictionary<string, string[]>();
        var strategy = Items.FirstOrDefault(x => x.Id == config.Strategy && x.Version == config.StrategyVersion);
        if (string.IsNullOrWhiteSpace(config.Name) || config.Name.Trim().Length > 40)
            errors["name"] = ["Название должно содержать от 1 до 40 символов."];
        if (strategy is null || !strategy.CanCreate)
            errors["strategy"] = ["Эта версия стратегии не зарегистрирована на сервере."];
        if (strategy is not null && !strategy.Exchanges.Contains(config.Exchange))
            errors["exchange"] = ["Выберите Binance, Bybit или OKX."];
        if (config.Symbol is null || !Regex.IsMatch(config.Symbol, @"\A[A-Z0-9]{2,16}USDT\z"))
            errors["symbol"] = ["Укажите символ USDT-фьючерса, например ARBUSDT."];
        if (config.Budget < 1 || config.Budget > 1_000_000)
            errors["budget"] = ["Выделенный баланс должен быть от 1 до 1 000 000 USDT."];
        if (config.LossLimit <= 0 || config.LossLimit > config.Budget)
            errors["lossLimit"] = ["Лимит убытка должен быть больше нуля и не больше баланса."];
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
