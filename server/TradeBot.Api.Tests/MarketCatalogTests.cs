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

    [Theory]
    [InlineData("bybit", BybitQuote)]
    [InlineData("okx", OkxQuote)]
    public void QuotesAreParsedAndWrongSymbolsOrDepthCannotBecomeBbo(string exchange, string raw)
    {
        var symbol = MarketCatalog.Symbol(exchange, "ARB");
        var options = RecorderOptions.Parse(["--exchange", exchange, "--coin", "ARB", "--symbol", symbol]);
        Assert.Equal(exchange + "-public", options.Feeds[1].Name);
        Assert.Equal("market", MarketWire.Classify(exchange + "-public", raw, "ARB", symbol));
        Assert.Throws<InvalidDataException>(() => MarketWire.Classify(exchange + "-public", raw.Replace("ARB", "SOL"), "ARB", symbol));
        var item = new CaptureEvent(exchange + "-public", "c1", "market", DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), raw);
        Assert.True(MarketQuote.TryRead(item, out var quote));
        Assert.Equal(1.01m, quote!.Mid);
        Assert.Equal(10m, quote.BidSize);
        Assert.False(MarketQuote.TryRead(item with { Payload = raw.Replace("orderbook.1.", "orderbook.50.").Replace("bbo-tbt", "books5") }, out _));
        Assert.True(MarketQuote.TryRead(item with { Payload = raw.Replace("\"1.00\"", "\"2.00\"") }, out var crossed));
        Assert.Null(crossed);
        using var session = new ResearchSession("test", options);
        session.Started("unused");
        session.Recorded(item with { Kind = "connected", Payload = "" }, 1, 1);
        session.Recorded(item, 2, 2);
        Assert.Equal(exchange, session.Snapshot().Exchange);
        Assert.Equal("1.00", session.Snapshot().Feeds[1].Quote!.Bid);
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
    public async Task ConcurrentRequestsReuseCatalogAndFailedRefreshCanBeRetried()
    {
        var fail = true;
        var requests = 0;
        using var service = new ResearchMarkets(new HttpClient(new Handler(request =>
        {
            Interlocked.Increment(ref requests);
            if (fail) return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Post ? Hl : Binance) };
        })));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync("binance", CancellationToken.None));
        fail = false;
        var before = requests;
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.GetAsync("binance", CancellationToken.None)));
        Assert.Equal(2, requests - before);
        Assert.All(results, result => Assert.Single(result.Instruments));
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
