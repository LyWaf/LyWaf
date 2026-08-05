using LyWaf.Services.WebTamper;
using LyWaf.Plugins.Core;

namespace LyWaf.Plugins.Sys.WebTamper;

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
        var capturedPath = Path.Combine(Path.GetTempPath(), $"lywaf-tamper-{Guid.NewGuid():N}.tmp");
        var captured = new FileStream(capturedPath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        context.Response.Body = captured;
        try
        {
            await next(context);
            await captured.FlushAsync(context.RequestAborted);
            if (context.Response.StatusCode is < 200 or >= 300 || captured.Length > WebTamperService.MaxContentBytes)
            {
                captured.Position = 0;
                await captured.CopyToAsync(originalBody, context.RequestAborted);
                return;
            }

            var currentHash = await WebTamperService.HashFileAsync(capturedPath, context.RequestAborted);
            var requestPath = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            service.RecordObservedContent(resource, requestPath, context.Response.ContentType ?? "application/octet-stream", capturedPath, currentHash);
            service.TryQueueBackgroundCheck(resource.Website, requestPath);
            var changed = baseline != null
                && !string.Equals(resource.BaselineHash, currentHash, StringComparison.Ordinal);
            if (changed)
            {
                context.Response.ContentType = baseline!.ContentType;
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers["X-LyWaf-Tamper-Protected"] = "baseline";
            }
            var outputPath = changed ? baseline!.FilePath : capturedPath;
            context.Response.ContentLength = changed ? baseline!.Length : captured.Length;
            await using var output = new FileStream(outputPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await output.CopyToAsync(originalBody, context.RequestAborted);
        }
        finally
        {
            if (acceptEncoding.Length > 0) context.Request.Headers.AcceptEncoding = acceptEncoding;
            context.Response.Body = originalBody;
            await captured.DisposeAsync();
            try { File.Delete(capturedPath); } catch { }
        }
    }
}
