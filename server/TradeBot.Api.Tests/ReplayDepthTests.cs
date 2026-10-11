using System.Text.Json;
using TradeBot.Api;
using TradeBot.Recorder;
using Xunit;

namespace TradeBot.Api.Tests;

public class ReplayDepthTests
{
    private static readonly ReplayInstrument Instrument = new(1, 1, 5, 10000, 1, .01m, "USDT", "fixture");
    private static readonly AnalysisRecording Recording = new("fixture", "SOL", "hyperliquid", "binance", DateTimeOffset.UnixEpoch, "completed", 0, false);
    private static readonly ReplaySettings Settings = new(FeeBps: 1, SlippageBps: 0, MinEdgeBps: 1, OrderNotional: 1200);
    private static ReplayMarketData Market(List<MarketAnalysis.Input>? data = null) => new([null, Instrument], (data ?? StrategyReplayTests.Data())
        .Where(x => x.Source == "binance-public" && x.Quote is not null)
        .Select(x => new ReplayBookEvent(x.Ticks, x.Sequence + 1, 1, "c", new(true, x.Ticks,
            [new(x.Quote!.Bid, 100), new(x.Quote.Bid - .02m, 100)],
            [new(x.Quote.Ask, 2), new(x.Quote.Ask + .02m, 100)]))).ToList());
    private static ReplayReport Run(ReplayMarketData? market, ReplaySettings? settings = null) =>
        StrategyReplay.Run(Recording, settings ?? Settings, 1000, 0, 125000, StrategyReplayTests.Data(), default, market);
    private static ReplayScenario Follow(ReplayReport report) => report.Scenarios.Single(x => x.Mode == "follow" && x.DelayMs == 10);

    [Theory]
    [InlineData("binance-public", """{"stream":"solusdt@depth20@100ms","data":{"e":"depthUpdate","s":"SOLUSDT","u":1,"b":[["99","2"]],"a":[["101","3"]]}}""")]
    [InlineData("bybit-public", """{"topic":"orderbook.50.SOLUSDT","type":"snapshot","data":{"s":"SOLUSDT","u":1,"b":[["99","2"]],"a":[["101","3"]]}}""")]
    [InlineData("okx-public", """{"arg":{"channel":"books5","instId":"SOL-USDT-SWAP"},"action":"snapshot","data":[{"ts":"1","bids":[["99","2","0","1"]],"asks":[["101","3","0","1"]]}]}""")]
    [InlineData("okx-public", """{"arg":{"channel":"books5","instId":"SOL-USDT-SWAP"},"data":[{"ts":"1","seqId":100,"bids":[["99","2","0","1"]],"asks":[["101","3","0","1"]]}]}""")]
    [InlineData("hyperliquid", """{"channel":"l2Book","data":{"coin":"SOL","time":1,"levels":[[{"px":"99","sz":"2"}],[{"px":"101","sz":"3"}]]}}""")]
    public void ReadsOnlyRecordedDepthChannels(string source, string payload)
    {
        Assert.True(ReplayDepthParser.TryRead(new(source, "c", "market", DateTimeOffset.UnixEpoch, 1, payload), "SOL", out var book));
        Assert.NotNull(book); Assert.True(book.Snapshot);
        Assert.Equal(new ReplayLevel(99, 2), Assert.Single(book.Bids)); Assert.Equal(new ReplayLevel(101, 3), Assert.Single(book.Asks));
    }

    [Theory]
    [InlineData("binance-market", """{"e":"aggTrade","s":"SOLUSDT","p":"100.5","q":"3","m":true}""")]
    [InlineData("binance-market", """{"stream":"solusdt@aggTrade","data":{"e":"aggTrade","s":"SOLUSDT","p":"100.5","q":"3","m":true}}""")]
    [InlineData("bybit-market", """{"topic":"publicTrade.SOLUSDT","type":"snapshot","data":[{"s":"SOLUSDT","S":"Sell","v":"3","p":"100.5"}]}""")]
    [InlineData("okx-market", """{"arg":{"channel":"trades","instId":"SOL-USDT-SWAP"},"data":[{"instId":"SOL-USDT-SWAP","px":"100.5","sz":"3","side":"sell"}]}""")]
    [InlineData("hyperliquid", """{"channel":"trades","data":[{"coin":"SOL","side":"A","px":"100.5","sz":"3"}]}""")]
    public void ReadsSellerInitiatedTradesFromEveryVenue(string source, string payload)
    {
        Assert.True(ReplayTradeParser.TryRead(new(source, "c", "market", DateTimeOffset.UnixEpoch, 1, payload), "SOL", out var prints));
        Assert.Equal(new ReplayPrint(100.5m, 3, false), Assert.Single(prints!));
        Assert.False(ReplayTradeParser.TryRead(new("hyperliquid", "c", "market", DateTimeOffset.UnixEpoch, 1, """{"channel":"l2Book","data":{}}"""), "SOL", out _));
    }

