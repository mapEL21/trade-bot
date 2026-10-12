using TradeBot.Recorder;

namespace TradeBot.Api;

public sealed record ReplaySettings(int CalibrationSeconds = 120, decimal ImpulseBps = 3, int WindowMs = 250,
    int MaxAgeMs = 500, decimal FeeBps = 5, decimal SlippageBps = 1, decimal MinEdgeBps = 2,
    decimal StopLossBps = 20, int MaxHoldMs = 2000, decimal LeaderAdvantageBps = .5m,
    string ExecutionModel = "depth", decimal OrderNotional = 10, decimal PriceLimitBps = 5,
    string EntryOrder = "taker", decimal MakerFeeBps = 2, int MakerWaitMs = 1000, decimal? OrderQuantity = null,
    string Leader = "auto", decimal? ReversalBps = null)
{
    // ReversalBps: how far the leader must move against the position within WindowMs to exit; null keeps the entry impulse.
    // Leader: "source" — the signal exchange chosen by the user leads and trades go to the other exchange; "auto" — the
    // leader is picked from the calibration window, which may find none. In both cases the window sets the normal basis.
    public void Validate()
    {
        new AnalysisSettings(ImpulseBps, WindowMs, 100, MaxHoldMs, MaxAgeMs, FeeBps, SlippageBps).Validate();
        if (CalibrationSeconds is < 30 or > 1800 || MinEdgeBps is < 0 or > 100 || StopLossBps is < 1 or > 1000
            || LeaderAdvantageBps is < 0.1m or > 100)
            throw new ArgumentException("Проверьте параметры стратегии: обучение 30–1800 с, запас 0–100 б.п., стоп 1–1000 б.п., преимущество лидера 0,1–100 б.п.");
        if (ExecutionModel is not ("depth" or "bbo") || OrderNotional is < 1 or > 100000 || PriceLimitBps is < 0 or > 100)
            throw new ArgumentException("Модель исполнения: depth или bbo; номинал заявки 1–100000, предел цены 0–100 б.п.");
        if (EntryOrder is not ("taker" or "maker") || MakerFeeBps is < -10 or > 100 || MakerWaitMs is < 10 or > 60000)
            throw new ArgumentException("Вход: taker или maker; комиссия maker от −10 до 100 б.п., ожидание исполнения 10–60000 мс.");
        if (OrderQuantity is <= 0 or > 1_000_000_000)
            throw new ArgumentException("Размер заявки в монетах должен быть больше нуля.");
        if (ReversalBps is < 0.1m or > 1000)
            throw new ArgumentException("Разворот поводыря для выхода — от 0,1 до 1000 б.п.");
        if (Leader is not ("auto" or "source"))
            throw new ArgumentException("Поводырь: auto (определить по начальному участку) или source (биржа сигнала).");
    }
}
public sealed record ReplayDecision(double Seconds, string Action, string Reason, string? Side = null, decimal? Price = null, decimal? NetBps = null, decimal? Quantity = null);
// MaxFavorableBps / MaxAdverseBps: the best and worst price move in the trade's direction, before fees, by the executable
// exit price seen while the position was open (including the final exit). Null in records made before they existed.
public sealed record ReplayTrade(double EntrySeconds, double ExitSeconds, string Side, decimal EntryPrice, decimal ExitPrice, decimal NetBps, string ExitReason,
    decimal? Quantity = null, decimal? NetQuote = null, decimal? FeesQuote = null, decimal? MaxFavorableBps = null, decimal? MaxAdverseBps = null);
public sealed record ReplayScenario(string Mode, int DelayMs, int Trades, decimal? MeanNetBps, double? WinPercent,
    decimal SumNetBps, decimal ClosedTradeDrawdownBps, int UnresolvedPositions, int Skipped, int Signals,
    ReplayTrade[] TradeLog, ReplayDecision[] Journal, bool JournalTruncated, int PartialEntries = 0, int PartialExits = 0,
    decimal? ClosedNetQuote = null, decimal? UnresolvedQuantity = null, int? MakerOrders = null, int? MakerFills = null,
    int? OrdersSent = null, int? EntriesFilled = null, int? OrdersUnfilled = null);
