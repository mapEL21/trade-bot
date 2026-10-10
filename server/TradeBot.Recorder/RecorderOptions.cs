using System.Text.RegularExpressions;

namespace TradeBot.Recorder;

public sealed record RecorderOptions(string Coin, string Symbol, int Seconds, string Output, long MaxBytes, string Exchange = "binance")
{
    public static RecorderOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || args[i] is not ("--coin" or "--symbol" or "--seconds" or "--output" or "--max-mb" or "--exchange")
                || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Неизвестный, повторный или незавершённый параметр.");
        }
        var coin = values.GetValueOrDefault("--coin", "");
        var symbol = values.GetValueOrDefault("--symbol", "");
        var exchange = values.GetValueOrDefault("--exchange", "binance");
        if (!Regex.IsMatch(coin, "\\A[A-Z][A-Z0-9]{0,19}\\z") || symbol != MarketCatalog.Symbol(exchange, coin))
            throw new ArgumentException("Нужны --coin и --symbol: например ARB и ARBUSDT, для OKX — ARB-USDT-SWAP. Поддерживается только прямое соответствие; множители и HIP-3 требуют отдельного сопоставления.");
        if (!int.TryParse(values.GetValueOrDefault("--seconds", "60"), out var seconds) || seconds is < 1 or > 3600)
            throw new ArgumentException("--seconds: от 1 до 3600.");
        if (!int.TryParse(values.GetValueOrDefault("--max-mb", "256"), out var mb) || mb is < 1 or > 1024)
            throw new ArgumentException("--max-mb: от 1 до 1024.");
        var output = Path.GetFullPath(values.GetValueOrDefault("--output", "data/market"));
        return new(coin, symbol, seconds, output, mb * 1024L * 1024L, exchange);
    }

    public Feed[] Feeds => Exchange switch
    {
        "bybit" => [
            new("hyperliquid", new("wss://api.hyperliquid.xyz/ws"), ["bbo", "trades", "l2Book"]),
            new("bybit-public", new("wss://stream.bybit.com/v5/public/linear"), [$"orderbook.1.{Symbol}", $"orderbook.50.{Symbol}"]),
            new("bybit-market", new("wss://stream.bybit.com/v5/public/linear"), [$"publicTrade.{Symbol}"])
        ],
        "okx" => [
            new("hyperliquid", new("wss://api.hyperliquid.xyz/ws"), ["bbo", "trades", "l2Book"]),
            new("okx-public", new("wss://ws.okx.com:8443/ws/v5/public"), ["bbo-tbt", "books5"]),
            new("okx-market", new("wss://ws.okx.com:8443/ws/v5/public"), ["trades"])
        ],
        "binance" =>
    [
        new("hyperliquid", new("wss://api.hyperliquid.xyz/ws"), ["bbo", "trades", "l2Book"]),
        new("binance-public", new($"wss://fstream.binance.com/public/stream?streams={Symbol.ToLowerInvariant()}@bookTicker/{Symbol.ToLowerInvariant()}@depth20@100ms"), []),
        new("binance-market", new($"wss://fstream.binance.com/market/ws/{Symbol.ToLowerInvariant()}@aggTrade"), [])
    ],
        _ => throw new ArgumentException("Биржа не поддерживается.")
    };
}

public sealed record Feed(string Name, Uri Endpoint, string[] Subscriptions);
