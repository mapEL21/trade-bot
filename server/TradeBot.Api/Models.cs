using System.Text.RegularExpressions;

namespace TradeBot.Api;

// Exchange is where the bot trades; for leader-lag the signal comes from SignalExchange, which must turn out to be the leader.
public sealed record LeaderLagParameters(string SignalExchange, ReplaySettings Settings, int DelayMs = 50, bool Counter = false);

public record BotConfig(string Name, string Exchange, string Symbol, decimal Budget,
    decimal LossLimit, string Strategy, string StrategyVersion, LeaderLagParameters? Parameters = null);

public sealed record Bot(string Id, string Name, string Exchange, string Symbol, decimal Budget,
    decimal LossLimit, string Strategy, string StrategyVersion, string CreatedAt, long Revision, LeaderLagParameters? Parameters = null)
    : BotConfig(Name, Exchange, Symbol, Budget, LossLimit, Strategy, StrategyVersion, Parameters)
{
    // draft, calibrating, running, paused, stopping, stopped or interrupted; filled from paper runs when the workspace is read.
    public string Status { get; init; } = "draft";
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
        "planned", true, false, ["Binance", "Bybit", "OKX"]),
        new("leader-lag", StrategyReplay.Version, "Поводырь → отстающая биржа",
        "Ищет биржу, которая раньше показывает движение цены, и входит на отстающей. Доступна живая симуляция: реальные заявки не отправляются.",
        "paper", true, true, ["Binance", "Bybit", "OKX", "Hyperliquid"])];

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
        if (config.Strategy == "leader-lag" && strategy is not null)
        {
            if (config.Parameters is not { } p) errors["parameters"] = ["Укажите параметры стратегии."];
            else
            {
                if (!strategy.Exchanges.Contains(p.SignalExchange) || p.SignalExchange == config.Exchange)
                    errors["signalExchange"] = ["Биржа сигнала должна быть другой поддерживаемой биржей."];
                try { (p.Settings ?? throw new ArgumentException("Укажите настройки стратегии.")).Validate(); }
                catch (ArgumentException e) { errors["settings"] = [e.Message]; }
                if (p.DelayMs is < 1 or > 1000) errors["delayMs"] = ["Задержка заявки — от 1 до 1000 мс."];
                if (p.Settings is { OrderQuantity: null } && p.Settings.OrderNotional > config.Budget)
                    errors["orderNotional"] = ["Номинал заявки не может превышать выделенный баланс."];
            }
        }
        else if (config.Parameters is not null) errors["parameters"] = ["У этой стратегии нет дополнительных параметров."];
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
