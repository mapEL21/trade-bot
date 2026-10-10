using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TradeBot.Recorder;

public static class MarketWire
{
    public const int MaxMessageBytes = 256 * 1024;

    public static IEnumerable<string> SubscriptionMessages(Feed feed, RecorderOptions options)
    {
        if (feed.Name.StartsWith("bybit-", StringComparison.Ordinal))
            return [JsonSerializer.Serialize(new { op = "subscribe", args = feed.Subscriptions })];
        if (feed.Name.StartsWith("okx-", StringComparison.Ordinal))
            return [JsonSerializer.Serialize(new { op = "subscribe", args = feed.Subscriptions.Select(channel => new { channel, instId = options.Symbol }) })];
        return feed.Subscriptions.Select(type => JsonSerializer.Serialize(new { method = "subscribe", subscription = new { type, coin = options.Coin } }));
    }

    public static async Task HeartbeatAsync(ClientWebSocket socket, string source, CancellationToken ct)
    {
        var payload = source.StartsWith("bybit-", StringComparison.Ordinal) ? "{\"op\":\"ping\"}"
            : source.StartsWith("okx-", StringComparison.Ordinal) ? "ping" : null;
        if (payload is null) return;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                await socket.SendAsync(Encoding.UTF8.GetBytes(payload).AsMemory(), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            if (!ct.IsCancellationRequested) socket.Abort();
        }
    }

    public static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Удалённая сторона закрыла поток.");
            if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Ожидалось текстовое сообщение.");
            if (message.Length + part.Count > MaxMessageBytes) throw new InvalidDataException("Сообщение превышает лимит.");
            message.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return new UTF8Encoding(false, true).GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    public static string Classify(string source, string raw, string coin, string symbol)
    {
        if (source.StartsWith("okx-", StringComparison.Ordinal) && raw == "pong") return "control";
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (source.StartsWith("bybit-", StringComparison.Ordinal))
        {
            if (root.TryGetProperty("op", out var op))
            {
                if (root.TryGetProperty("success", out var success) && !success.GetBoolean()) throw new InvalidDataException("Bybit отклонил подписку.");
                if (op.GetString() is "subscribe" or "ping" or "pong") return "control";
            }
            var topic = root.GetProperty("topic").GetString();
            var data = root.GetProperty("data");
            if (source == "bybit-public" && (topic == $"orderbook.1.{symbol}" || topic == $"orderbook.50.{symbol}")
                && data.GetProperty("s").GetString() == symbol) return "market";
            if (source == "bybit-market" && topic == $"publicTrade.{symbol}" && data.GetArrayLength() > 0
                && data.EnumerateArray().All(t => t.GetProperty("s").GetString() == symbol)) return "market";
        }
        else if (source.StartsWith("okx-", StringComparison.Ordinal))
        {
            if (root.TryGetProperty("event", out var kind))
            {
                if (kind.GetString() == "error") throw new InvalidDataException("OKX отклонил подписку.");
                if (kind.GetString() == "subscribe") return "control";
            }
            var arg = root.GetProperty("arg");
            var channel = arg.GetProperty("channel").GetString();
            if (arg.GetProperty("instId").GetString() == symbol && root.GetProperty("data").GetArrayLength() > 0
                && (source == "okx-public" && channel is "bbo-tbt" or "books5" || source == "okx-market" && channel == "trades")) return "market";
        }
        else if (source == "hyperliquid")
        {
            var channel = root.GetProperty("channel").GetString();
            if (channel == "error") throw new InvalidDataException("Hyperliquid отклонил подписку.");
            if (channel is "subscriptionResponse" or "pong") return "control";
            var data = root.GetProperty("data");
            if (channel == "trades")
            {
                if (data.GetArrayLength() == 0) return "control";
                foreach (var trade in data.EnumerateArray())
                    if (trade.GetProperty("coin").GetString() != coin) throw new InvalidDataException("Чужой инструмент в потоке.");
                return "market";
            }
            if (channel is "bbo" or "l2Book" && data.GetProperty("coin").GetString() == coin) return "market";
        }
        else
        {
            if (root.TryGetProperty("data", out var wrapped)) root = wrapped;
            if (root.TryGetProperty("e", out var e) && e.GetString() is "bookTicker" or "depthUpdate" or "aggTrade"
                && root.GetProperty("s").GetString() == symbol) return "market";
        }
        throw new InvalidDataException("Неизвестное сообщение или несовпадение инструмента.");
    }
}
