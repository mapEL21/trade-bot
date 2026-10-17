using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

public class MarketCatalogTests
{
    private const string Hl = """{"universe":[{"name":"ARB"},{"name":"SOL"},{"name":"OLD","isDelisted":true}]}""";
    private const string Binance = """{"symbols":[{"symbol":"ARBUSDT","baseAsset":"ARB","quoteAsset":"USDT","contractType":"PERPETUAL","status":"TRADING"}]}""";
    private const string Bybit = """{"retCode":0,"result":{"nextPageCursor":"","list":[{"symbol":"ARBUSDT","baseCoin":"ARB","quoteCoin":"USDT","settleCoin":"USDT","contractType":"LinearPerpetual","status":"Trading"}]}}""";
    private const string Okx = """{"code":"0","data":[{"instId":"ARB-USDT-SWAP","ctValCcy":"ARB","settleCcy":"USDT","instType":"SWAP","ctType":"linear","state":"live"}]}""";
    private const string BybitQuote = """{"topic":"orderbook.1.ARBUSDT","type":"snapshot","ts":100,"data":{"s":"ARBUSDT","u":1,"b":[["1.00","10"]],"a":[["1.02","15"]]}}""";
    private const string OkxQuote = """{"arg":{"channel":"bbo-tbt","instId":"ARB-USDT-SWAP"},"data":[{"ts":"100","bids":[["1.00","10","0","1"]],"asks":[["1.02","15","0","1"]]}]}""";

    public static IEnumerable<object[]> ExchangePairs =>
        from source in new[] { "hyperliquid", "binance", "bybit", "okx" }
        from target in new[] { "hyperliquid", "binance", "bybit", "okx" }
        where source != target
        select new object[] { source, target };

    private static string Metadata(string exchange) => exchange switch { "hyperliquid" => Hl, "binance" => Binance, "bybit" => Bybit, _ => Okx };
    private static string QuotePayload(string exchange) => exchange switch
    {
        "hyperliquid" => """{"channel":"bbo","data":{"coin":"ARB","time":100,"bbo":[{"px":"1.00","sz":"10"},{"px":"1.02","sz":"15"}]}}""",
        "binance" => """{"e":"bookTicker","s":"ARBUSDT","u":100,"b":"1.00","B":"10","a":"1.02","A":"15"}""",
        "bybit" => BybitQuote,
        _ => OkxQuote
    };

    [Theory]
    [MemberData(nameof(ExchangePairs))]
    public void AnyOrderedPairUsesOnlySelectedMarketsAndCorrectSymbols(string source, string target)
    {
        var symbol = MarketCatalog.Symbol(target, "ARB");
        var options = RecorderOptions.Parse(["--source-exchange", source, "--exchange", target, "--coin", "ARB", "--symbol", symbol]);
        Assert.Equal([new MarketInstrument("ARB", symbol)], MarketCatalog.CommonMarkets(target, Metadata(target), Metadata(source), source));
        Assert.Empty(MarketCatalog.CommonMarkets(target, Metadata(target), Metadata(source).Replace("ARB", "OTHER"), source));
        Assert.Equal(options.Feeds.Length, options.Feeds.Select(f => f.Name).Distinct().Count());
        Assert.All(options.Feeds, feed => Assert.Contains(feed.Name.Split('-')[0], new[] { source, target }));
        foreach (var exchange in new[] { source, target })
        {
            var id = RecorderOptions.QuoteFeedId(exchange);
            Assert.Equal("market", MarketWire.Classify(id, QuotePayload(exchange), "ARB", options.SymbolForFeed(id)));
        }
        if (source == "okx" || target == "okx")
        {
            var message = MarketWire.SubscriptionMessages(options.Feeds.First(f => f.Name == "okx-public"), options).Single();
            Assert.Contains("ARB-USDT-SWAP", message);
        }
    }

    [Theory]
    [InlineData("hyperliquid")]
    [InlineData("binance")]
    [InlineData("bybit")]
    [InlineData("okx")]
    public void RejectsSameExchangeBeforeStartingNetwork(string exchange) =>
        Assert.Throws<ArgumentException>(() => RecorderOptions.Parse(["--source-exchange", exchange, "--exchange", exchange, "--coin", "ARB", "--symbol", MarketCatalog.Symbol(exchange, "ARB")]));

    [Theory]
    [InlineData("binance", Binance)]
    [InlineData("bybit", Bybit)]
    [InlineData("okx", Okx)]
    public void CatalogContainsOnlyDirectActiveMatches(string exchange, string metadata)
    {
        Assert.Equal([new MarketInstrument("ARB", MarketCatalog.Symbol(exchange, "ARB"))], MarketCatalog.CommonMarkets(exchange, metadata, Hl));
        Assert.Empty(MarketCatalog.CommonMarkets(exchange, metadata, Hl.Replace("\"ARB\"", "\"OTHER\"")));
        Assert.Empty(MarketCatalog.CommonMarkets(exchange, metadata.Replace("ARB", "OLD"), Hl));
        var inactive = metadata.Replace("TRADING", "SETTLING").Replace("Trading", "PreLaunch").Replace("live", "suspend");
        Assert.Empty(MarketCatalog.CommonMarkets(exchange, inactive, Hl));
        Assert.Empty(MarketCatalog.CommonMarkets(exchange, metadata.Replace("USDT", "USDC"), Hl));
        Assert.Empty(MarketCatalog.CommonMarkets(exchange, metadata.Replace("ARBUSDT", "1000ARBUSDT").Replace("ARB-USDT-SWAP", "1000ARB-USDT-SWAP"), Hl));
    }

    [Fact]
    public void ControlMessagesAndUnknownExchangeAreHandledExplicitly()
    {
        Assert.Equal("control", MarketWire.Classify("bybit-public", """{"op":"subscribe","success":true}""", "ARB", "ARBUSDT"));
        Assert.Throws<InvalidDataException>(() => MarketWire.Classify("bybit-public", """{"op":"subscribe","success":false}""", "ARB", "ARBUSDT"));
        Assert.Equal("control", MarketWire.Classify("okx-public", "pong", "ARB", "ARB-USDT-SWAP"));
        Assert.Throws<InvalidDataException>(() => MarketWire.Classify("okx-public", """{"event":"error"}""", "ARB", "ARB-USDT-SWAP"));
        Assert.Throws<ArgumentException>(() => MarketCatalog.Symbol("https://localhost", "ARB"));
    }

    [Fact]
    public async Task BybitCatalogFollowsPagination()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            var body = requests == 1 ? Bybit.Replace("\"nextPageCursor\":\"\"", "\"nextPageCursor\":\"next\"") : Bybit.Replace("ARB", "SOL");
            if (requests == 2) Assert.Contains("cursor=next", request.RequestUri!.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }));
        var raw = await MarketCatalog.ExchangeMetadataAsync(http, "bybit", CancellationToken.None);
        Assert.Equal(2, requests);
        Assert.Equal(2, MarketCatalog.CommonMarkets("bybit", raw, Hl).Length);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
