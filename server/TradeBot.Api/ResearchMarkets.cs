using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record ResearchMarketList(string Exchange, DateTimeOffset UpdatedUtc, MarketInstrument[] Instruments);

// Per-exchange cache and a single refresh prevent repeated metadata requests from open tabs.
public sealed class ResearchMarkets(HttpClient http) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ResearchMarketList> cache = [];

    public async Task<ResearchMarketList> GetAsync(string exchange, CancellationToken ct)
    {
        _ = MarketCatalog.Symbol(exchange, "ARB");
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(exchange, out var saved) && DateTimeOffset.UtcNow - saved.UpdatedUtc < TimeSpan.FromMinutes(5)) return saved;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var metadata = MarketCatalog.ExchangeMetadataAsync(http, exchange, timeout.Token);
            var hyperliquid = MarketCatalog.HyperliquidMetadataAsync(http, timeout.Token);
            await Task.WhenAll(metadata, hyperliquid);
            var list = new ResearchMarketList(exchange, DateTimeOffset.UtcNow,
                MarketCatalog.CommonMarkets(exchange, await metadata, await hyperliquid));
            cache[exchange] = list;
            return list;
        }
        finally { gate.Release(); }
    }

    public void Dispose() { gate.Dispose(); http.Dispose(); }
}
