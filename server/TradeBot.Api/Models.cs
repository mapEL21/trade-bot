using System.Text.RegularExpressions;

namespace TradeBot.Api;

// Exchange is where the bot trades; for leader-lag the signal comes from SignalExchange, which must turn out to be the leader.
// Leverage only changes the margin an order needs (nominal / leverage) and how close a loss may come to that margin.
public sealed record LeaderLagParameters(string SignalExchange, ReplaySettings Settings, int DelayMs = 50, bool Counter = false,
    int Leverage = 1)
{
    public const int MaxLeverage = 125;
}

// Signal: settings of a "signal" bot, which trades every coin of the data recording; its Symbol is "*".
public record BotConfig(string Name, string Exchange, string Symbol, decimal Budget,
    decimal LossLimit, string Strategy, string StrategyVersion, LeaderLagParameters? Parameters = null, SignalParameters? Signal = null);

public sealed record Bot(string Id, string Name, string Exchange, string Symbol, decimal Budget,
    decimal LossLimit, string Strategy, string StrategyVersion, string CreatedAt, long Revision, LeaderLagParameters? Parameters = null,
    SignalParameters? Signal = null)
    : BotConfig(Name, Exchange, Symbol, Budget, LossLimit, Strategy, StrategyVersion, Parameters, Signal)
{
    // draft, calibrating, running, paused, stopping, stopped or interrupted; filled from paper runs when the workspace is read.
    public string Status { get; init; } = "draft";
    // Closed trades of the bot's paper runs, filled when the workspace is read; never real executions.
    public BotTrade[] Trades { get; init; } = [];
    // Result of the open simulated position if it were closed now through the book, after both fees; null without one.
    public decimal? Unrealized { get; init; }
}

// One closed paper trade in USDT, in the shape the workspace UI uses for statistics and charts.
// A closed trade in the form shared by every strategy: what was traded where, when, for how long and at what size.
// The instrument and exchanges come from the run that made the trade, never from the bot's current settings, so
// editing a bot does not rewrite its history. Leader is strategy-specific and stays null for strategies without one.
public sealed record BotTrade(string Id, string At, string Side, decimal Gross, decimal Fee, decimal Entry, decimal Exit,
    decimal Quantity, string RunId, bool Simulated = true, string? Symbol = null, string? Exchange = null,
    string? Leader = null, string? OpenedAt = null, double? HoldSeconds = null, decimal? Notional = null,
    string? ExitReason = null, decimal? MaxFavorableBps = null, decimal? MaxAdverseBps = null)
{
    static readonly Dictionary<string, string> ExchangeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["binance"] = "Binance", ["bybit"] = "Bybit", ["okx"] = "OKX", ["hyperliquid"] = "Hyperliquid",
    };

    public static string? ExchangeName(string? id) => id is null ? null : ExchangeNames.GetValueOrDefault(id, id);

    // Exit time is the run start plus the trade's session time; fees and size come from the depth model, or follow
    // from the configured size and fees in the simplified BBO model.
    public static IEnumerable<BotTrade> FromRun(PaperRunRecord run)
    {
        var s = run.Settings;
        var symbol = run.Coin.ToUpperInvariant() + "USDT";
        var exchange = ExchangeName(run.ExecutionExchange ?? run.Exchange);
        var leader = ExchangeName(run.Leader);
        return run.TradeLog.Select((t, i) =>
        {
            var notional = s.OrderQuantity is { } coins ? coins * t.EntryPrice : s.OrderNotional;
            var quantity = t.Quantity ?? notional / t.EntryPrice;
            var entryFee = s.EntryOrder == "maker" ? s.MakerFeeBps : s.FeeBps;
            var fee = t.FeesQuote ?? quantity * (t.EntryPrice * entryFee + t.ExitPrice * s.FeeBps) / 10000;
            var net = t.NetQuote ?? t.NetBps / 10000 * quantity * t.EntryPrice;
            return new BotTrade($"{run.Id}-{i}", run.StartedUtc.AddSeconds(t.ExitSeconds).ToString("O"), t.Side, net + fee, fee,
                t.EntryPrice, t.ExitPrice, quantity, run.Id, Symbol: symbol, Exchange: exchange, Leader: leader,
                OpenedAt: run.StartedUtc.AddSeconds(t.EntrySeconds).ToString("O"),
                HoldSeconds: Math.Max(0, t.ExitSeconds - t.EntrySeconds), Notional: quantity * t.EntryPrice,
                ExitReason: t.ExitReason, MaxFavorableBps: t.MaxFavorableBps, MaxAdverseBps: t.MaxAdverseBps);
        });
    }
}

public sealed record WorkspaceEvent(string Id, string At, string BotId, string BotName, string Text);
public sealed record Workspace(int SchemaVersion, Bot[] Bots, WorkspaceEvent[] Events);
// Retired: kept so existing bots and their history stay valid, but no longer offered for new bots.
public sealed record StrategyDefinition(string Id, string Version, string Name, string Description,
    string ImplementationStatus, bool CanCreate, bool CanRun, string[] Exchanges, bool Retired = false);

// Catalog metadata is deliberately separate from executable strategies.
public sealed class StrategyCatalog
{
    public IReadOnlyList<StrategyDefinition> Items { get; } = [new(
        "impulse", "0.1.0", "Импульс",
        "Планируемая стратегия краткосрочных ценовых импульсов. Правила входа и выхода ещё не реализованы.",
        "planned", true, false, ["Binance", "Bybit", "OKX"], Retired: true),
        new("leader-lag", StrategyReplay.Version, "Поводырь → отстающая биржа",
        "Ищет биржу, которая раньше показывает движение цены, и входит на отстающей. Отставание оказалось меньше комиссий; новых ботов не создаём.",
        "paper", true, true, ["Binance", "Bybit", "OKX", "Hyperliquid"], Retired: true),
        new(SignalId, SignalVersion, "Сигнал лаборатории",
        "Торгует выбранный сигнал лаборатории на всех монетах записи данных. Размер позиции — от риска на сделку с учётом комиссий. Симуляция по записям, реальные заявки не отправляются.",
        "paper", true, true, ["Binance"])];
    public const string SignalId = "signal", SignalVersion = "0.1.0";

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
                if (p.Leverage is < 1 or > LeaderLagParameters.MaxLeverage)
                    errors["leverage"] = [$"Плечо — целое число от 1 до {LeaderLagParameters.MaxLeverage}."];
                else if (p.Settings is { OrderQuantity: null } && p.Settings.OrderNotional > config.Budget * p.Leverage)
                    errors["orderNotional"] = ["Номинал заявки не может превышать выделенный баланс, умноженный на плечо."];
            }
        }
        else if (config.Parameters is not null) errors["parameters"] = ["У этой стратегии нет дополнительных параметров."];
        if (config.Strategy == SignalId)
        {
            if (config.Signal is not { } signal) errors["signal"] = ["Укажите настройки сигнала."];
            else foreach (var (key, value) in signal.Validate()) errors[key] = value;
            if (config.Symbol != "*") errors["symbol"] = ["Бот сигнала торгует все монеты записи данных."];
        }
        else if (config.Signal is not null) errors["signal"] = ["Настройки сигнала есть только у бота сигнала."];
        // The same tickers the common pair list offers (1-20 characters), so every listed pair can be saved.
        if (config.Strategy != SignalId && (config.Symbol is null || !Regex.IsMatch(config.Symbol, @"\A[A-Z0-9]{1,20}USDT\z")))
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
