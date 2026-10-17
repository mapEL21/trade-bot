using System.Globalization;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

// Walls in the order book: approach, eaten, moved and pulled, on one coin with a calm book of 20 levels a side.
public sealed class WallSignalTests
{
    private const long T0 = 1_760_000_000_000;
    private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);

    // 20 levels a side, 0.01 apart around the mid 100 + offset, one coin each (about 100 USDT); walls replace a level's size.
    private static CaptureEvent Book(long second, decimal offset, Dictionary<decimal, decimal>? walls = null)
    {
        var ms = T0 + second * 1000;
        string Side(int sign) => string.Join(',', Enumerable.Range(1, 20).Select(i =>
        {
            var price = Math.Round(100 + offset + sign * .01m * i, 2);
            return $"[\"{S(price)}\",\"{S(walls?.GetValueOrDefault(price) is > 0 and var q ? q : 1)}\"]";
        }));
        return new(ScanRecorder.Public, "c", "market", DateTimeOffset.UnixEpoch, ms,
            $"{{\"stream\":\"xusdt@depth20@500ms\",\"data\":{{\"e\":\"depthUpdate\",\"E\":{ms},\"T\":{ms},\"s\":\"XUSDT\",\"b\":[{Side(-1)}],\"a\":[{Side(1)}]}}}}");
    }
    private static CaptureEvent Trade(long second, decimal price) => new(ScanRecorder.Market, "c", "market", DateTimeOffset.UnixEpoch, T0 + second * 1000,
        $"{{\"stream\":\"xusdt@aggTrade\",\"data\":{{\"e\":\"aggTrade\",\"s\":\"XUSDT\",\"p\":\"{S(price)}\",\"q\":\"1\",\"T\":{T0 + second * 1000},\"m\":false}}}}");

    [Fact]
    public void WallsAreFoundWhenApproachedEatenMovedAndPulled()
    {
        var events = new List<CaptureEvent>();
        void At(long second, decimal offset, Dictionary<decimal, decimal>? walls = null)
        {
            events.Add(Trade(second, 100 + offset));
            events.Add(Book(second, offset, walls));
        }
        // A bid wall at 99.85 from the start; signals need five minutes of trades first.
        for (var t = 0; t < 360; t++) At(t, 0, new() { [99.85m] = 100 });
        // The price comes down to 0.05% from it: approach, towards the wall (down).
        for (var t = 360; t < 380; t++) At(t, -.1m, new() { [99.85m] = 100 });
        // Then trades through it: eaten, down.
        for (var t = 380; t < 440; t++) At(t, -.2m, new() { [99.95m] = 100 });
        // An ask wall at 99.95 since 380 s is moved to 99.92 after 60 s: moved, down (where it went).
        for (var t = 440; t < 470; t++) At(t, -.2m, new() { [99.92m] = 100, [99.70m] = 100 });
        // A bid wall at 99.70, 0.1% from the 99.80 mid, vanishes without the price reaching it: pulled, down.
        for (var t = 470; t < 520; t++) At(t, -.2m, new() { [99.92m] = 100, [99.70m] = t < 510 ? 100 : 1 });

        var detector = new SignalDetector(new Dictionary<string, decimal> { ["XUSDT"] = 10_000_000 }, liquidations: false);
        foreach (var e in events) detector.Process(e);
        var result = detector.Result("walls");
        var found = result.Events.Where(e => e.Type.Contains("плотности")).Select(e => (e.Type, e.Direction, (e.AtMs - T0) / 1000)).ToList();
        Assert.Contains(("Подход к плотности", -1, 360L), found);
        Assert.Contains(("Проедание плотности", -1, 380L), found);
        Assert.Contains(("Переставление плотности", -1, 440L), found);
        Assert.Contains(("Снятие плотности", -1, 510L), found);
        // The moved wall is still a wall: no "eaten" or "pulled" for it.
        Assert.Single(found, f => f.Item1 == "Проедание плотности");
        Assert.Single(found, f => f.Item1 == "Снятие плотности");

        // The spread is 2 bp throughout: below two maker fees in every snapshot.
        var spread = Assert.Single(result.Spreads!);
        Assert.Equal((events.Count / 2L, 0L), (spread.Snapshots, spread.Above4));
        Assert.Equal(2m, Math.Round(spread.SumBps / spread.Snapshots, 1));
    }

    [Fact]
    public void AWallYoungerThanThirtySecondsGivesNoSignal()
    {
        var events = new List<CaptureEvent>();
        for (var t = 0; t < 360; t++) { events.Add(Trade(t, 100)); events.Add(Book(t, 0)); }
        // A wall appears 10 s before the price comes to it and is gone after: too young to count.
        for (var t = 360; t < 370; t++) { events.Add(Trade(t, 100)); events.Add(Book(t, 0, new() { [99.95m] = 100 })); }
        for (var t = 370; t < 400; t++) { events.Add(Trade(t, 99.9m)); events.Add(Book(t, -.1m)); }
        var detector = new SignalDetector(new Dictionary<string, decimal> { ["XUSDT"] = 10_000_000 }, liquidations: false);
        foreach (var e in events) detector.Process(e);
        Assert.DoesNotContain(detector.Result("young").Events, e => e.Type.Contains("плотности"));
    }

    [Theory]
    [InlineData(2026, 10, 17, 2, 0, "asia")]
    [InlineData(2026, 10, 17, 9, 30, "europe")]
    [InlineData(2026, 10, 17, 15, 0, "us")]
    [InlineData(2026, 10, 17, 22, 59, "late")]
    public void TradingSessionsFollowTheUtcHour(int y, int mo, int d, int h, int mi, string session) =>
        Assert.Equal(session, TradingSessions.Of(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));
}
