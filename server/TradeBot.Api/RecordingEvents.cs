using TradeBot.Recorder;

namespace TradeBot.Api;

// What one recorded event contributes to the strategy. A Hyperliquid connection event is both a quote reset and a trade-stream
// reset, so Point and Trade can be set together.
public readonly record struct ParsedEvent(MarketAnalysis.Input? Point, ReplayBookEvent? Book, ReplayTradeEvent? Trade);

// Reads recorded events exactly as the replay does; the live paper session uses the same parser so both see identical inputs.
public sealed class RecordingEventParser(string coin, string sourceExchange, string exchange, bool depth)
{
    private readonly string[] ids = [RecorderOptions.QuoteFeedId(sourceExchange), RecorderOptions.QuoteFeedId(exchange)];
    private readonly string[] tradeIds = [RecorderOptions.TradeFeedId(sourceExchange), RecorderOptions.TradeFeedId(exchange)];

    public ParsedEvent Parse(CaptureEvent item, int sequence)
    {
        ReplayTradeEvent? trade = null;
        if (depth && tradeIds.Contains(item.Source))
        {
            var venue = Array.IndexOf(tradeIds, item.Source);
            // Connection events reset the trade stream; for Hyperliquid they are shared with quotes and fall through.
            if (item.Kind is "connecting" or "connected" or "gap" or "invalid")
                trade = new(item.ReceivedTicks, sequence, venue, item.Connection, null);
            else if (item.Kind == "market" && ReplayTradeParser.TryRead(item, coin, out var prints))
                return new(null, null, new(item.ReceivedTicks, sequence, venue, item.Connection, prints));
        }
        if (!ids.Contains(item.Source)) return new(null, null, trade);
        if (depth && item.Kind == "market" && ReplayDepthParser.TryRead(item, coin, out var book))
            return new(null, new(item.ReceivedTicks, sequence, Array.IndexOf(ids, item.Source), item.Connection, book), trade);
        MarketQuote? quote = null;
        if (item.Kind is not ("connecting" or "connected" or "gap" or "invalid")
            && (item.Kind != "market" || !MarketQuote.TryRead(item, out quote))) return new(null, null, trade);
        return new(new(item.ReceivedTicks, sequence, item.Source, item.Connection, item.Kind, quote), null, trade);
    }
}
