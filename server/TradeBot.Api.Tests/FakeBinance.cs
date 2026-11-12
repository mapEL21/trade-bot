using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using Xunit;

namespace TradeBot.Api.Tests;

// Answers like Binance USDT-M futures: XUSDT with a 0.001 quantity step, 0.05 price step and a 5 USDT minimum order.
internal sealed class FakeBinance : HttpMessageHandler
{
    public readonly List<(string Method, string Path, Dictionary<string, string> Query)> Requests = [];
    public decimal Price = 100;
    public decimal Position;
    public bool RefuseStop, LoseNextMarketAnswer, Offline, HedgeMode;
    public readonly Dictionary<string, string> Orders = [];
    public readonly Dictionary<string, string> Stops = [];
    private int refusal;
    private string? Refuse(int code) { refusal = code; return null; }
    private static string Stop(Dictionary<string, string> q) =>
        $$"""{"algoId":1,"clientAlgoId":"{{q["clientAlgoId"]}}","orderType":"{{q["type"]}}","symbol":"{{q["symbol"]}}","side":"{{q["side"]}}","algoStatus":"NEW","triggerPrice":"{{q["triggerPrice"]}}","actualPrice":"0","closePosition":true}""";
    // The stop fired on the exchange: the position is gone and the stop reports its fill price.
    public void TriggerStop(decimal price)
    {
        foreach (var (id, json) in Stops.ToList())
            Stops[id] = json.Replace("\"algoStatus\":\"NEW\"", "\"algoStatus\":\"FINISHED\"")
                .Replace("\"actualPrice\":\"0\"", $"\"actualPrice\":\"{price.ToString(CultureInfo.InvariantCulture)}\"");
        Position = 0;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var q = query.AllKeys.ToDictionary(k => k!, k => query[k]!);
        var path = request.RequestUri.AbsolutePath;
        Requests.Add((request.Method.Method, path, q));
        if (Offline) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.");
        if (path != "/fapi/v1/time" && path != "/fapi/v1/exchangeInfo" && path != "/fapi/v1/ticker/price")
        {
            Assert.True(request.Headers.Contains("X-MBX-APIKEY"));
            Assert.True(q.ContainsKey("signature") && q.ContainsKey("timestamp"));
        }
        string S(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        string Order(string id, string status, decimal qty, decimal price, string side, string type) =>
            $"{{\"clientOrderId\":\"{id}\",\"status\":\"{status}\",\"executedQty\":\"{S(qty)}\",\"avgPrice\":\"{S(price)}\",\"side\":\"{side}\",\"type\":\"{type}\"}}";
        var body = (request.Method.Method, path) switch
        {
            ("GET", "/fapi/v1/time") => $"{{\"serverTime\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}",
            ("GET", "/fapi/v1/exchangeInfo") => """{"symbols":[{"symbol":"XUSDT","filters":[{"filterType":"PRICE_FILTER","tickSize":"0.05"},{"filterType":"LOT_SIZE","stepSize":"0.001","minQty":"0.001"},{"filterType":"MARKET_LOT_SIZE","stepSize":"0.001","minQty":"0.001"},{"filterType":"MIN_NOTIONAL","notional":"5"}]}]}""",
            ("GET", "/fapi/v2/balance") => """[{"asset":"USDT","balance":"5010.5","availableBalance":"5000.5","crossUnPnl":"1.25"}]""",
            ("POST", "/fapi/v1/marginType") or ("POST", "/fapi/v1/leverage") => "{}",
            ("GET", "/fapi/v1/commissionRate") => $"{{\"symbol\":\"{q["symbol"]}\",\"makerCommissionRate\":\"0.0002\",\"takerCommissionRate\":\"0.0005\"}}",
            // Conditional orders go through the Algo Order API only; the old endpoint refuses them.
            ("POST", "/fapi/v1/order") when q["type"] == "STOP_MARKET" => Refuse(-4120),
            ("POST", "/fapi/v1/algoOrder") => RefuseStop ? null : Stops[q["clientAlgoId"]] = Stop(q),
            ("GET", "/fapi/v1/algoOrder") => Stops.GetValueOrDefault(q["clientAlgoId"]),
            // A stop that already triggered cannot be cancelled, but it can still be asked about.
            ("DELETE", "/fapi/v1/algoOrder") => Stops.GetValueOrDefault(q["clientAlgoId"])?.Contains("\"NEW\"") == true && Stops.Remove(q["clientAlgoId"]) ? "{}" : null,
            ("DELETE", "/fapi/v1/algoOpenOrders") => "{}",
            ("GET", "/fapi/v1/positionSide/dual") => $$"""{"dualSidePosition":{{(HedgeMode ? "true" : "false")}}}""",
            ("GET", "/fapi/v1/ticker/price") => $$"""{"symbol":"XUSDT","price":"{{S(Price)}}"}""",
            ("POST", "/fapi/v1/order") when q.GetValueOrDefault("reduceOnly") == "true" && Position == 0 => Refuse(-2022),
            ("POST", "/fapi/v1/order") => Market(q, Order),
            ("GET", "/fapi/v1/order") => Orders.GetValueOrDefault(q["origClientOrderId"]),
            ("DELETE", "/fapi/v1/order") => "{}",
            ("DELETE", "/fapi/v1/allOpenOrders") => """{"code":200,"msg":"The operation of cancel all open order is done."}""",
            ("GET", "/fapi/v2/positionRisk") => Position == 0 ? "[]" : $"[{{\"symbol\":\"XUSDT\",\"positionAmt\":\"{S(Position)}\",\"entryPrice\":\"100\",\"unRealizedProfit\":\"0\"}}]",
            _ => throw new InvalidOperationException($"unexpected {request.Method} {path}"),
        };
        if (body == "lost") throw new HttpRequestException("connection reset");
        if (body is null)
        {
            var code = refusal != 0 ? refusal : (request.Method.Method, path) == ("GET", "/fapi/v1/order") ? -2013 : -2021;
            refusal = 0;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent($"{{\"code\":{code},\"msg\":\"refused\"}}", Encoding.UTF8) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
    }

    private string Market(Dictionary<string, string> q, Func<string, string, decimal, decimal, string, string, string> order)
    {
        var qty = decimal.Parse(q["quantity"], CultureInfo.InvariantCulture);
        Position += q["side"] == "BUY" ? qty : -qty;
        var answer = Orders[q["newClientOrderId"]] = order(q["newClientOrderId"], "FILLED", qty, Price, q["side"], "MARKET");
        if (LoseNextMarketAnswer) { LoseNextMarketAnswer = false; return "lost"; }
        return answer;
    }
}
