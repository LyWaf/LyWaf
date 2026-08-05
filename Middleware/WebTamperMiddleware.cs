using LyWaf.Services.WebTamper;
using LyWaf.Plugins.Core;

namespace LyWaf.Middleware;

public sealed class WebTamperMiddleware(RequestDelegate next, IWebTamperService service, IPluginManager pluginManager)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (service.IsInternalCheck(context.Request))
        {
            context.Request.Headers.Remove("X-LyWaf-Tamper-Check");
            await next(context);
            return;
        }

        if (pluginManager.GetPluginState("web-tamper") is not (PluginState.Running or PluginState.Initialized))
        {
            await next(context);
            return;
        }

        if (!service.TryGetProtectedContent(context.Request, out var resource, out var baseline)
            || resource == null)
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        var acceptEncoding = context.Request.Headers.AcceptEncoding.ToString();
        context.Request.Headers.Remove("Accept-Encoding");
        await using var captured = new MemoryStream();
        context.Response.Body = captured;
        try
        {
            await next(context);
            if (context.Response.StatusCode is < 200 or >= 300 || captured.Length > WebTamperService.MaxContentBytes)
            {
                captured.Position = 0;
                await captured.CopyToAsync(originalBody, context.RequestAborted);
                return;
            }

            var observed = captured.ToArray();
            var requestPath = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            service.RecordObservedContent(resource, requestPath, context.Response.ContentType ?? "application/octet-stream", observed);
            var changed = baseline != null
                && !string.Equals(resource.BaselineHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(observed)), StringComparison.Ordinal);
            var output = changed ? baseline!.Bytes : observed;
            if (changed)
            {
                context.Response.ContentType = baseline!.ContentType;
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers["X-LyWaf-Tamper-Protected"] = "baseline";
            }
            context.Response.ContentLength = output.Length;
            await originalBody.WriteAsync(output, context.RequestAborted);
        }
        finally
        {
            if (acceptEncoding.Length > 0) context.Request.Headers.AcceptEncoding = acceptEncoding;
            context.Response.Body = originalBody;
        }
    }
}
