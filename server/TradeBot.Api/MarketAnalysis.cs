using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record AnalysisSettings(decimal ImpulseBps = 3, int WindowMs = 250, int EntryDelayMs = 100,
    int HoldMs = 1000, int MaxAgeMs = 1000, decimal FeeBps = 5, decimal SlippageBps = 1)
{
    public void Validate()
    {
        if (ImpulseBps is < 0.1m or > 1000 || WindowMs is < 10 or > 5000 || EntryDelayMs is < 0 or > 5000
            || HoldMs is < 100 or > 10000 || MaxAgeMs is < 10 or > 5000 || FeeBps is < 0 or > 100 || SlippageBps is < 0 or > 100)
            throw new ArgumentException("Проверьте параметры анализа: импульс 0,1–1000 б.п., окно 10–5000 мс, задержка 0–5000 мс, удержание 100–10000 мс, свежесть 10–5000 мс, комиссия и проскальзывание 0–100 б.п.");
    }
}
public sealed record AnalysisRecording(string Id, string Coin, string SourceExchange, string Exchange, DateTimeOffset StartedUtc,
    string Status, long Bytes, bool HasReport);
public sealed record AnalysisFeed(string Exchange, int Quotes, decimal? AverageSpreadBps);
public sealed record HorizonResult(int HorizonMs, int Samples, double? SameDirectionPercent, decimal? MeanMoveBps);
public sealed record ModelTrade(double AtSeconds, string Side, decimal GrossBps, decimal NetBps);
public sealed record ModelResult(int Trades, int Excluded, double? WinPercent, decimal? MeanGrossBps, decimal? MeanNetBps,
    decimal? MeanFeeBps, ModelTrade[] Examples);
public sealed record DirectionAnalysis(string SourceExchange, string Exchange, int Impulses, HorizonResult[] Horizons, ModelResult Model);
public sealed record AnalysisReport(int Version, AnalysisRecording Recording, AnalysisSettings Settings, DateTimeOffset AnalyzedUtc,
    double DurationSeconds, long Events, int Gaps, int InvalidQuotes, int OutOfOrderQuotes, double CoveragePercent,
    AnalysisFeed[] Feeds, DirectionAnalysis[] Directions, string[] Notes);

public static class MarketAnalysis
{
    public const int MaxEvents = 1_000_000;
    public const int MaxPoints = 300_000;
    public static readonly int[] Horizons = [100, 250, 500, 1000, 2000, 5000];
    public sealed record Input(long Ticks, int Sequence, string Source, string Connection, string Kind, MarketQuote? Quote);
    private sealed record Point(double Ms, int Segment, MarketQuote? Quote);
    private sealed class Timeline
    {
        public readonly List<Point> Points = [];
        public string? Connection;
        public MarketQuote? Last;
        public int Segment;
        public int Quotes;
        public decimal SpreadSum;
        public Point? At(double ms, int maxAge)
        {
            var point = PointAt(ms);
            return point?.Quote is not null && ms - point.Ms <= maxAge ? point : null;
        }
        public Point? PointAt(double ms)
        {
            var lo = 0; var hi = Points.Count - 1; var found = -1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (Points[mid].Ms <= ms) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return found < 0 ? null : Points[found];
        }
    }

