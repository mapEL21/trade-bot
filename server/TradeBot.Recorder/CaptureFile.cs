using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace TradeBot.Recorder;

public sealed record CaptureEvent(string Source, string Connection, string Kind, DateTimeOffset ReceivedUtc,
    long ReceivedTicks, string Payload);

public sealed record CaptureBufferReport(int Capacity, long MaxEstimatedBytes, int PeakEvents,
    long PeakEstimatedBytes, int PendingEvents, long PendingEstimatedBytes);

// A full queue terminates the capture: silent loss would invalidate latency research.
public sealed class CaptureFile
{
    private readonly Channel<CaptureEvent> queue;
    private readonly object bufferGate = new();
    private readonly int capacity;
    private readonly long maxBufferedBytes;
    private int pending, peakEvents;
    private long pendingBytes, peakBytes;
    private bool completed;
    private readonly long maxBytes;
    private readonly List<double> writeDelay = [];
    private readonly Action<CaptureEvent, long, long>? onRecorded;
    public long Written { get; private set; }
    public long Bytes { get; private set; }
    public Dictionary<string, long> MarketMessages { get; } = new(StringComparer.Ordinal);

    public CaptureFile(long maxBytes, int capacity = 8192, Action<CaptureEvent, long, long>? onRecorded = null,
        long maxBufferedBytes = 32 * 1024 * 1024)
    {
        if (maxBytes <= 0 || capacity <= 0 || maxBufferedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        this.maxBytes = maxBytes;
        this.capacity = capacity;
        this.maxBufferedBytes = maxBufferedBytes;
        this.onRecorded = onRecorded;
        queue = Channel.CreateBounded<CaptureEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public void Publish(CaptureEvent item)
    {
        // Bound retained strings as well as event count; never wait on disk in a socket reader.
        var size = EstimatedBytes(item);
        lock (bufferGate)
        {
            if (completed) throw new InvalidOperationException("Очередь записи уже закрыта.");
            if (pending >= capacity || size > maxBufferedBytes - pendingBytes)
                throw new InvalidOperationException($"Регистратор не успевает сохранять данные: буфер заполнен ({pending}/{capacity} событий, около {pendingBytes}/{maxBufferedBytes} байт). Сессия неполная.");
            if (!queue.Writer.TryWrite(item)) throw new InvalidOperationException("Очередь записи уже закрыта.");
            pending++;
            pendingBytes += size;
            peakEvents = Math.Max(peakEvents, pending);
            peakBytes = Math.Max(peakBytes, pendingBytes);
        }
    }

    private static long EstimatedBytes(CaptureEvent item) => 512L + 2L *
        ((long)item.Payload.Length + item.Source.Length + item.Connection.Length + item.Kind.Length);

    public CaptureBufferReport BufferSummary()
    {
        lock (bufferGate) return new(capacity, maxBufferedBytes, peakEvents, peakBytes, pending, pendingBytes);
    }

    public void Complete()
    {
        lock (bufferGate) { completed = true; queue.Writer.TryComplete(); }
    }

    public async Task DrainAsync(Stream output)
    {
        await foreach (var item in queue.Reader.ReadAllAsync())
        {
            lock (bufferGate) { pending--; pendingBytes -= EstimatedBytes(item); }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(item);
            if (bytes.Length + 1L > maxBytes - Bytes) throw new IOException("Достигнут лимит размера записи. Сессия остановлена.");
            await output.WriteAsync(bytes);
            await output.WriteAsync(NewLine);
            Bytes += bytes.Length + 1;
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

    private static readonly byte[] NewLine = [(byte)'\n'];

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