    [Fact]
    public void MalformedRecognizedDepthInvalidatesRatherThanRefreshingBook()
    {
        Assert.True(ReplayDepthParser.TryRead(new("bybit-public", "c", "market", DateTimeOffset.UnixEpoch, 1,
            """{"topic":"orderbook.50.SOLUSDT","type":"snapshot","data":{"s":"SOLUSDT","u":2,"b":[["99","-2"]],"a":[]}}"""), "SOL", out var book));
        Assert.Null(book);
        Assert.False(ReplayDepthParser.TryRead(new("bybit-public", "c", "market", DateTimeOffset.UnixEpoch, 1,
            """{"topic":"orderbook.1.SOLUSDT"}"""), "SOL", out _));
    }

    [Fact]
    public void DeltaNeedsSnapshotAndDeletesLevelsWithoutReplenishingUnchangedConsumedLiquidity()
    {
        var book = new ReplayBook();
        book.Apply(new(false, 2, [new(99, 3)], [new(101, 3)]), 0, 1); Assert.False(book.Valid);
        book.Apply(new(true, 3, [new(99, 3)], [new(101, 3), new(102, 4)]), 1, 1);
        Assert.Equal(2, book.Fill(true, 2, 101, 0, 1, true).Quantity);
        book.Apply(new(false, 4, [new(99, 5)], []), 2, 1);
        Assert.Equal(1, book.Fill(true, 2, 101, 0, 1, false).Quantity);
        book.Apply(new(false, 5, [], [new(101, 0)]), 3, 1);
        Assert.Equal(102, book.Fill(true, 2, 102, 0, 1, false).Price);
        book.Apply(new(false, 4, [], [new(101, 9)]), 4, 1); Assert.False(book.Valid);
        book.Apply(new(false, 6, [new(99, 2)], [new(101, 2)]), 5, 1); Assert.False(book.Valid);
        book.Apply(new(true, 1, [new(98, 2)], [new(103, 2)]), 6, 1); Assert.True(book.Valid);
    }

    [Fact]
    public void ComputesWeightedPriceAndDistinguishesPriceLimitFromMissingDepth()
    {
        var book = new ReplayBook(); book.Apply(new(true, 1, [new(99, 10)], [new(100, 2), new(101, 3)]), 0, 1);
        var fill = book.Fill(true, 4, 101, 0, 1, false);
        Assert.Equal(4, fill.Quantity); Assert.Equal(100.5m, fill.Price); Assert.False(fill.BeyondVisibleDepth);
        var bounded = book.Fill(true, 4, 100, 0, 1, false);
        Assert.Equal(2, bounded.Quantity); Assert.False(bounded.BeyondVisibleDepth);
        Assert.True(book.Fill(true, 6, 102, 0, 1, false).BeyondVisibleDepth);
        Assert.Equal(0, book.Fill(true, 4, 100, 1, 1, false).Quantity);
    }

    [Fact]
    public void OkxContractsAndLotRulesAreConvertedToCoinsFromRecordedMetadata()
    {
        using var manifest = JsonDocument.Parse("""{"exchangeMetadata":{"data":[{"instId":"SOL-USDT-SWAP","ctType":"linear","ctValCcy":"SOL","settleCcy":"USDT","ctMult":"1","ctVal":"0.1","lotSz":"1","minSz":"2","maxMktSz":"100","tickSz":"0.01"}]}}""");
        var rules = ReplayInstrument.Read(manifest.RootElement, "okx", "SOL", false)!;
        Assert.Equal(.1m, rules.QuantityStep); Assert.Equal(.2m, rules.MinimumQuantity); Assert.Equal(10m, rules.MaximumQuantity);
        Assert.Equal(.2m, rules.RoundQuantity(.29m));
        var book = new ReplayBook(); book.Apply(new(true, 1, [new(99, 100)], [new(101, 100)]), 0, rules.BasePerUnit);
        Assert.Equal(10, book.Fill(true, 11, 101, 0, rules.QuantityStep, false).Quantity);
        Assert.Null(ReplayInstrument.Read(manifest.RootElement, "okx", "ETH", false));
    }

