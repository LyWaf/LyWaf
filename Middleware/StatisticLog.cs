
using LyWaf.Shared;
using LyWaf.Utils;
using NLog;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Middleware;
public class StatisticLogMiddleware(RequestDelegate next)
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        long timestamp_start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        context.Request.EnableBuffering();
        var end = context.GetEndpoint()!;

        await _next(context);
        context.Items.TryGetValue("ProxyDestUrl", out var destUrl);
        _logger.Info("ProxyDestUrl === {} remote ip == {}", destUrl, RequestUtil.GetClientIp(context.Request));
        var new_value = string.Format("{0} {1}", destUrl, timestamp_start);
        SharedData.IpDict.AddOrUpdate("value", new_value);
        SharedData.IpDict.AddOrUpdate("old", new_value);
        SharedData.IpDict.AddOrUpdate("value1", "ke6");
        long timestamp_end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        await StatisticUtil.DoStatisticRequest(context, (string?)destUrl ?? "", timestamp_end - timestamp_start);
        context.Request.Body.Position = 0;
        Stream rawbody = new MemoryStream();
        var request = context.Request;
        await request.Body.CopyToAsync(rawbody, 200);
        var body = StreamUtil.ConvertToString(rawbody);
        var url = RequestUtil.GetRequestUrl(request);
        var response = context.Response;
        var reqRaw = RequestUtil.RecordRuquest(request, body);
        _logger.Info("以下是curl指令:\n{}\n返回{},长度{}\n当前请求耗时: {} ms\n",
            reqRaw, response.StatusCode,
            response.ContentLength != null ? response.ContentLength : response.Headers.TransferEncoding,
            timestamp_end - timestamp_start);

    }
}