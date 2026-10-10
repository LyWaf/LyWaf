using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace LyWaf.Plugins.Sys.HttpUpload;

/// <summary>Applies the upload body budget before WAF/logging can start reading the stream.</summary>
public sealed class UploadRequestLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IPluginManager manager, IOptionsMonitor<HttpUploadOptions> options)
    {
        var settings = options.CurrentValue;
        if (manager.GetPluginState("http-upload") is PluginState.Running or PluginState.Initialized
            && settings.Enabled && settings.Key.Length >= 32
            && manager.IsPermissionGranted("http-upload", PluginPermission.DataDirectory | PluginPermission.SensitiveConfiguration)
            && HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.Equals(settings.Path, StringComparison.Ordinal)
            && HttpUploadMiddleware.IsHostAllowed(context.Request.Host.Host, settings.Hosts)
            && (!settings.RequireHttps || context.Request.IsHttps)
            && settings.MaxUploadBytes is > 0 and <= 1099511627776)
        {
            // Budget includes multipart headers/fields; the file itself has a separate exact byte check.
            var limit = settings.MaxUploadBytes + 65536;
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
            if (context.Request.ContentLength > limit)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
        }
        await next(context);
    }
}
