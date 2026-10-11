using System.Globalization;
using System.Text.Json;
using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record ReplayInstrument(decimal QuantityStep, decimal MinimumQuantity, decimal MinimumNotional,
    decimal? MaximumQuantity, decimal BasePerUnit, decimal PriceStep, string QuoteCurrency, string RuleSource,
    int? HyperliquidSizeDecimals = null)
{
    public decimal RoundQuantity(decimal quantity) => decimal.Floor(quantity / QuantityStep) * QuantityStep;
    public decimal Limit(decimal price, bool buy, decimal bandBps)
    {
        var limit = price * (1 + (buy ? 1 : -1) * bandBps / 10000);
        var step = PriceStep;
        if (HyperliquidSizeDecimals is { } decimals)
        {
            // Five significant figures, the perpetual decimal cap, or whole integer prices.
            decimal magnitude = 1;
            while (magnitude * 10 <= limit) magnitude *= 10;
            while (magnitude > limit) magnitude /= 10;
            step = Math.Min(1, Math.Max(Pow10(-(6 - decimals)), magnitude / 10000));
        }
        return (buy ? decimal.Floor(limit / step) : decimal.Ceiling(limit / step)) * step;
    }
    private static decimal Pow10(int n) { decimal x = 1; for (var i = 0; i < Math.Abs(n); i++) x = n < 0 ? x / 10 : x * 10; return x; }
    internal static decimal Number(JsonElement value) => decimal.Parse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static decimal N(JsonElement value, string key) => Number(value.GetProperty(key));

    public static ReplayInstrument? Read(JsonElement manifest, string exchange, string coin, bool source)
    {
        try
        {
            var key = source ? "sourceMetadata" : "exchangeMetadata";
            if (!manifest.TryGetProperty(key, out var meta) && !(source && exchange == "hyperliquid" && manifest.TryGetProperty("hyperliquidMetadata", out meta))) return null;
            ReplayInstrument result;
            if (exchange == "binance")
            {
                var item = meta.GetProperty("symbols").EnumerateArray().Single(x => x.GetProperty("symbol").GetString() == coin + "USDT");
                if (item.GetProperty("contractType").GetString() != "PERPETUAL" || item.GetProperty("baseAsset").GetString() != coin
                    || item.GetProperty("quoteAsset").GetString() != "USDT") return null;
                var filters = item.GetProperty("filters").EnumerateArray().ToDictionary(x => x.GetProperty("filterType").GetString()!);
                var lot = filters["MARKET_LOT_SIZE"];
                result = new(N(lot, "stepSize"), N(lot, "minQty"), N(filters["MIN_NOTIONAL"], "notional"), N(lot, "maxQty"), 1,
                    N(filters["PRICE_FILTER"], "tickSize"), "USDT", "Метаданные записи: MARKET_LOT_SIZE, MIN_NOTIONAL, PRICE_FILTER");
            }
            else if (exchange == "bybit")
            {
                var item = meta.GetProperty("result").GetProperty("list").EnumerateArray().Single(x => x.GetProperty("symbol").GetString() == coin + "USDT");
                if (item.GetProperty("contractType").GetString() != "LinearPerpetual" || item.GetProperty("baseCoin").GetString() != coin
                    || item.GetProperty("settleCoin").GetString() != "USDT") return null;
                var lot = item.GetProperty("lotSizeFilter");
                result = new(N(lot, "qtyStep"), N(lot, "minOrderQty"), N(lot, "minNotionalValue"), N(lot, "maxMktOrderQty"), 1,
                    N(item.GetProperty("priceFilter"), "tickSize"), "USDT", "Метаданные записи: lotSizeFilter, priceFilter");
            }
            else if (exchange == "okx")
            {
                var item = meta.GetProperty("data").EnumerateArray().Single(x => x.GetProperty("instId").GetString() == coin + "-USDT-SWAP");
                if (item.GetProperty("ctType").GetString() != "linear" || item.GetProperty("ctValCcy").GetString() != coin
                    || item.GetProperty("settleCcy").GetString() != "USDT" || N(item, "ctMult") != 1) return null;
                var unit = N(item, "ctVal");
                result = new(N(item, "lotSz") * unit, N(item, "minSz") * unit, 0, N(item, "maxMktSz") * unit, unit,
                    N(item, "tickSz"), "USDT", "Метаданные записи: lotSz, minSz, maxMktSz; контракты переведены в монеты через ctVal");
            }
            else if (exchange == "hyperliquid")
            {
                var item = meta.GetProperty("universe").EnumerateArray().Single(x => x.GetProperty("name").GetString() == coin);
                var decimals = item.GetProperty("szDecimals").GetInt32();
                if (decimals is < 0 or > 6) return null;
                var step = Pow10(-decimals);
                result = new(step, step, 10, null, 1, Pow10(-(6 - decimals)), "USDC",
                    "szDecimals из записи; минимальный номинал 10 USD — допущение по правилам Hyperliquid, не историческое поле", decimals);
            }
            else return null;
            return result.QuantityStep > 0 && result.MinimumQuantity > 0 && result.MinimumNotional >= 0 && result.PriceStep > 0
                && result.BasePerUnit > 0 && (result.MaximumQuantity is null || result.MaximumQuantity >= result.MinimumQuantity) ? result : null;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException) { return null; }
    }
}

