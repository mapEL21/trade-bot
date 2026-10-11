using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record ResearchMarketList(string Exchange, DateTimeOffset UpdatedUtc, MarketInstrument[] Instruments, string SourceExchange = "hyperliquid");
// Order size rules of one perpetual, in coins, plus the last price to convert sizes for the form; not a trading quote.
public sealed record InstrumentInfo(string Exchange, string Coin, decimal QuantityStep, decimal MinimumQuantity, decimal MinimumNotional,
    decimal? MaximumQuantity, decimal? LastPrice, decimal? MinimumOrderNotional, string QuoteCurrency, string RuleSource, DateTimeOffset UpdatedUtc,
    int? MaxLeverage = null);

// Per-exchange cache and a single refresh prevent repeated metadata requests from open tabs.
public sealed class ResearchMarkets(HttpClient http) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ResearchMarketList> cache = [];
    private readonly Dictionary<string, (string Metadata, DateTimeOffset At)> metadata = [];

    private async Task<string> MetadataAsync(string exchange, CancellationToken ct)
    {
        if (metadata.TryGetValue(exchange, out var saved) && DateTimeOffset.UtcNow - saved.At < TimeSpan.FromMinutes(5)) return saved.Metadata;
        var fresh = await MarketCatalog.ExchangeMetadataAsync(http, exchange, ct);
        metadata[exchange] = (fresh, DateTimeOffset.UtcNow);
        return fresh;
    }

    public async Task<ResearchMarketList> GetAsync(string exchange, CancellationToken ct, string sourceExchange = "hyperliquid")
    {
        MarketCatalog.ValidatePair(sourceExchange, exchange);
        var key = sourceExchange + ":" + exchange;
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out var saved) && DateTimeOffset.UtcNow - saved.UpdatedUtc < TimeSpan.FromMinutes(5)) return saved;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var target = await MetadataAsync(exchange, timeout.Token);
            var source = await MetadataAsync(sourceExchange, timeout.Token);
            var list = new ResearchMarketList(exchange, DateTimeOffset.UtcNow, MarketCatalog.CommonMarkets(exchange, target, source, sourceExchange), sourceExchange);
            cache[key] = list;
            return list;
        }
        finally { gate.Release(); }
    }

    // The same rules the replay reads from a recording (ReplayInstrument), taken from today's exchange metadata.
    public async Task<InstrumentInfo?> InstrumentAsync(string exchange, string coin, CancellationToken ct)
    {
        _ = MarketCatalog.Symbol(exchange, coin);
        string raw;
        await gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            raw = await MetadataAsync(exchange, timeout.Token);
        }
        finally { gate.Release(); }
        using var manifest = JsonDocument.Parse($"{{\"exchangeMetadata\":{raw}}}");
        if (ReplayInstrument.Read(manifest.RootElement, exchange, coin, false) is not { } rules) return null;
        decimal? price = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            price = await MarketCatalog.LastPriceAsync(http, exchange, coin, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || e is OperationCanceledException && !ct.IsCancellationRequested) { }
        // The smallest order the exchange accepts at this price: a whole step at or above both minimums.
        decimal? minimumOrder = price is { } p
            ? Math.Max(rules.MinimumQuantity, decimal.Ceiling(rules.MinimumNotional / p / rules.QuantityStep) * rules.QuantityStep) * p
            : null;
        return new(exchange, coin, rules.QuantityStep, rules.MinimumQuantity, rules.MinimumNotional, rules.MaximumQuantity, price,
            minimumOrder, rules.QuoteCurrency, rules.RuleSource.Replace("Метаданные записи", "Текущие правила биржи"), DateTimeOffset.UtcNow,
            MaxLeverage(manifest.RootElement.GetProperty("exchangeMetadata"), exchange, coin));
    }

    // The exchange's top leverage tier for the instrument where public metadata has it; Binance publishes it only to signed requests.
    public static int? MaxLeverage(JsonElement meta, string exchange, string coin)
    {
        static int? Parse(JsonElement value) => value.ValueKind == JsonValueKind.Number ? (int)value.GetDecimal()
            : decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? (int)n : null;
        try
        {
            return exchange switch
            {
                "okx" => meta.GetProperty("data").EnumerateArray().Where(x => x.GetProperty("instId").GetString() == coin + "-USDT-SWAP")
                    .Select(x => Parse(x.GetProperty("lever"))).FirstOrDefault(),
                "bybit" => meta.GetProperty("result").GetProperty("list").EnumerateArray().Where(x => x.GetProperty("symbol").GetString() == coin + "USDT")
                    .Select(x => Parse(x.GetProperty("leverageFilter").GetProperty("maxLeverage"))).FirstOrDefault(),
                "hyperliquid" => meta.GetProperty("universe").EnumerateArray().Where(x => x.GetProperty("name").GetString() == coin)
                    .Select(x => Parse(x.GetProperty("maxLeverage"))).FirstOrDefault(),
                _ => null
            } is > 0 and var lever ? lever : null;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    public void Dispose() { gate.Dispose(); http.Dispose(); }
}