    [Fact]
    public void ReplayUsesVolumeWeightedPricesAndActualRoundTripFees()
    {
        var report = Run(Market()); var scenario = Follow(report); var trade = Assert.Single(scenario.TradeLog);
        Assert.Equal("evaluated", report.Status); Assert.Equal("0.3.2", report.Version);
        Assert.Equal(9, trade.Quantity); Assert.Equal((2 * 120.01m + 7 * 120.03m) / 9, trade.EntryPrice);
        Assert.Equal(120.99m, trade.ExitPrice);
        Assert.Equal(9 * (trade.EntryPrice + trade.ExitPrice) / 10000, trade.FeesQuote);
        Assert.Equal(9 * (trade.ExitPrice - trade.EntryPrice) - trade.FeesQuote, trade.NetQuote);
        Assert.Equal(trade.NetQuote, scenario.ClosedNetQuote);
        Assert.Equal(trade.NetQuote / 1200 * 10000, scenario.SumNetBps);
    }

    // The maker buy is placed at 119.99 for 10 coins (floor(1200 / 119.99)) at 120.26 s with 100 coins visible ahead of it.
    private static readonly ReplaySettings Maker = Settings with { EntryOrder = "maker", MakerFeeBps = 2 };
    private static ReplayMarketData MakerMarket(params (long Ms, decimal Price, decimal Quantity)[] sells) => Market() with
    {
        Trades = [new(0, -3, 1, "t", null), .. sells.Select((x, i) => new ReplayTradeEvent(x.Ms, 300000 + i, 1, "t", [new(x.Price, x.Quantity, false)]))]
    };

    [Fact]
    public void MakerFillsOnlyAfterTradedVolumeClearsTheQueueAheadAndPaysMakerFeeOnEntry()
    {
        var queued = Follow(Run(MakerMarket((120300, 119.99m, 100)), Maker));
        Assert.Empty(queued.TradeLog); Assert.Equal(1, queued.MakerOrders); Assert.Equal(0, queued.MakerFills);

        // 5 coins after the queue at 120.300 (cancel of the rest sent), a sell through our price at 120.305 completes the order.
        var scenario = Follow(Run(MakerMarket((120300, 119.99m, 105), (120305, 119.98m, 10)), Maker));
        var trade = Assert.Single(scenario.TradeLog);
        Assert.Equal(1, scenario.MakerFills); Assert.Equal(0, scenario.PartialEntries);
        Assert.Equal(120.305, trade.EntrySeconds, 6); Assert.Equal(119.99m, trade.EntryPrice); Assert.Equal(10, trade.Quantity);
        Assert.Equal(10 * (119.99m * 2 + trade.ExitPrice * 1) / 10000, trade.FeesQuote);
        Assert.Equal(10 * (trade.ExitPrice - 119.99m) - trade.FeesQuote, trade.NetQuote);
    }

    [Fact]
    public void PartialMakerFillCancelsRestAndCountsFillsUntilTheCancelArrives()
    {
        // 4 coins fill at 120.300; the cancel reaches the exchange at 120.310, so 2 more at 120.305 count and 10 at 120.320 do not.
        var scenario = Follow(Run(MakerMarket((120300, 119.99m, 104), (120305, 119.99m, 2), (120320, 119.99m, 10)), Maker));
        var trade = Assert.Single(scenario.TradeLog);
        Assert.Equal(1, scenario.PartialEntries); Assert.Equal(6, trade.Quantity); Assert.Equal(120.31, trade.EntrySeconds, 6);
        Assert.Contains(scenario.Journal, x => x.Reason == "Заявка maker исполнена частично; остаток отменяется.");
    }

    [Fact]
    public void BuyerInitiatedTradesAndPricesAboveTheBidNeverFillAMakerBuy()
    {
        var market = Market() with
        {
            Trades = [new(0, -3, 1, "t", null), new(120300, 300000, 1, "t", [new(119.99m, 500, true), new(120m, 500, false)])]
        };
        Assert.Empty(Follow(Run(market, Maker)).TradeLog);
    }

    [Fact]
    public void TradeStreamGapOrMissingTradesMakeMakerFillUnknown()
    {
        var market = MakerMarket((1000, 120m, 1)); market.Trades!.Add(new(120300, 300001, 1, "t", null));
        var scenario = Follow(Run(market, Maker));
        Assert.Equal(1, scenario.UnresolvedPositions); Assert.Contains(scenario.Journal, x => x.Reason.Contains("Разрыв потока сделок"));
        Assert.Equal("no-execution-data", Run(Market(), Maker).Status);
    }

    [Fact]
    public void ExitThatNeverFillsAfterHoldLimitDoesNotLoopAndEndsUnresolved()
    {
        // Every exit from 120.5 s misses its limit; the hold limit (122.26 s) must not be re-armed with a past timestamp.
        var market = Market();
        for (var i = 0; i < market.Books.Count; i++)
        {
            var item = market.Books[i];
            if (item.Ticks >= 120500) market.Books[i] = item with { Update = item.Update! with { Bids = [new(120.5m, 100)] } };
        }
        var scenario = Follow(Run(market));
        Assert.Empty(scenario.TradeLog); Assert.Equal(1, scenario.UnresolvedPositions);
        Assert.Contains(scenario.Journal, x => x.Reason == "Запись закончилась с открытой позицией.");
    }