public sealed record ReplayLeader(string SourceExchange, string Exchange, int Samples, decimal? ResponseBps);
public sealed record ReplayReport(string Strategy, string Version, AnalysisRecording Recording, ReplaySettings Settings,
    string Status, string Explanation, string? Leader, string? ExecutionExchange, decimal? NormalBasisBps,
    double DurationSeconds, ReplayLeader[] Candidates, ReplayScenario[] Scenarios, string[] Notes, ReplayInstrument? Instrument = null);

// Pure offline execution: local arrival times only; no API keys, networking, orders or account state.
public static class StrategyReplay
{
    public const string Version = "0.3.2";
    public static ReplayReport Run(AnalysisRecording recording, ReplaySettings settings, long frequency, long first, long last,
        List<MarketAnalysis.Input> input, CancellationToken ct, ReplayMarketData? market = null)
    {
        settings.Validate();
        if (frequency <= 0 || frequency > 1_000_000_000_000 || first < 0 || last < first)
            throw new InvalidDataException("Некорректные временные метки записи.");
        var duration = (last - first) / (double)frequency;
        if (duration > 86400) throw new InvalidDataException("Неподдерживаемая длительность записи.");
        var cutoff = checked(first + (long)settings.CalibrationSeconds * frequency);
        var calibration = input.Where(x => x.Ticks < cutoff).ToList();
        var analysis = MarketAnalysis.Analyze(recording, new(settings.ImpulseBps, settings.WindowMs, 100, 100,
            settings.MaxAgeMs, settings.FeeBps, settings.SlippageBps), frequency, first, Math.Min(last, cutoff), calibration.Count, 0, calibration, ct);
        var candidates = analysis.Directions.Select(d => new ReplayLeader(d.SourceExchange, d.Exchange,
            d.Horizons[0].Samples, d.Horizons[0].MeanMoveBps)).ToArray();
        string[] notes = [
            $"Исследовательская стратегия v{Version}: поводырь выбран только на начальном участке и зафиксирован. Это предварительная гипотеза лидерства по реакции через 100 мс, не доказанная причинность.",
            settings.ExecutionModel == "depth"
                ? "Исполнение по видимому стакану: условная немедленная заявка с пределом цены от bid/ask в момент решения. Размер округляется вниз. Цена — средняя по уровням плюс заданное дополнительное проскальзывание; комиссия за обе стороны. Это модель IOC, а не точная эмуляция правил брокера."
                : "Упрощённая модель BBO: последние свежие bid/ask, проскальзывание и комиссия. Объём, минимальная заявка и частичные исполнения не учитываются. Номинал и предел цены в этой модели не используются.",
            "Одна позиция на сценарий. Задержка — время от отправки заявки до её исполнения на бирже, она применяется и к входу, и к выходу. Все условия входа проверяются в момент сигнала; отправленная заявка исполняется на зафиксированных условиях, даже если отставание к тому времени исчезло. Потеря данных ведомой биржи до исполнения даёт неопределённый результат, а не отмену. При потере свежести или разрыве во время позиции сценарий останавливается с неоценённой позицией, без выдуманного закрытия.",
            "В модели стакана сумма и просадка в б.п. нормированы на заданный номинал, средний итог — на фактический объём каждой закрытой сделки. Если выход не уложился в предел цены, остаток закрывается новой заявкой после следующего обновления котировок, а сделка получает среднюю цену всех частей выхода. Если исполнение за записанной глубиной неизвестно, остаток не закрывается выдуманной ценой: сценарий прекращается, вся незавершённая сделка исключена из итогов закрытых сделок.",
            "Используется только записанная глубина, без скрытых заявок. Если исполнение отправленной заявки за последним записанным уровнем неизвестно, результат не определён и сценарий прекращается. Запись не отражает влияние нашего бота на дальнейший рынок. Плечо, маржа, funding, ликвидация, ценовые коридоры биржи и персональные лимиты не моделируются; тариф 5 б.п. — пример.",
            "Контрольный вход против импульса использует те же первоначальные сигналы отбора, но не гарантирует доступного движения в обратную сторону. Сценарии независимы; результаты нельзя складывать.",
            "Выбор лучшей задержки на этой записи требует новой независимой проверки. Положительный результат не подтверждает прибыльность. Живая симуляция и торговое исполнение ещё не подключены.",
            .. (settings.EntryOrder == "maker" ? new[] { "Вход maker (экспериментальная модель): post-only заявка по своей лучшей цене (bid для покупки, ask для продажи) на момент решения, с той же задержкой доставки; отклоняется, если цена уже дошла до её уровня. В модели стакана заявка встаёт в конец очереди своего уровня: исполненный объём = записанные сделки агрессора по её цене или хуже минус видимый объём перед ней, не больше размера заявки. Уход заявок из очереди не учитывается. При частичном исполнении остаток отменяется, и пока отмена идёт, может исполниться ещё часть. В упрощённой модели BBO объём неизвестен: исполнение засчитывается целиком, когда встречная цена дошла до уровня заявки. Разрыв потока сделок, устаревание котировок ведомой биржи или конец записи при выставленной заявке дают неопределённый результат. Выход — taker." } : Array.Empty<string>())
        ];
        ReplayReport Empty(string status, string reason) => new("leader-lag", Version, recording, settings, status, reason,
            null, null, null, duration, candidates, [], notes);
        if (recording.Status != "completed") return Empty("incomplete", "Нужна запись без ошибок и разрывов: текущая сессия завершена неполно.");
        if (duration <= settings.CalibrationSeconds + settings.MaxHoldMs / 1000.0 + .2)
            return Empty("too-short", "После участка выбора поводыря не осталось достаточно данных для проверки сделок.");
        if (ChooseLeader(candidates, settings, recording.SourceExchange, recording.Exchange) is not { } best)
            return Empty("no-leader", "Поводырь не определён: нужны минимум 10 наблюдений в каждом направлении, положительная реакция и достаточное преимущество. Сделки не моделировались.");
        var ticks = Normalize(recording, frequency, first, input, ct);
        var leader = best.SourceExchange == recording.SourceExchange ? 0 : 1;
        var instrument = market?.Instruments[1 - leader];
        if (settings.ExecutionModel == "depth" && (instrument is null || market!.Books.All(x => x.Venue != 1 - leader || x.Update is null)))
            return Empty("no-execution-data", "Для выбранной отстающей биржи нет сохранённых правил размера заявки или глубины стакана. Нужна новая запись либо явно выбранная упрощённая модель BBO.");
        var makerDepth = settings.ExecutionModel == "depth" && settings.EntryOrder == "maker";
        if (makerDepth && market!.Trades?.Any(x => x.Venue == 1 - leader && x.Prints is not null) != true)
            return Empty("no-execution-data", "Для maker-входа по стакану нужны записанные сделки отстающей биржи: по ним считается объём, прошедший через цену заявки. Нужна запись со сделками либо упрощённая модель BBO.");
        if (NormalBasis(ticks, leader, settings, ct) is not { } basis)
            return Empty("no-basis", "Недостаточно совместных свежих котировок для оценки обычной разницы цен.");
        if (settings.ExecutionModel == "depth")
        {
            ticks.AddRange(market!.Books.Where(x => x.Venue == 1 - leader && x.Ticks >= first && x.Ticks <= last).Select(x =>
                new EngineEvent((x.Ticks - first) * 1000.0 / frequency, x.Venue, null, false, x.Sequence, x.Connection, true, x.Update)));
            if (makerDepth)
                ticks.AddRange(market.Trades!.Where(x => x.Venue == 1 - leader && x.Ticks >= first && x.Ticks <= last).Select(x =>
                    new EngineEvent((x.Ticks - first) * 1000.0 / frequency, x.Venue, null, false, x.Sequence, x.Connection, IsTrade: true, Prints: x.Prints)));
            ticks = ticks.OrderBy(x => x.Ms).ThenBy(x => x.Sequence).ToList();
        }
        var scenarios = new List<ReplayScenario>();
        foreach (var delay in new[] { 10, 25, 50, 100 })
            foreach (var counter in new[] { false, true }) scenarios.Add(LeaderLagEngine.Run(ticks, new(leader, basis, settings, delay, counter, instrument), ct, duration * 1000));
        return new("leader-lag", Version, recording, settings, "evaluated", (settings.Leader == "source"
                ? "Поводырь задан: биржа сигнала. Начальный участок использован для обычной разницы цен. "
                : "Поводырь выбран на начальном участке. ") + LeaderNote(candidates, best) + " Результаты ниже относятся только к последующей части записи.",
            best.SourceExchange, best.Exchange, basis, duration, candidates, scenarios.ToArray(), notes, settings.ExecutionModel == "depth" ? instrument : null);
    }

