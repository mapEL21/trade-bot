using System.Text.RegularExpressions;

namespace TradeBot.Recorder;

public sealed record RecorderOptions(string Coin, string Symbol, int Seconds, string Output, long MaxBytes)
{
    public static RecorderOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length || args[i] is not ("--coin" or "--symbol" or "--seconds" or "--output" or "--max-mb")
                || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Неизвестный, повторный или незавершённый параметр.");
        }
        var coin = values.GetValueOrDefault("--coin", "");
        var symbol = values.GetValueOrDefault("--symbol", "");
        if (!Regex.IsMatch(coin, "\\A[A-Z][A-Z0-9]{0,19}\\z") || symbol != coin + "USDT")
            throw new ArgumentException("Нужны --coin и --symbol, например ARB и ARBUSDT. Первая версия поддерживает только прямое соответствие TOKEN/TOKENUSDT; множители и HIP-3 требуют отдельного сопоставления.");
        if (!int.TryParse(values.GetValueOrDefault("--seconds", "60"), out var seconds) || seconds is < 1 or > 3600)
            throw new ArgumentException("--seconds: от 1 до 3600.");
        if (!int.TryParse(values.GetValueOrDefault("--max-mb", "256"), out var mb) || mb is < 1 or > 1024)
            throw new ArgumentException("--max-mb: от 1 до 1024.");
        var output = Path.GetFullPath(values.GetValueOrDefault("--output", "data/market"));
        return new(coin, symbol, seconds, output, mb * 1024L * 1024L);
    }

    public Feed[] Feeds =>
    [
        new("hyperliquid", new("wss://api.hyperliquid.xyz/ws"), ["bbo", "trades", "l2Book"]),
        new("binance-public", new($"wss://fstream.binance.com/public/stream?streams={Symbol.ToLowerInvariant()}@bookTicker/{Symbol.ToLowerInvariant()}@depth20@100ms"), []),
        new("binance-market", new($"wss://fstream.binance.com/market/ws/{Symbol.ToLowerInvariant()}@aggTrade"), [])
    ];
}

public sealed record Feed(string Name, Uri Endpoint, string[] Subscriptions);
