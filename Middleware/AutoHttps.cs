using LyWaf.Services.WafInfo;

namespace LyWaf.Middleware;

public class AutoHttpsMiddleware(RequestDelegate next, IWafInfoService wafInfoService)
{
    private readonly RequestDelegate _next = next;
    private readonly Dictionary<int, int> _autoHttpsRedirects = wafInfoService.GetOptions().Listens
            .Where(l => !l.IsHttps && l.AutoHttpsPort.HasValue && l.AutoHttpsPort > 0 && l.AutoHttpsPort <= 65535)
            .ToDictionary(l => l.Port, l => l.AutoHttpsPort!.Value);

    public async Task InvokeAsync(HttpContext context)
    {
        var localPort = context.Connection.LocalPort;
        if (_autoHttpsRedirects.TryGetValue(localPort, out var httpsPort))
        {
            var host = context.Request.Host.Host;
            var path = context.Request.Path + context.Request.QueryString;
            var httpsUrl = httpsPort == 443
                ? $"https://{host}{path}"
                : $"https://{host}:{httpsPort}{path}";

            context.Response.Redirect(httpsUrl, permanent: true);
            return;
        }
        await _next(context);
    }
}
