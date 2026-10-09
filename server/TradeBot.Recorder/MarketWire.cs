using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TradeBot.Recorder;

public static class MarketWire
{
    public const int MaxMessageBytes = 256 * 1024;

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
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (source == "hyperliquid")
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
