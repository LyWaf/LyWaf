using System.Text.Json;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Middleware;

public sealed class CorsMiddleware(RequestDelegate next)
{
    private const string MetadataKey = "_Cors";

    public async Task InvokeAsync(HttpContext context)
    {
        var route = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>();
        if (route?.Config.Metadata?.TryGetValue(MetadataKey, out var json) != true || string.IsNullOrWhiteSpace(json))
        {
            await next(context);
            return;
        }

        var options = JsonSerializer.Deserialize<CorsRouteOptions>(json, JsonOptions);
        var origin = context.Request.Headers.Origin.ToString();
        if (options is null || string.IsNullOrWhiteSpace(origin) || !IsOriginAllowed(origin, options.AllowedOrigins))
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsOptions(context.Request.Method)
            && context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            ApplyResponseHeaders(context.Response.Headers, origin, options);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        context.Response.OnStarting(() =>
        {
            ApplyResponseHeaders(context.Response.Headers, origin, options);
            return Task.CompletedTask;
        });
        await next(context);
    }

    private static void ApplyResponseHeaders(IHeaderDictionary headers, string origin, CorsRouteOptions options)
    {
        var allowAnyOrigin = options.AllowedOrigins.Contains("*");
        headers.AccessControlAllowOrigin = allowAnyOrigin && !options.AllowCredentials ? "*" : origin;
        headers.Append("Vary", "Origin");
        headers.AccessControlAllowMethods = string.Join(", ", options.AllowedMethods);
        headers.AccessControlAllowHeaders = string.Join(", ", options.AllowedHeaders);
        if (options.ExposedHeaders.Count > 0)
            headers.AccessControlExposeHeaders = string.Join(", ", options.ExposedHeaders);
        if (options.AllowCredentials)
            headers.AccessControlAllowCredentials = "true";
        if (options.MaxAge > 0)
            headers.AccessControlMaxAge = options.MaxAge.ToString();
    }

    private static bool IsOriginAllowed(string origin, List<string> allowedOrigins)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)) return false;
        foreach (var allowed in allowedOrigins)
        {
            if (allowed == "*" || string.Equals(allowed.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return true;

            if (!allowed.Contains("*.", StringComparison.Ordinal)) continue;
            if (!Uri.TryCreate(allowed.Replace("*.", "wildcard."), UriKind.Absolute, out var patternUri)) continue;
            var suffix = patternUri.Host["wildcard".Length..];
            if (originUri.Scheme.Equals(patternUri.Scheme, StringComparison.OrdinalIgnoreCase)
                && originUri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && originUri.Host.Length > suffix.Length
                && originUri.Port == patternUri.Port)
                return true;
        }
        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class CorsRouteOptions
    {
        public List<string> AllowedOrigins { get; init; } = ["*"];
        public List<string> AllowedMethods { get; init; } = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];
        public List<string> AllowedHeaders { get; init; } = ["*"];
        public List<string> ExposedHeaders { get; init; } = [];
        public bool AllowCredentials { get; init; }
        public int MaxAge { get; init; } = 600;
    }
}