public readonly record struct ReplayLevel(decimal Price, decimal Quantity);
public sealed record ReplayBookUpdate(bool Snapshot, long Order, ReplayLevel[] Bids, ReplayLevel[] Asks);
public sealed record ReplayBookEvent(long Ticks, int Sequence, int Venue, string Connection, ReplayBookUpdate? Update);
public sealed record ReplayMarketData(ReplayInstrument?[] Instruments, List<ReplayBookEvent> Books);
public sealed record ReplayFill(decimal Quantity, decimal Price, bool BeyondVisibleDepth);

public static class ReplayDepthParser
{
    // Only the exact feeds captured by RecorderOptions are supported. Unknown/malformed depth invalidates the book.
    public static bool TryRead(CaptureEvent item, string coin, out ReplayBookUpdate? update)
    {
        update = null; var relevant = false;
        try
        {
            using var document = JsonDocument.Parse(item.Payload);
            var root = document.RootElement;
            JsonElement bids, asks; long order; bool snapshot = true; bool objects = false; int depth;
            switch (item.Source)
            {
                case "binance-public":
                    if (!root.TryGetProperty("stream", out var stream) || stream.GetString() != coin.ToLowerInvariant() + "usdt@depth20@100ms") return false;
                    relevant = true; root = root.GetProperty("data");
                    if (root.GetProperty("s").GetString() != coin + "USDT" || root.GetProperty("e").GetString() != "depthUpdate") return true;
                    bids = root.GetProperty("b"); asks = root.GetProperty("a"); order = root.GetProperty("u").GetInt64(); depth = 20; break;
                case "bybit-public":
                    if (!root.TryGetProperty("topic", out var topic) || topic.GetString() != "orderbook.50." + coin + "USDT") return false;
                    relevant = true; var type = root.GetProperty("type").GetString(); root = root.GetProperty("data");
                    if (root.GetProperty("s").GetString() != coin + "USDT" || type is not ("snapshot" or "delta")) return true;
                    order = root.GetProperty("u").GetInt64(); snapshot = type == "snapshot" || order == 1;
                    bids = root.GetProperty("b"); asks = root.GetProperty("a"); depth = snapshot ? 50 : 200; break;
                case "okx-public":
                    if (!root.TryGetProperty("arg", out var arg) || arg.GetProperty("channel").GetString() != "books5") return false;
                    relevant = true;
                    if (arg.GetProperty("instId").GetString() != coin + "-USDT-SWAP"
                        || (root.TryGetProperty("action", out var action) && action.GetString() != "snapshot")) return true;
                    root = root.GetProperty("data")[0]; bids = root.GetProperty("bids"); asks = root.GetProperty("asks");
                    order = long.Parse(root.GetProperty("ts").GetString()!, CultureInfo.InvariantCulture); depth = 5; break;
                case "hyperliquid":
                    if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != "l2Book") return false;
                    relevant = true; root = root.GetProperty("data");
                    if (root.GetProperty("coin").GetString() != coin) return true;
                    bids = root.GetProperty("levels")[0]; asks = root.GetProperty("levels")[1];
                    order = root.GetProperty("time").GetInt64(); depth = 20; objects = true; break;
                default: return false;
            }
            ReplayLevel[] Levels(JsonElement rows)
            {
                if (rows.GetArrayLength() > depth) throw new FormatException();
                var levels = rows.EnumerateArray().Select(x => new ReplayLevel(
                    ReplayInstrument.Number(objects ? x.GetProperty("px") : x[0]),
                    ReplayInstrument.Number(objects ? x.GetProperty("sz") : x[1]))).ToArray();
                if (levels.Any(x => x.Price <= 0 || x.Price > 1e12m || x.Quantity < 0 || x.Quantity > 1e12m)
                    || levels.Select(x => x.Price).Distinct().Count() != levels.Length) throw new FormatException();
                return levels;
            }
            if (order < 0) return true;
            update = new(snapshot, order, Levels(bids), Levels(asks));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException or IndexOutOfRangeException) { }
        return relevant;
    }
}

