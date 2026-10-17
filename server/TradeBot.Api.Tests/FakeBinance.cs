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
    public bool RefuseStop, LoseNextMarketAnswer;
    public readonly Dictionary<string, string> Orders = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var q = query.AllKeys.ToDictionary(k => k!, k => query[k]!);
        var path = request.RequestUri.AbsolutePath;
        Requests.Add((request.Method.Method, path, q));
        if (path != "/fapi/v1/time" && path != "/fapi/v1/exchangeInfo")
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
            ("GET", "/fapi/v2/balance") => """[{"asset":"USDT","availableBalance":"5000.5"}]""",
            ("POST", "/fapi/v1/marginType") or ("POST", "/fapi/v1/leverage") => "{}",
            ("POST", "/fapi/v1/order") when q["type"] == "STOP_MARKET" => RefuseStop ? null : Orders[q["newClientOrderId"]] = Order(q["newClientOrderId"], "NEW", 0, 0, q["side"], "STOP_MARKET"),
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
            var code = (request.Method.Method, path) == ("GET", "/fapi/v1/order") ? -2013 : -2021;
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
