using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

public sealed class SignalLabTests : IDisposable
{
    private const long T0 = 1_700_000_000_000, Burst = T0 + 400_000;
    private readonly string root = Path.Combine(Path.GetTempPath(), "signal-lab-tests-" + Guid.NewGuid());
    private static string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);

    private static CaptureEvent Trade(long ms, decimal price, decimal quantity, bool buy) => new(ScanRecorder.Market, "c", "market", DateTimeOffset.UnixEpoch, ms,
        $"{{\"stream\":\"xusdt@aggTrade\",\"data\":{{\"e\":\"aggTrade\",\"s\":\"XUSDT\",\"p\":\"{S(price)}\",\"q\":\"{S(quantity)}\",\"T\":{ms},\"m\":{(!buy).ToString().ToLowerInvariant()}}}}}");
    private static CaptureEvent Book(long ms, decimal bid, decimal ask) => new(ScanRecorder.Public, "c", "market", DateTimeOffset.UnixEpoch, ms,
        $"{{\"stream\":\"xusdt@depth20@500ms\",\"data\":{{\"e\":\"depthUpdate\",\"E\":{ms},\"T\":{ms},\"s\":\"XUSDT\",\"b\":[[\"{S(bid)}\",\"10\"]],\"a\":[[\"{S(ask)}\",\"10\"]]}}}}");

    // 400 s of one small trade a second at 100 with a 99.99/100.01 book, then 20 large buys in 2 s lifting the price, after
    // which the book sits at 100.19/100.21. bookUntil: the last moment the book is updated (the stream "freezes" after it).
    private static List<CaptureEvent> Recording(long bookUntil = long.MaxValue, long bookFrom = long.MinValue)
    {
        var events = new List<CaptureEvent>();
        for (var ms = T0; ms < Burst + 400_000; ms += 500)
        {
            var after = ms >= Burst + 500;
            if (ms <= bookUntil && ms >= bookFrom) events.Add(Book(ms, after ? 100.19m : 99.99m, after ? 100.21m : 100.01m));
            if (ms % 1000 == 0 && (ms < Burst || ms >= Burst + 2000)) events.Add(Trade(ms, ms < Burst ? 100 : 100.2m, 1, ms % 2000 == 0));
        }
        for (var k = 0; k < 20; k++) events.Add(Trade(Burst + k * 100, 100.1m + .005m * k, 50, true));
        return events.OrderBy(e => e.ReceivedTicks).ToList();
    }

    private static LabRecording Detect(List<CaptureEvent> events)
    {
        var detector = new SignalDetector(new Dictionary<string, decimal> { ["XUSDT"] = 300_000_000 }, liquidations: true);
        foreach (var e in events) detector.Process(e);
        return detector.Result("test");
    }

    [Fact]
    public void BurstOfBuysIsFoundOnceAndMeasuredAtTheOppositeBestPrices()
    {
        var result = Detect(Recording());
        var burst = Assert.Single(result.Events, e => e.Type == "Всплеск объёма");
        Assert.Equal(1, burst.Direction);
        Assert.Equal(Burst, burst.AtMs);
        Assert.Single(result.Events, e => e.Type == "Перекос агрессора" && e.Direction == 1);
        // The recording's span is its market data, from the first update, not from the first event.
        Assert.Equal(T0, result.FirstMs);
        // Entry 250 ms later at the ask (100.01) for a long, the bid (99.99) for a short; exits at the book of the horizon.
        Assert.Equal((100.19m / 100.01m - 1) * 100, burst.Move(0, withEvent: true)!.Value, 10);
        Assert.Equal((99.99m / 100.21m - 1) * 100, burst.Move(0, withEvent: false)!.Value, 10);
        Assert.Equal((100.19m / 100.01m - 1) * 100, burst.Move(3, withEvent: true)!.Value, 10);
    }

    [Fact]
    public void ExitAfterTheBookStoppedUpdatingIsUnknownNotTakenFromALaterPrice()
    {
        // The book stops 10 s after the burst: the 5 s exit is known, the later ones are not.
        var burst = Assert.Single(Detect(Recording(bookUntil: Burst + 10_000)).Events, e => e.Type == "Всплеск объёма");
        Assert.NotNull(burst.Move(0, true));
        Assert.All(new[] { 1, 2, 3 }, i => Assert.Null(burst.Move(i, true)));
    }

    [Fact]
    public void EventBeforeTheFirstBookHasNoEntryInsteadOfAZeroPrice()
    {
        // No book until a second after the burst: the entry 250 ms after it is unknown, not 0.
        var burst = Assert.Single(Detect(Recording(bookFrom: Burst + 1000)).Events, e => e.Type == "Всплеск объёма");
        Assert.Null(burst.EntryAsk);
        Assert.All(Enumerable.Range(0, SignalDetector.Horizons.Length), i => Assert.Null(burst.Move(i, true)));
        // A zero price saved by an older version is never used in a calculation.
        var broken = burst with { EntryBid = 0, EntryAsk = 0, ExitBid = [100, 100, 100, 100], ExitAsk = [100, 100, 100, 100] };
        Assert.Null(broken.Move(0, true));
    }

    [Fact]
    public async Task LabProcessesFinishedRecordingsOnceAndReportsBothDirectionsAfterFees()
    {
        string Write(string id, bool finished)
        {
            var dir = Path.Combine(root, id); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new { liquidations = true, symbols = new[] { new ScanSymbol("XUSDT", 300_000_000, 100) } }));
            File.WriteAllLines(Path.Combine(dir, StorageHousekeeper.Events), Recording().Select(e => JsonSerializer.Serialize(e)));
            if (finished) File.WriteAllText(Path.Combine(dir, "summary.json"), "{}");
            return dir;
        }
        var done = Write("20261015T100000Z-11111111111111111111111111111111", true);
        var writing = Write("20261015T110000Z-22222222222222222222222222222222", false);
        using var lab = new SignalLab(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Scanner:Path"] = root }).Build(),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalLab>.Instance);
        Assert.True(lab.Start());
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (lab.Status.State == "running") await Task.Delay(50, wait.Token);
        Assert.Equal(("finished", 1, 1), (lab.Status.State, lab.Status.Done, lab.Status.Total));
        Assert.True(File.Exists(Path.Combine(done, SignalLab.File)));
        Assert.False(File.Exists(Path.Combine(writing, SignalLab.File)), "a recording still being written is not processed");
        // A second run has nothing new to do.
        Assert.True(lab.Start());
        while (lab.Status.State == "running") await Task.Delay(50, wait.Token);
        Assert.Equal(0, lab.Status.Total);

        var report = lab.Report(.1m);
        Assert.Equal((1, 1), (report.Recordings, report.Counts.Single(c => c.Type == "Всплеск объёма").Count));
        var follow = report.Rows.Single(r => r.Type == "Всплеск объёма" && r.Mode == "follow" && r.Horizon == 5);
        Assert.Equal(Math.Round((100.19m / 100.01m - 1) * 100 - .1m, 4), follow.MeanPct);
        Assert.False(follow.Stable); // one event is far from the 100 needed
        var trade = Assert.Single(lab.Trades("Всплеск объёма", "fade", 5, .1m));
        Assert.Equal(("short", 99.99m, 100.21m, .1m), (trade.Side, trade.Entry, trade.Exit, trade.Fee));
        Assert.Equal((99.99m / 100.21m - 1) * 100, trade.Gross, 10);
        Assert.Throws<ArgumentException>(() => lab.Trades("Неизвестно", "follow", 5, .1m));
        Assert.Throws<ArgumentException>(() => lab.Report(5));
    }

    [Fact]
    public void SimultaneousEventsOnManyCoinsAreOneEpisodeAndTheDateFilterKeepsOnlyNewData()
    {
        // Three coins jerk up within 5 s: one market moment; the same signal a minute later is a second one.
        static LabEvent Spike(string symbol, long at, decimal exit) => new(symbol, "Вынос цены", 1, at, 100, "", 99.99m, 100,
            [exit, exit, exit, exit], [exit + .01m, exit + .01m, exit + .01m, exit + .01m]);
        var events = new[] { Spike("AUSDT", T0, 101), Spike("BUSDT", T0 + 2000, 103), Spike("CUSDT", T0 + 5000, 102), Spike("AUSDT", T0 + 60_000, 99) };
        var episodes = SignalLab.Episodes(events);
        Assert.Equal([3, 1], episodes.Select(e => e.Count));
        // An event in the other direction at the same moment is a separate episode.
        Assert.Equal(3, SignalLab.Episodes(events.Append(events[0] with { Direction = -1 })).Count);

        var dir = Path.Combine(root, "20261015T100000Z-33333333333333333333333333333333"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summary.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(dir, SignalLab.File), JsonSerializer.Serialize(new LabRecording(SignalDetector.Version, "x", T0, T0 + 3_600_000, 3, events),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var lab = new SignalLab(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Scanner:Path"] = root }).Build(),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalLab>.Instance);
        var row = lab.Report(0).Rows.Single(r => r.Type == "Вынос цены" && r.Mode == "follow" && r.Horizon == 5);
        // Episode 1: the average of its three trades (entry at the ask 100, exits at bids 101, 103, 102); episode 2: exit 99.
        Assert.Equal((4, 2), (row.Count, row.Episodes));
        Assert.Equal(Math.Round(((1m + 3 + 2) / 3 + -1) / 2, 4), row.MeanPct);
        Assert.Equal(50, row.WinRate);
        Assert.Equal(2, lab.Report(0).Counts.Single(c => c.Type == "Вынос цены").Episodes);
        // From half a minute in: only the later episode, and only the hours after that moment.
        var recent = lab.Report(0, T0 + 30_000);
        Assert.Equal((1, 1), (recent.Events, recent.Rows.Single(r => r.Type == "Вынос цены" && r.Mode == "follow" && r.Horizon == 5).Episodes));
        Assert.True(recent.Hours < 1);
        // A recording that ended before any event (start unknown in older results) adds no hours.
        var empty = Path.Combine(root, "20261015T110000Z-66666666666666666666666666666666"); Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "summary.json"), "{}");
        File.WriteAllText(Path.Combine(empty, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(empty, SignalLab.File), JsonSerializer.Serialize(new LabRecording(SignalDetector.Version, "e", 0, T0 + 60_000, 3, []),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(1m, lab.Report(0).Hours);
        Assert.Single(lab.Trades("Вынос цены", "follow", 5, 0, T0 + 30_000));
        // By session: T0 is 22:13 UTC, the quiet hours; the other sessions have neither events nor hours here.
        var sessions = lab.Sessions("Вынос цены", "follow", 5, 0);
        Assert.Equal(["asia", "europe", "us", "late"], sessions.Select(x => x.Session));
        var late = sessions.Single(x => x.Session == "late");
        Assert.Equal(2, late.Episodes);
        Assert.Equal(row.MeanPct, late.MeanPct);
        Assert.Equal((0, (decimal?)null), (sessions.Single(x => x.Session == "asia").Episodes, sessions.Single(x => x.Session == "asia").MeanPct));
        Assert.Throws<ArgumentException>(() => lab.Report(0, null, "mars"));
    }

    [Theory]
    // 49 good episodes: still collecting; 50 good: confirmed; 50 with a losing second half: rejected for that signal.
    [InlineData(49, 50, "collecting")]
    [InlineData(50, 50, "confirmed")]
    [InlineData(50, 25, "collecting")]
    public void HypothesisIsJudgedOnlyOnEventsFromItsStartWithTheRuleFixedInAdvance(int count, int good, string verdict)
    {
        // A spike every minute from the start moment; exits at 101 make +1% (before 0.1% fees), at 99 −1%. Older events, all
        // losing, sit before the start and must not count.
        var from = SignalLab.HypothesisFromMs;
        static LabEvent Spike(long at, decimal exit) => new("AUSDT", "Вынос цены", 1, at, 100, "", 99.99m, 100,
            [exit, exit, exit, exit], [exit + .01m, exit + .01m, exit + .01m, exit + .01m]);
        var events = Enumerable.Range(0, count).Select(k => Spike(from + k * 60_000L, k < good ? 101 : 99))
            .Concat(Enumerable.Range(1, 20).Select(k => Spike(from - k * 60_000L, 99))).ToArray();
        var dir = Path.Combine(root, "20261016T000000Z-44444444444444444444444444444444"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summary.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(dir, SignalLab.File), JsonSerializer.Serialize(
            new LabRecording(SignalDetector.Version, "x", from - 3_600_000, from + 3_600_000, 1, events), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var lab = new SignalLab(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Scanner:Path"] = root }).Build(),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalLab>.Instance);
        var check = lab.Check(SignalLab.Hypotheses[0]);
        var spike = check.Signals.Single(x => x.Type == "Вынос цены");
        Assert.Equal((count, 50), (spike.Episodes, spike.Needed));
        Assert.Equal(count == 50 && good == 25 ? "rejected" : verdict == "confirmed" ? "confirmed" : "collecting", spike.Verdict);
        // No cascades were recorded: that signal is still collecting, so one rejected signal alone does not reject the hypothesis.
        Assert.Equal(("collecting", 0), (check.Signals.Single(x => x.Type == "Каскад ликвидаций").Verdict, check.Signals.Single(x => x.Type == "Каскад ликвидаций").Episodes));
        Assert.Equal(verdict, check.Verdict);
        Assert.Equal(.1m, check.FeePct);
    }

    [Fact]
    public void TheSecondHypothesisCountsOnlyDataAfterTheRecordingsItWasChosenOnAndKeepsTheFirstInTheHistory()
    {
        var current = SignalLab.Hypotheses[^1];
        Assert.Equal(2, current.Number);
        // The last recording the choice was made on ended on 17.10.2026 at 09:27 MSK.
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.FromHours(3)).ToUnixTimeMilliseconds(), current.FromMs);
        Assert.Equal((100, ("Вынос цены", "follow", 60)), (current.Episodes, Assert.Single(current.Signals)));
        using var lab = new SignalLab(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Scanner:Path"] = root }).Build(),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalLab>.Instance);
        var check = lab.Hypothesis();
        Assert.Equal((2, "collecting", 100), (check.Number, check.Verdict, check.Signals[0].Needed));
        Assert.Equal(1, Assert.Single(check.Previous!).Number);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
