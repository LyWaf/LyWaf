
namespace LyWaf.Plugins.Sys.HttpSettings;

public sealed class HttpSettingsMiddleware(RequestDelegate next, Func<HttpContext, HttpSettingsOptions> resolve)
{
    private static readonly Dictionary<string, string> Empty = [];
    private static readonly Dictionary<string, string> Security = new()
    {
        ["X-Content-Type-Options"] = "nosniff", ["X-Frame-Options"] = "SAMEORIGIN",
        ["Referrer-Policy"] = "strict-origin-when-cross-origin"
    };

    public Task InvokeAsync(HttpContext context)
    {
        var options = resolve(context);
        if (!options.Enabled) return next(context);
        HttpHeaderPolicy.Apply(context.Request.Headers, options.RequestHeadersIfMissing, options.RequestHeaders,
            options.AppendRequestHeaders, options.RemoveRequestHeaders);
        string? requestId = null;
        if (!string.IsNullOrEmpty(options.RequestIdHeader) && HttpHeaderPolicy.IsRequestIdName(options.RequestIdHeader))
        {
            requestId = context.TraceIdentifier;
            if (requestId.Length > 128 || !HttpHeaderPolicy.IsValidValue(requestId)) requestId = Guid.NewGuid().ToString("N");
            context.Request.Headers[options.RequestIdHeader] = requestId;
        }
        // Registered at First: executes after inner OnStarting callbacks, including cache hits and blocks.
        context.Response.OnStarting(() =>
        {
            if (options.StatusCodes.Count > 0 && !options.StatusCodes.Any(p => HttpHeaderPolicy.MatchesStatus(p, context.Response.StatusCode)))
                return Task.CompletedTask;
            if (options.SecurityHeaders) HttpHeaderPolicy.Apply(context.Response.Headers, Security, Empty, Empty, []);
            if (options.HstsMaxAgeSeconds > 0 && context.Request.IsHttps)
                context.Response.Headers.TryAdd("Strict-Transport-Security", "max-age=" + options.HstsMaxAgeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            HttpHeaderPolicy.Apply(context.Response.Headers, options.Headers, options.ResponseHeaders,
                options.AppendResponseHeaders, options.RemoveResponseHeaders);
            if (requestId is not null && !options.RemoveResponseHeaders.Contains(options.RequestIdHeader, StringComparer.OrdinalIgnoreCase))
                context.Response.Headers[options.RequestIdHeader] = requestId;
            return Task.CompletedTask;
        });
        return next(context);
    }
}