    // A fixed leader is the signal exchange whatever the calibration window showed; otherwise it is selected from it.
    public static ReplayLeader? ChooseLeader(ReplayLeader[] candidates, ReplaySettings settings, string source, string exchange) =>
        settings.Leader == "source"
            ? candidates.FirstOrDefault(x => x.SourceExchange == source) ?? new(source, exchange, 0, null)
            : SelectLeader(candidates, settings);

    // Measured lead on the calibration window, shown for information: with a fixed leader it does not block trading.
    public static string LeaderNote(ReplayLeader[] candidates, ReplayLeader leader)
    {
        static string Bps(decimal? v) => v is { } x ? x.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " б.п." : "нет данных";
        var other = candidates.FirstOrDefault(x => x.SourceExchange != leader.SourceExchange);
        var text = $"За начальный участок средняя реакция {leader.Exchange} на импульсы {leader.SourceExchange} через 100 мс — {Bps(leader.ResponseBps)} ({leader.Samples} набл.)";
        if (other is null) return text + ".";
        text += $", обратная — {Bps(other.ResponseBps)} ({other.Samples} набл.).";
        return other.ResponseBps > (leader.ResponseBps ?? decimal.MinValue)
            ? text + " Обратная реакция сильнее: на этом участке раньше двигалась биржа исполнения, сигнал может быть слабым."
            : text;
    }