    [Fact]
    public void QuantityBelowExchangeMinimumIsSkippedAndReported()
    {
        var result = Follow(Run(Market(), Settings with { OrderNotional = 10 }));
        Assert.Empty(result.TradeLog); Assert.Equal(0, result.UnresolvedPositions);
        Assert.Contains(result.Journal, x => x.Reason.Contains("Размер заявки после округления"));
    }

    [Fact]
    public void PartialEntryCancelsRemainderAndClosesOnlyFilledQuantity()
    {
        var market = Market();
        for (var i = 0; i < market.Books.Count; i++)
        {
            var item = market.Books[i];
            if (item.Ticks >= 120000 && item.Ticks < 120500) market.Books[i] = item with { Update = item.Update! with { Asks = [new(120.01m, 2), new(121, 100)] } };
        }
        var scenario = Follow(Run(market)); var trade = Assert.Single(scenario.TradeLog);
        Assert.Equal(1, scenario.PartialEntries); Assert.Equal(2, trade.Quantity);
        Assert.Equal(120.01m, trade.EntryPrice); Assert.Equal(0, scenario.UnresolvedPositions);
    }

    [Fact]
    public void ExitBlockedByPriceLimitIsRetriedInsteadOfDroppingTheTradeFromStatistics()
    {
        // A short dip in the book makes the first IOC exit miss its limit. Excluding such trades would hide the worst exits.
        var market = Market();
        for (var i = 0; i < market.Books.Count; i++)
        {
            var item = market.Books[i];
            if (item.Ticks is >= 120500 and < 120600) market.Books[i] = item with { Update = item.Update! with { Bids = [new(120.8m, 100)] } };
        }
        var scenario = Follow(Run(market)); var trade = Assert.Single(scenario.TradeLog);
        Assert.Equal(0, scenario.UnresolvedPositions); Assert.Equal(9, trade.Quantity);
        Assert.Equal(120.99m, trade.ExitPrice); Assert.True(trade.ExitSeconds > 120.51);
        Assert.Contains(scenario.Journal, x => x.Action == "выход не исполнен");
    }

    [Fact]
    public void PartialExitsAccumulateIntoOneClosedTradeAtWeightedPrice()
    {
        var market = Market();
        for (var i = 0; i < market.Books.Count; i++)
        {
            var item = market.Books[i];
            if (item.Ticks is >= 120500 and < 120550) market.Books[i] = item with { Update = item.Update! with { Bids = [new(121m, 4), new(120.5m, 100)] } };
        }
        var scenario = Follow(Run(market)); var trade = Assert.Single(scenario.TradeLog);
        Assert.Equal(1, scenario.PartialExits); Assert.Equal(9, trade.Quantity);
        Assert.Equal((4 * 121m + 5 * 120.99m) / 9, trade.ExitPrice);
        Assert.Equal(9 * (trade.EntryPrice + trade.ExitPrice) / 10000, trade.FeesQuote);
        Assert.Equal(trade.NetQuote, scenario.ClosedNetQuote);
    }

    [Fact]
    public void PartialExitLeavesExplicitRemainderAndDoesNotFabricateClosedProfit()
    {
        var market = Market();
        for (var i = 0; i < market.Books.Count; i++)
        {
            var item = market.Books[i];
            if (item.Ticks >= 120500) market.Books[i] = item with { Update = item.Update! with { Bids = [new(120.99m, 1)] } };
        }
        var scenario = Follow(Run(market));
        Assert.Empty(scenario.TradeLog); Assert.Equal(1, scenario.PartialExits); Assert.Equal(1, scenario.UnresolvedPositions);
        Assert.Equal(8, scenario.UnresolvedQuantity); Assert.Equal(0, scenario.ClosedNetQuote);
    }

    [Fact]
    public void FutureDepthAtExactExecutionTimeCannotImproveTheFill()
    {
        var market = Market(); var original = Follow(Run(market));
        market.Books.Add(new(120260, 999999, 1, "c", new(true, 120260, [new(119, 100)], [new(120, 100)])));
        var changed = Follow(Run(market));
        Assert.Equal(original.TradeLog[0].EntryPrice, changed.TradeLog[0].EntryPrice);
    }

    [Fact]
    public void MissingOrStaleDepthNeverFallsBackToUnlimitedBboLiquidity()
    {
        Assert.Equal("no-execution-data", Run(null).Status);
        var market = Market(); market.Books.RemoveAll(x => x.Ticks >= 119000);
        var scenario = Follow(Run(market));
        Assert.Empty(scenario.TradeLog); Assert.Contains(scenario.Journal, x => x.Reason.Contains("Нет свежего восстановленного стакана"));
    }
}
