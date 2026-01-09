
using LyWaf.Services;
using LyWaf.Services.Statistic;
using LyWaf.Shared;

namespace LyWaf.Utils;

public class WafUtil
{
    public const string FB_OUTPUT_HTML = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta http-equiv="Content-Type" content="text/html; charset=utf-8" />
        <meta http-equiv="Content-Language" content="zh-cn" />
        <title>LyWaf-Web应用防火墙</title>
        </head>
        <body>
        <h2 align="center"> 您的IP为:{local_client_ip} </h2>
        <h3 align="center"> 您的IP存在异常访问的情况, 若误封, 请联系管理员 </h3>
        {show_reason_info}
        <h4 align="center"> LyWaf为您的服务提供保驾护航 </h4>
        </body>
        </html>
    """;
    public static void DoFbIp(string ip, string reason, TimeSpan? timeout = null)
    {
        if (timeout == null)
        {
            var statisticService = ServiceLocator.GetRequiredService<IStatisticService>();
            if (statisticService == null)
            {
                timeout = TimeSpan.FromSeconds(600);
            }
            else
            {
                timeout = statisticService.GetOption().GetDefaultFbTime();
            }
        }
        SharedData.ClientFb.AddOrUpdate(ip, reason, timeout);
    }

    public static string? GetFbReason(string ip)
    {
        SharedData.ClientFb.TryGetValue(ip, out var reason);
        return reason;
    }

    public static async Task WriteFbOutput(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.ContentType = "text/html; charset=utf-8";
        var ip = RequestUtil.GetClientIp(context.Request);
        // var val = string.Format(FB_OUTPUT_HTML, new Dictionary<string, string> { { "local_client_ip", ip } });
        var val = FB_OUTPUT_HTML.Replace("{local_client_ip}", ip);
#if DEBUG
        val = val.Replace("{show_reason_info}", $"<h3 align=\"center\"> 禁用原因: {reason}  </h3>");
#else
        val = val.Replace("{show_reason_info}", "");
#endif
        await context.Response.WriteAsync(val);
    }
}
