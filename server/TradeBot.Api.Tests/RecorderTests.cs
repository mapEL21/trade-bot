using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

public class RecorderTests
{
    [Theory]
    [InlineData("ARB", "1000ARBUSDT")]
    [InlineData("ARB", "ETHUSDT")]
    [InlineData("../ARB", "../ARBUSDT")]
    public void RefusesAmbiguousOrUnsafeMapping(string coin, string symbol) =>
        Assert.Throws<ArgumentException>(() => RecorderOptions.Parse(["--coin", coin, "--symbol", symbol]));

    [Fact]
    public void RefusesInactiveMarketsBeforeSubscriptions()
    {
        var options = RecorderOptions.Parse(["--coin", "ARB", "--symbol", "ARBUSDT"]);
        const string b = """{"symbols":[{"symbol":"ARBUSDT","baseAsset":"ARB","quoteAsset":"USDT","contractType":"PERPETUAL","status":"TRADING"}]}""";
        RecorderProgram.ValidateMarkets(b, """{"universe":[{"name":"ARB"}]}""", options);
        Assert.Throws<ArgumentException>(() => RecorderProgram.ValidateMarkets(b, """{"universe":[{"name":"ARB","isDelisted":true}]}""", options));
        Assert.Throws<ArgumentException>(() => RecorderProgram.ValidateMarkets(b.Replace("TRADING", "SETTLING"), """{"universe":[{"name":"ARB"}]}""", options));
    }

    [Fact]
    public void DistinguishesSubscriptionAcknowledgementFromMarketData()
    {
        Assert.Equal("control", MarketWire.Classify("hyperliquid", """{"channel":"subscriptionResponse","data":{}}""", "ARB", "ARBUSDT"));
        Assert.Equal("market", MarketWire.Classify("hyperliquid", """{"channel":"bbo","data":{"coin":"ARB","time":123,"bbo":[null,null]}}""", "ARB", "ARBUSDT"));
        Assert.Equal("market", MarketWire.Classify("binance-public", """{"stream":"arbusdt@bookTicker","data":{"e":"bookTicker","s":"ARBUSDT","u":45}}""", "ARB", "ARBUSDT"));
        Assert.Throws<InvalidDataException>(() => MarketWire.Classify("binance-market", """{"e":"aggTrade","s":"ETHUSDT"}""", "ARB", "ARBUSDT"));
        Assert.Throws<InvalidDataException>(() => MarketWire.Classify("hyperliquid", """{"channel":"error","data":"unknown coin"}""", "ARB", "ARBUSDT"));
    }

    [Fact]
    public async Task PreservesRawPayloadAndReceiptTimeWhenDraining()
    {
        var file = new CaptureFile(100000);
        var now = DateTimeOffset.UtcNow;
        var ticks = Stopwatch.GetTimestamp();
        const string raw = "{\"p\":\"0.123456789012345678\",\"message\":\"строка\\nвторая\"}";
        file.Publish(new("test", "connection1", "market", now, ticks, raw));
        file.Complete();
        using var output = new MemoryStream();
        await file.DrainAsync(output);
        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        var saved = JsonSerializer.Deserialize<CaptureEvent>(lines[0])!;
        Assert.Equal(raw, saved.Payload);
        Assert.Equal(now, saved.ReceivedUtc);
        Assert.Equal(ticks, saved.ReceivedTicks);
        Assert.Equal(1, file.MarketMessages["test"]);
    }

    [Fact]
    public void QueueOverflowFailsInsteadOfDroppingOldEvents()
    {
        var file = new CaptureFile(100000, 1);
        file.Publish(new("test", "1", "market", DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), "{}"));
        Assert.Throws<InvalidOperationException>(() => file.Publish(new("test", "1", "market", DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), "{}")));
    }

    [Fact]
    public async Task FileLimitDoesNotWritePartialJsonLine()
    {
        var file = new CaptureFile(1);
        file.Publish(new("test", "1", "market", DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), "{}"));
        file.Complete();
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => file.DrainAsync(output));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task ReassemblesUtf8SplitAcrossFrames()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"цена\"}");
        using var socket = new FragmentSocket([bytes[..10], bytes[10..]]);
        Assert.Equal("{\"text\":\"цена\"}", await MarketWire.ReceiveTextAsync(socket, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsOversizedMessage()
    {
        var parts = Enumerable.Range(0, 33).Select(_ => new byte[8192]).ToArray();
        using var socket = new FragmentSocket(parts);
        await Assert.ThrowsAsync<InvalidDataException>(() => MarketWire.ReceiveTextAsync(socket, CancellationToken.None));
    }

    private sealed class FragmentSocket(byte[][] frames) : WebSocket
    {
        private int index;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[index++];
            frame.AsSpan().CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Text, index == frames.Length));
        }
    }
}
