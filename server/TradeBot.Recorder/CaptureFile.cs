using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TradeBot.Recorder;

public sealed record CaptureEvent(string Source, string Connection, string Kind, DateTimeOffset ReceivedUtc,
    long ReceivedTicks, string Payload);

// A full queue terminates the capture: silent loss would invalidate latency research.
public sealed class CaptureFile
{
    private readonly Channel<CaptureEvent> queue;
    private readonly long maxBytes;
    private readonly List<double> writeDelay = [];
    private readonly Action<CaptureEvent, long, long>? onRecorded;
    public long Written { get; private set; }
    public long Bytes { get; private set; }
    public Dictionary<string, long> MarketMessages { get; } = new(StringComparer.Ordinal);

    public CaptureFile(long maxBytes, int capacity = 128, Action<CaptureEvent, long, long>? onRecorded = null)
    {
        this.maxBytes = maxBytes;
        this.onRecorded = onRecorded;
        queue = Channel.CreateBounded<CaptureEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public void Publish(CaptureEvent item)
    {
        if (!queue.Writer.TryWrite(item))
            throw new InvalidOperationException("Очередь записи переполнена или закрыта. Сессия неполная.");
    }

    public void Complete() => queue.Writer.TryComplete();

    public async Task DrainAsync(Stream output)
    {
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item) + "\n");
            if (Bytes + bytes.Length > maxBytes) throw new IOException("Достигнут лимит размера записи. Сессия остановлена.");
            await output.WriteAsync(bytes);
            Bytes += bytes.Length;
            Written++;
            if (item.Kind == "market") MarketMessages[item.Source] = MarketMessages.GetValueOrDefault(item.Source) + 1;
            // Bounded rolling sample; this measures persistence delay, NOT exchange/network latency.
            var delay = Stopwatch.GetElapsedTime(item.ReceivedTicks).TotalMilliseconds;
            if (writeDelay.Count < 10000) writeDelay.Add(delay);
            else writeDelay[(int)((Written - 1) % 10000)] = delay;
            if (Written % 256 == 0) await output.FlushAsync();
            onRecorded?.Invoke(item, Written, Bytes);
        }
        await output.FlushAsync();
    }

    public object DelaySummary()
    {
        var sorted = writeDelay.Order().ToArray();
        double? P(double p) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
        return new
        {
            metric = "receive-to-file-write-ms; not network or order latency",
            sampleCount = sorted.Length,
            window = "last up to 10000 events",
            p50 = P(.5),
            p95 = P(.95),
            p99 = P(.99)
        };
    }
}
