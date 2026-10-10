using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record ResearchMarketList(string Exchange, DateTimeOffset UpdatedUtc, MarketInstrument[] Instruments, string SourceExchange = "hyperliquid");

// Per-exchange cache and a single refresh prevent repeated metadata requests from open tabs.
public sealed class ResearchMarkets(HttpClient http) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ResearchMarketList> cache = [];

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
            var metadata = MarketCatalog.ExchangeMetadataAsync(http, exchange, timeout.Token);
            var source = MarketCatalog.ExchangeMetadataAsync(http, sourceExchange, timeout.Token);
            await Task.WhenAll(metadata, source);
            var list = new ResearchMarketList(exchange, DateTimeOffset.UtcNow,
                MarketCatalog.CommonMarkets(exchange, await metadata, await source, sourceExchange), sourceExchange);
            cache[key] = list;
            return list;
        }
        finally { gate.Release(); }
    }

    public void Dispose() { gate.Dispose(); http.Dispose(); }
}