public sealed class ReplayBook
{
    private readonly SortedDictionary<decimal, decimal> bids = new();
    private readonly SortedDictionary<decimal, decimal> asks = new();
    private long order = -1;
    public bool Valid { get; private set; }
    public double At { get; private set; }
    public void Reset() { bids.Clear(); asks.Clear(); Valid = false; order = -1; }
    public bool Fresh(double at, int maxAge) => Valid && at - At <= maxAge;
    public void Apply(ReplayBookUpdate? update, double at, decimal unit)
    {
        if (update is null) { Reset(); return; }
        if (update.Snapshot && update.Order == 1) Reset();
        if (update.Order < order) { Reset(); return; } // Never silently continue an out-of-order delta chain.
        if (update.Order == order || (!update.Snapshot && !Valid)) return;
        if (update.Snapshot) { bids.Clear(); asks.Clear(); }
        void Side(SortedDictionary<decimal, decimal> side, ReplayLevel[] levels)
        {
            foreach (var level in levels)
                if (level.Quantity == 0) side.Remove(level.Price); else side[level.Price] = level.Quantity * unit;
        }
        Side(bids, update.Bids); Side(asks, update.Asks);
        if (bids.Count == 0 || asks.Count == 0 || bids.Last().Key >= asks.First().Key || bids.Count > 50 || asks.Count > 50) { Reset(); return; }
        order = update.Order; At = at; Valid = true;
    }
    public ReplayFill Fill(bool buy, decimal quantity, decimal limit, decimal extraSlippageBps, decimal step, bool consume)
    {
        var side = buy ? asks : bids;
        var levels = (buy ? side.AsEnumerable() : side.Reverse()).ToArray();
        decimal filled = 0, value = 0; var bounded = false;
        var used = new List<(decimal Price, decimal Quantity)>();
        foreach (var level in levels)
        {
            var price = level.Key * (1 + (buy ? 1 : -1) * extraSlippageBps / 10000);
            if (buy ? price > limit : price < limit) { bounded = true; break; }
            var take = decimal.Floor(Math.Min(quantity - filled, level.Value) / step) * step;
            filled += take; value += take * price; used.Add((level.Key, take));
            if (price == limit) bounded = true;
            if (filled == quantity) break;
        }
        if (consume) foreach (var use in used) side[use.Price] -= use.Quantity;
        return new(filled, filled == 0 ? 0 : value / filled, filled < quantity && !bounded);
    }
}