    public static AnalysisReport Analyze(AnalysisRecording recording, AnalysisSettings settings, long frequency,
        long firstTicks, long lastTicks, long events, int gaps, List<Input> input, CancellationToken ct)
    {
        settings.Validate();
        if (frequency <= 0 || frequency > 1_000_000_000_000 || firstTicks < 0 || lastTicks < firstTicks)
            throw new InvalidDataException("Некорректные временные метки записи.");
        var durationMs = (lastTicks - firstTicks) / (double)frequency * 1000;
        if (durationMs > 86_400_000) throw new InvalidDataException("Неподдерживаемая длительность записи.");
        var source = new Timeline(); var target = new Timeline();
        var sourceId = RecorderOptions.QuoteFeedId(recording.SourceExchange);
        var targetId = RecorderOptions.QuoteFeedId(recording.Exchange);
        var invalid = 0; var outOfOrder = 0;
        foreach (var item in input.OrderBy(x => x.Ticks).ThenBy(x => x.Sequence))
        {
            ct.ThrowIfCancellationRequested();
            var timeline = item.Source == sourceId ? source : item.Source == targetId ? target : null;
            if (timeline is null) continue;
            var ms = (item.Ticks - firstTicks) / (double)frequency * 1000;
            if (item.Kind is "connecting" or "connected" or "gap")
            {
                timeline.Connection = item.Connection;
                timeline.Last = null;
                timeline.Points.Add(new(ms, ++timeline.Segment, null));
                continue;
            }
            if (item.Connection != timeline.Connection) { outOfOrder++; continue; }
            if (item.Quote is null)
            {
                invalid++;
                timeline.Last = null;
                timeline.Points.Add(new(ms, ++timeline.Segment, null));
                continue;
            }
            if (timeline.Last is { } previous && item.Quote.SourceOrder < previous.SourceOrder) { outOfOrder++; continue; }
            timeline.Last = item.Quote;
            timeline.Quotes++;
            timeline.SpreadSum += item.Quote.SpreadBps;
            timeline.Points.Add(new(ms, timeline.Segment, item.Quote));
        }
        var coverage = Coverage(source, target, durationMs, settings.MaxAgeMs, ct);
        var directions = new[] {
            Direction(source, target, recording.SourceExchange, recording.Exchange, durationMs, settings, ct),
            Direction(target, source, recording.Exchange, recording.SourceExchange, durationMs, settings, ct)
        };
        var notes = new List<string>();
        if (durationMs < 600_000) notes.Add("Запись короче 10 минут: подходит для проверки подключения, но не для вывода о стратегии.");
        if (directions.Any(d => d.Model.Trades < 30)) notes.Add("Менее 30 оценённых входов хотя бы в одном направлении. Результат слишком чувствителен к отдельным движениям.");
        if (coverage < 90) notes.Add("Свежие котировки обеих площадок доступны менее 90% времени. Пропущенные окна не считаются нулевым результатом.");
        if (gaps > 0 || invalid > 0 || recording.Status != "completed") notes.Add("В записи есть разрывы, некорректные котировки или неполное завершение. Окна через разрывы котировок исключены.");
        notes.Add("Измеряется порядок получения данных этим компьютером, а не доказанное опережение биржевых движков. Часы бирж не используются для выравнивания.");
        notes.Add("Модель использует лучшие bid/ask, задержку, комиссию и заданное проскальзывание. Объём заявки, очередь, глубина, funding и реальное исполнение не моделируются. Параметры комиссии — ваш сценарий, не тариф аккаунта.");
        notes.Add("Это описание одной записи, без проверки статистической значимости. Подобранные здесь параметры нужно проверить на новых записях; положительный результат не подтверждает прибыльность.");
        return new(1, recording, settings, DateTimeOffset.UtcNow, durationMs / 1000, events, gaps, invalid, outOfOrder, coverage,
            [Feed(recording.SourceExchange, source), Feed(recording.Exchange, target)], directions, notes.ToArray());
    }

    private static AnalysisFeed Feed(string name, Timeline timeline) => new(name, timeline.Quotes,
        timeline.Quotes == 0 ? null : timeline.SpreadSum / timeline.Quotes);

    private static double Coverage(Timeline source, Timeline target, double end, int age, CancellationToken ct)
    {
        if (end <= 0) return 0;
        var boundaries = source.Points.Concat(target.Points).SelectMany(p => new[] { p.Ms, Math.Min(end, p.Ms + age) })
            .Append(0).Append(end).Distinct().Order().ToArray();
        double covered = 0;
        for (var i = 1; i < boundaries.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var middle = (boundaries[i] + boundaries[i - 1]) / 2;
            if (source.At(middle, age) is not null && target.At(middle, age) is not null) covered += boundaries[i] - boundaries[i - 1];
        }
        return covered / end * 100;
    }

