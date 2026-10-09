namespace TradeBot.Recorder;

public interface IRecordingObserver
{
    void Started(string directory);
    void Recorded(CaptureEvent item, long written, long bytes);
    void Finished(RecordingReport report);
}

public sealed record RecordingReport(DateTimeOffset EndedUtc, string Status, string? Failure,
    int GapCount, long Written, long Bytes, Dictionary<string, long> MarketMessages,
    object PersistenceDelay, bool ExecutionEnabled = false);

public interface IRecordingRunner
{
    Task<int> RunAsync(RecorderOptions options, IRecordingObserver observer, CancellationToken ct);
}

public sealed class RecordingRunner : IRecordingRunner
{
    public Task<int> RunAsync(RecorderOptions options, IRecordingObserver observer, CancellationToken ct) =>
        RecorderProgram.RunAsync(options, ct, observer);
}