    // The leader must react positively, with enough samples both ways and a margin over the reverse direction.
    public static ReplayLeader? SelectLeader(ReplayLeader[] candidates, ReplaySettings settings)
    {
        var best = candidates.OrderByDescending(x => x.ResponseBps ?? decimal.MinValue).First();
        var other = candidates.Single(x => x.SourceExchange != best.SourceExchange);
        return candidates.Any(x => x.Samples < 10) || best.ResponseBps is null or <= 0 || other.ResponseBps is null
            || best.ResponseBps.Value - other.ResponseBps.Value < settings.LeaderAdvantageBps ? null : best;
    }

    // Median leader/follower mid gap over the calibration window, sampled at most every 100 ms; null below 100 samples.
    public static decimal? NormalBasis(IEnumerable<EngineEvent> ticks, int leader, ReplaySettings settings, CancellationToken ct)
    {
        var quotes = new[] { new VenueQuotes(), new VenueQuotes() };
        var bases = new List<decimal>(); double sampledAt = -100;
        foreach (var tick in ticks.TakeWhile(t => t.Ms < settings.CalibrationSeconds * 1000))
        {
            ct.ThrowIfCancellationRequested(); quotes[tick.Venue].Apply(tick);
            if (tick.Ms - sampledAt < 100 || quotes.Any(q => !q.Fresh(tick.Ms, settings.MaxAgeMs))) continue;
            bases.Add((quotes[leader].Current!.Mid / quotes[1 - leader].Current!.Mid - 1) * 10000); sampledAt = tick.Ms;
        }
        if (bases.Count < 100) return null;
        bases.Sort(); return bases[bases.Count / 2];
    }

    private static List<EngineEvent> Normalize(AnalysisRecording recording, long frequency, long first, List<MarketAnalysis.Input> input, CancellationToken ct)
    {
        var normalizer = new QuoteNormalizer(recording.SourceExchange, recording.Exchange); var result = new List<EngineEvent>();
        foreach (var item in input.OrderBy(x => x.Ticks).ThenBy(x => x.Sequence))
        {
            ct.ThrowIfCancellationRequested();
            if (normalizer.Push(item, (item.Ticks - first) * 1000.0 / frequency) is { } tick) result.Add(tick);
        }
        return result;
    }
}