    private static DirectionAnalysis Direction(Timeline source, Timeline target, string sourceName, string targetName,
        double end, AnalysisSettings settings, CancellationToken ct)
    {
        var moves = Horizons.Select(_ => new List<decimal>()).ToArray();
        var trades = new List<ModelTrade>();
        decimal feeSum = 0;
        int impulses = 0, excluded = 0;
        double nextAllowed = 0;
        var cooldown = Math.Max(Horizons.Max(), settings.EntryDelayMs + settings.HoldMs);
        Point? previous = null;
        foreach (var point in source.Points)
        {
            ct.ThrowIfCancellationRequested();
            var changed = point.Quote is not null && previous?.Quote?.Mid != point.Quote.Mid;
            previous = point;
            if (!changed || point.Ms < nextAllowed) continue;
            var baseline = source.At(point.Ms - settings.WindowMs, settings.MaxAgeMs);
            var targetNow = target.At(point.Ms, settings.MaxAgeMs);
            if (baseline?.Quote is null || baseline.Segment != point.Segment || targetNow?.Quote is null) continue;
            var impulse = (point.Quote!.Mid / baseline.Quote.Mid - 1) * 10000;
            if (Math.Abs(impulse) < settings.ImpulseBps) continue;
            var sign = impulse > 0 ? 1 : -1;
            impulses++;
            nextAllowed = point.Ms + cooldown;
            for (var i = 0; i < Horizons.Length; i++)
            {
                var at = point.Ms + Horizons[i];
                var future = target.At(at, settings.MaxAgeMs);
                if (at <= end && future?.Quote is not null && future.Segment == targetNow.Segment
                    && source.PointAt(at)?.Segment == point.Segment)
                    moves[i].Add(sign * (future.Quote.Mid / targetNow.Quote.Mid - 1) * 10000);
            }
            var entryTime = point.Ms + settings.EntryDelayMs;
            var exitTime = entryTime + settings.HoldMs;
            var entry = target.At(entryTime, settings.MaxAgeMs);
            var exit = target.At(exitTime, settings.MaxAgeMs);
            if (exitTime > end || entry?.Quote is null || exit?.Quote is null || entry.Segment != targetNow.Segment
                || exit.Segment != targetNow.Segment || source.PointAt(exitTime)?.Segment != point.Segment)
            { excluded++; continue; }
            var slip = settings.SlippageBps / 10000;
            var entryPrice = sign > 0 ? entry.Quote.Ask * (1 + slip) : entry.Quote.Bid * (1 - slip);
            var exitPrice = sign > 0 ? exit.Quote.Bid * (1 - slip) : exit.Quote.Ask * (1 + slip);
            var gross = sign * (exitPrice / entryPrice - 1) * 10000;
            var fee = settings.FeeBps * (1 + exitPrice / entryPrice);
            feeSum += fee;
            trades.Add(new(point.Ms / 1000, sign > 0 ? "long" : "short", gross, gross - fee));
        }
        var horizons = Horizons.Select((h, i) => new HorizonResult(h, moves[i].Count,
            moves[i].Count == 0 ? null : 100.0 * moves[i].Count(x => x > 0) / moves[i].Count,
            moves[i].Count == 0 ? null : moves[i].Average())).ToArray();
        var model = new ModelResult(trades.Count, excluded, trades.Count == 0 ? null : 100.0 * trades.Count(t => t.NetBps > 0) / trades.Count,
            trades.Count == 0 ? null : trades.Average(t => t.GrossBps), trades.Count == 0 ? null : trades.Average(t => t.NetBps),
            trades.Count == 0 ? null : feeSum / trades.Count, trades.Take(20).ToArray());
        return new(sourceName, targetName, impulses, horizons, model);
    }
}
