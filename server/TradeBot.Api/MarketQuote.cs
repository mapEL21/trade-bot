using System.Globalization;
using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record MarketQuote(decimal Bid, decimal Ask, decimal BidSize, decimal AskSize,
    long SourceOrder, DateTimeOffset ReceivedUtc, long ReceivedTicks)
{
    public decimal Mid => Bid / 2 + Ask / 2;
    public decimal SpreadBps => (Ask - Bid) / Mid * 10000;

    // Only BBO updates change the display. Trades/depth snapshots must not refresh an old BBO.
    public static bool TryRead(CaptureEvent item, out MarketQuote? quote)
    {
        quote = null;
        var relevant = false;
        try
        {
            using var doc = JsonDocument.Parse(item.Payload);
            var data = doc.RootElement;
            decimal bid, ask, bidSize, askSize;
            long order;
            if (item.Source == "hyperliquid")
            {
                if (data.GetProperty("channel").GetString() != "bbo") return false;
                relevant = true;
                data = data.GetProperty("data");
                order = data.GetProperty("time").GetInt64();
                var bbo = data.GetProperty("bbo");
                bid = Number(bbo[0].GetProperty("px"));
                bidSize = Number(bbo[0].GetProperty("sz"));
                ask = Number(bbo[1].GetProperty("px"));
                askSize = Number(bbo[1].GetProperty("sz"));
            }
            else if (item.Source == "binance-public")
            {
                if (data.TryGetProperty("data", out var wrapped)) data = wrapped;
                if (data.GetProperty("e").GetString() != "bookTicker") return false;
                relevant = true;
                order = data.GetProperty("u").GetInt64();
                bid = Number(data.GetProperty("b"));
                bidSize = Number(data.GetProperty("B"));
                ask = Number(data.GetProperty("a"));
                askSize = Number(data.GetProperty("A"));
            }
            else return false;
            if (order < 0 || bid <= 0 || ask < bid || ask > 1_000_000_000_000m || bidSize <= 0 || askSize <= 0) return true;
            quote = new(bid, ask, bidSize, askSize, order, item.ReceivedUtc, item.ReceivedTicks);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or IndexOutOfRangeException or ArgumentException)
        {
            // The recorder keeps the original event. The monitor never presents an invalid quote as current.
        }
        return relevant;
    }

    private static decimal Number(JsonElement field) => decimal.Parse(field.GetString()!, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}
