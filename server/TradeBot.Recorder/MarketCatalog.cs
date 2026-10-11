using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeBot.Recorder;

public sealed record MarketInstrument(string Coin, string Symbol);

public static class MarketCatalog
{
    public static string Symbol(string exchange, string coin) => exchange switch
    {
        "hyperliquid" => coin,
        "binance" or "bybit" => coin + "USDT",
        "okx" => coin + "-USDT-SWAP",
        _ => throw new ArgumentException("Биржа не поддерживается. Выберите Hyperliquid, Binance, Bybit или OKX.")
    };

    public static void ValidatePair(string sourceExchange, string exchange)
    {
        _ = Symbol(sourceExchange, "ARB");
        _ = Symbol(exchange, "ARB");
        if (sourceExchange == exchange) throw new ArgumentException("Выберите две разные биржи.");
    }

    public static async Task<string> ExchangeMetadataAsync(HttpClient http, string exchange, CancellationToken ct)
    {
        _ = Symbol(exchange, "ARB");
        if (exchange == "hyperliquid") return await HyperliquidMetadataAsync(http, ct);
        if (exchange == "binance") return await http.GetStringAsync("https://fapi.binance.com/fapi/v1/exchangeInfo", ct);
        if (exchange == "okx") return await http.GetStringAsync("https://www.okx.com/api/v5/public/instruments?instType=SWAP", ct);
        var entries = new List<JsonElement>();
        var cursors = new HashSet<string>();
        var cursor = "";
        for (var page = 0; page < 20; page++)
        {
            var raw = await http.GetStringAsync("https://api.bybit.com/v5/market/instruments-info?category=linear&status=Trading&limit=1000&cursor=" + Uri.EscapeDataString(cursor), ct);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.GetProperty("retCode").GetInt32() != 0) throw new HttpRequestException("Bybit: каталог недоступен.");
            var result = doc.RootElement.GetProperty("result");
            entries.AddRange(result.GetProperty("list").EnumerateArray().Select(x => x.Clone()));
            cursor = result.GetProperty("nextPageCursor").GetString() ?? "";
            if (cursor.Length == 0) return JsonSerializer.Serialize(new { retCode = 0, result = new { list = entries } });
            if (!cursors.Add(cursor)) break;
        }
        throw new HttpRequestException("Не удалось полностью загрузить каталог Bybit.");
    }
    // Last traded price from the public ticker, used only to convert between coins and USDT in the bot form.
    public static async Task<decimal?> LastPriceAsync(HttpClient http, string exchange, string coin, CancellationToken ct)
    {
        var symbol = Symbol(exchange, coin);
        string? text;
        if (exchange == "hyperliquid")
        {
            using var response = await http.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "allMids" }, ct);
            response.EnsureSuccessStatusCode();
            using var mids = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            text = mids.RootElement.TryGetProperty(coin, out var mid) ? mid.GetString() : null;
        }
        else
        {
            var url = exchange switch
            {
                "binance" => "https://fapi.binance.com/fapi/v1/ticker/price?symbol=" + Uri.EscapeDataString(symbol),
                "okx" => "https://www.okx.com/api/v5/market/ticker?instId=" + Uri.EscapeDataString(symbol),
                _ => "https://api.bybit.com/v5/market/tickers?category=linear&symbol=" + Uri.EscapeDataString(symbol)
            };
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
            var root = doc.RootElement;
            text = exchange switch
            {
                "binance" => root.GetProperty("price").GetString(),
                "okx" => root.GetProperty("data").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } row ? row.GetProperty("last").GetString() : null,
                _ => root.GetProperty("result").GetProperty("list").EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } item ? item.GetProperty("lastPrice").GetString() : null
            };
        }
        return decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var price) && price > 0 ? price : null;
    }

    public static async Task<string> HyperliquidMetadataAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "meta" }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static MarketInstrument[] CommonBinanceMarkets(string binance, string hyperliquid) => CommonMarkets("binance", binance, hyperliquid);

    public static MarketInstrument[] CommonMarkets(string exchange, string metadata, string sourceMetadata, string sourceExchange = "hyperliquid")
    {
        ValidatePair(sourceExchange, exchange);
        var coins = ActiveMarkets(sourceExchange, sourceMetadata).Select(s => s.Coin).ToHashSet(StringComparer.Ordinal);
        return ActiveMarkets(exchange, metadata).Where(s => coins.Contains(s.Coin)).ToArray();
    }

    public static MarketInstrument[] ActiveMarkets(string exchange, string metadata)
    {
        _ = Symbol(exchange, "ARB");
        using var b = JsonDocument.Parse(metadata);
        IEnumerable<MarketInstrument> markets;
        if (exchange == "hyperliquid") markets = b.RootElement.GetProperty("universe").EnumerateArray()
            .Where(s => !s.TryGetProperty("isDelisted", out var delisted) || !delisted.GetBoolean())
            .Select(s => new MarketInstrument(s.GetProperty("name").GetString()!, s.GetProperty("name").GetString()!));
        else if (exchange == "binance") markets = b.RootElement.GetProperty("symbols").EnumerateArray()
            .Where(s => s.GetProperty("quoteAsset").GetString() == "USDT"
                && s.GetProperty("contractType").GetString() == "PERPETUAL"
                && s.GetProperty("status").GetString() == "TRADING")
            .Select(s => new MarketInstrument(s.GetProperty("baseAsset").GetString()!, s.GetProperty("symbol").GetString()!));
        else if (exchange == "bybit")
        {
            if (b.RootElement.GetProperty("retCode").GetInt32() != 0) throw new HttpRequestException("Bybit: каталог недоступен.");
            markets = b.RootElement.GetProperty("result").GetProperty("list").EnumerateArray()
                .Where(s => s.GetProperty("status").GetString() == "Trading" && s.GetProperty("contractType").GetString() == "LinearPerpetual"
                    && s.GetProperty("quoteCoin").GetString() == "USDT" && s.GetProperty("settleCoin").GetString() == "USDT")
                .Select(s => new MarketInstrument(s.GetProperty("baseCoin").GetString()!, s.GetProperty("symbol").GetString()!));
        }
        else
        {
            if (b.RootElement.GetProperty("code").GetString() != "0") throw new HttpRequestException("OKX: каталог недоступен.");
            markets = b.RootElement.GetProperty("data").EnumerateArray()
                .Where(s => s.GetProperty("state").GetString() == "live" && s.GetProperty("instType").GetString() == "SWAP"
                    && s.GetProperty("ctType").GetString() == "linear" && s.GetProperty("settleCcy").GetString() == "USDT")
                .Select(s => new MarketInstrument(s.GetProperty("ctValCcy").GetString()!, s.GetProperty("instId").GetString()!));
        }
        return markets
            .Where(s => Regex.IsMatch(s.Coin, "\\A[A-Z][A-Z0-9]{0,19}\\z")
                && s.Symbol == Symbol(exchange, s.Coin))
            .Distinct().OrderBy(s => s.Coin, StringComparer.Ordinal).ToArray();
    }
}
