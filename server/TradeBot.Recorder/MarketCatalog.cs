using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeBot.Recorder;

public sealed record MarketInstrument(string Coin, string Symbol);

public static class MarketCatalog
{
    public static string Symbol(string exchange, string coin) => exchange switch
    {
        "binance" or "bybit" => coin + "USDT",
        "okx" => coin + "-USDT-SWAP",
        _ => throw new ArgumentException("Биржа не поддерживается. Выберите Binance, Bybit или OKX.")
    };

    public static async Task<string> ExchangeMetadataAsync(HttpClient http, string exchange, CancellationToken ct)
    {
        _ = Symbol(exchange, "ARB");
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
    public static async Task<string> HyperliquidMetadataAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "meta" }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static MarketInstrument[] CommonBinanceMarkets(string binance, string hyperliquid) => CommonMarkets("binance", binance, hyperliquid);

    public static MarketInstrument[] CommonMarkets(string exchange, string metadata, string hyperliquid)
    {
        _ = Symbol(exchange, "ARB");
        using var b = JsonDocument.Parse(metadata);
        using var h = JsonDocument.Parse(hyperliquid);
        var coins = h.RootElement.GetProperty("universe").EnumerateArray()
            .Where(s => !s.TryGetProperty("isDelisted", out var delisted) || !delisted.GetBoolean())
            .Select(s => s.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
        IEnumerable<MarketInstrument> markets;
        if (exchange == "binance") markets = b.RootElement.GetProperty("symbols").EnumerateArray()
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
                && s.Symbol == Symbol(exchange, s.Coin) && coins.Contains(s.Coin))
            .Distinct().OrderBy(s => s.Coin, StringComparer.Ordinal).ToArray();
    }
}
