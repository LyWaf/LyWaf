using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;
using LyWaf.Services.Metrics;

namespace LyWaf.Services.ResponseCaching;

/// <summary>
/// 缓存 YARP 最终安全响应。它位于压缩内层、防篡改外层，因此磁盘保存未压缩且已经过防篡改替换的内容。
/// </summary>
public sealed class ResponseCacheMiddleware(
    RequestDelegate next,
    ResponseCacheStore store,
    ResponseCacheRefreshService refreshService,
    IOptionsMonitor<ResponseCacheOptions> options,
    PrometheusMetrics? metrics = null)
{
    private static readonly HashSet<string> ExcludedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Content-Length", "Age", "X-LyWaf-Cache"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var policy = ResolvePolicy(context);
        if (!CanUseRequest(context.Request, policy))
        {
            metrics?.RecordResponseCache("bypass");
            await next(context);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var forceRefresh = context.Request.Headers.ContainsKey("X-LyWaf-Cache-Refresh") ||
            context.Request.Headers.CacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase);
        store.TryFind(context.Request, out var cached);
        if (cached is null && HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }
        if (!forceRefresh && cached is not null && now <= cached.FreshUntil)
        {
            await ServeAsync(context, cached, "HIT");
            metrics?.RecordResponseCache("hit");
            return;
        }
        if (!forceRefresh && cached is not null && now <= cached.StaleWhileRevalidateUntil)
        {
            QueueRefresh(context.Request, cached);
            await ServeAsync(context, cached, "STALE");
            metrics?.RecordResponseCache("stale");
            return;
        }

        var baseKey = ResponseCacheStore.BaseKey(context.Request);
        using (await store.AcquireLockAsync(baseKey, context.RequestAborted))
        {
            if (!forceRefresh && store.TryFind(context.Request, out var lockedHit) && lockedHit is not null)
            {
                if (DateTimeOffset.UtcNow <= lockedHit.FreshUntil)
                {
                    await ServeAsync(context, lockedHit, "HIT");
                    metrics?.RecordResponseCache("hit");
                    return;
                }
                cached = lockedHit;
            }
            await FetchAndStoreAsync(context, policy, cached);
        }
    }

    private async Task FetchAndStoreAsync(HttpContext context, ResponseCachePolicy policy, ResponseCacheEntry? stale)
    {
        var originalBody = context.Response.Body;
        var temporaryPath = store.CreateTemporaryPath();
        await using var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var capture = new BoundedCaptureStream(file, originalBody, policy.MaxBodyBytes);
        var oldIfNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
        var oldIfModifiedSince = context.Request.Headers.IfModifiedSince.ToString();
        if (stale?.ETag is { Length: > 0 }) context.Request.Headers.IfNoneMatch = stale.ETag;
        if (stale?.LastModified is not null) context.Request.Headers.IfModifiedSince = stale.LastModified.Value.ToString("R");
        context.Request.Headers.Remove("X-LyWaf-Cache-Refresh");
        context.Response.Body = capture;
        Exception? failure = null;
        try { await next(context); }
        catch (Exception ex) { failure = ex; }
        finally
        {
            context.Response.Body = originalBody;
            RestoreHeader(context.Request.Headers, "If-None-Match", oldIfNoneMatch);
            RestoreHeader(context.Request.Headers, "If-Modified-Since", oldIfModifiedSince);
        }
        try { await file.FlushAsync(context.RequestAborted); }
        finally { await file.DisposeAsync(); }

        if (failure is not null)
        {
            ResponseCacheStore.DeleteFile(temporaryPath);
            if (!capture.Overflowed && stale is not null && DateTimeOffset.UtcNow <= stale.StaleIfErrorUntil)
            {
                await ServeAsync(context, stale, "STALE-ERROR");
                metrics?.RecordResponseCache("stale_error");
                return;
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw new InvalidOperationException("异常重抛后不应继续执行");
        }

        if (!capture.Overflowed && stale is not null && context.Response.StatusCode == StatusCodes.Status304NotModified)
        {
            ResponseCacheStore.DeleteFile(temporaryPath);
            var ttl = ResolveTtl(context.Response, policy.DefaultTtl);
            store.Refresh(stale, ttl, policy.StaleWhileRevalidate, policy.StaleIfError);
            await ServeAsync(context, stale, "REVALIDATED");
            metrics?.RecordResponseCache("revalidated");
            return;
        }

        if (!capture.Overflowed && stale is not null && context.Response.StatusCode >= 500 &&
            DateTimeOffset.UtcNow <= stale.StaleIfErrorUntil)
        {
            ResponseCacheStore.DeleteFile(temporaryPath);
            await ServeAsync(context, stale, "STALE-ERROR");
            metrics?.RecordResponseCache("stale_error");
            return;
        }

        if (capture.Overflowed)
        {
            ResponseCacheStore.DeleteFile(temporaryPath);
            metrics?.RecordResponseCache("bypass");
            return;
        }
        if (!CanStoreResponse(context.Response, policy, out var varyHeaders))
        {
            await using var uncachedBody = OpenBody(temporaryPath);
            await uncachedBody.CopyToAsync(originalBody, context.RequestAborted);
            ResponseCacheStore.DeleteFile(temporaryPath);
            metrics?.RecordResponseCache("bypass");
            return;
        }

        var ttlValue = ResolveTtl(context.Response, policy.DefaultTtl);
        var headers = context.Response.Headers.Where(x => !ExcludedResponseHeaders.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value.Select(value => value ?? string.Empty).ToArray(), StringComparer.OrdinalIgnoreCase);
        var etag = context.Response.Headers.ETag.ToString();
        var lastModified = TryDate(context.Response.Headers.LastModified.ToString());
        var entry = store.Commit(temporaryPath, ResponseCacheStore.BaseKey(context.Request), context.Request,
            context.Response.StatusCode, headers, varyHeaders, ttlValue, policy.StaleWhileRevalidate,
            policy.StaleIfError, string.IsNullOrEmpty(etag) ? null : etag, lastModified);
        if (entry is null)
        {
            await using var uncachedBody = OpenBody(temporaryPath);
            await uncachedBody.CopyToAsync(originalBody, context.RequestAborted);
            ResponseCacheStore.DeleteFile(temporaryPath);
            metrics?.RecordResponseCache("bypass");
            return;
        }
        context.Response.Headers["X-LyWaf-Cache"] = "MISS";
        metrics?.RecordResponseCache("miss");
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await using var body = OpenBody(entry.BodyPath);
            await body.CopyToAsync(originalBody, context.RequestAborted);
        }
    }

    private ResponseCachePolicy ResolvePolicy(HttpContext context)
    {
        var current = options.CurrentValue;
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.Metadata;
        var enabled = ReadBool(metadata, "ResponseCache.Enabled") ?? current.Enabled;
        var ttl = ReadInt(metadata, "ResponseCache.TtlSeconds") ?? current.DefaultTtlSeconds;
        return new ResponseCachePolicy(enabled, Math.Clamp(current.MaxBodyBytes, 1, 1024L * 1024 * 1024),
            TimeSpan.FromSeconds(Math.Clamp(ttl, 1, 86400 * 365)),
            TimeSpan.FromSeconds(Math.Clamp(current.StaleWhileRevalidateSeconds, 0, 86400)),
            TimeSpan.FromSeconds(Math.Clamp(current.StaleIfErrorSeconds, 0, 86400 * 7)),
            current.Methods.ToHashSet(StringComparer.OrdinalIgnoreCase), current.StatusCodes.ToHashSet(),
            current.CacheAuthenticated);
    }

    private static bool CanUseRequest(HttpRequest request, ResponseCachePolicy policy) => policy.Enabled &&
        policy.Methods.Contains(request.Method) && !request.Headers.ContainsKey("Range") &&
        !request.Headers.CacheControl.ToString().Contains("no-store", StringComparison.OrdinalIgnoreCase) &&
        (policy.CacheAuthenticated || !request.Headers.ContainsKey("Authorization"));

    private static bool CanStoreResponse(HttpResponse response, ResponseCachePolicy policy, out IReadOnlyList<string> varyHeaders)
    {
        varyHeaders = response.Headers.Vary.SelectMany(x => (x ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var cacheControl = response.Headers.CacheControl.ToString();
        return policy.StatusCodes.Contains(response.StatusCode) && !response.Headers.ContainsKey("Set-Cookie") &&
            !varyHeaders.Contains("*", StringComparer.Ordinal) &&
            !cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase) &&
            !cacheControl.Contains("private", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan ResolveTtl(HttpResponse response, TimeSpan fallback)
    {
        var cacheControl = response.Headers.CacheControl.ToString();
        if (cacheControl.Split(',').Any(x => x.Trim().Equals("no-cache", StringComparison.OrdinalIgnoreCase)))
            return TimeSpan.Zero;
        foreach (var name in new[] { "s-maxage", "max-age" })
        {
            var part = cacheControl.Split(',').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
            if (part is not null && int.TryParse(part[(part.IndexOf('=') + 1)..].Trim('"'), out var seconds))
                return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }
        var expires = TryDate(response.Headers.Expires.ToString());
        return expires is null ? fallback : TimeSpan.FromSeconds(Math.Max(0, (expires.Value - DateTimeOffset.UtcNow).TotalSeconds));
    }

    private async Task ServeAsync(HttpContext context, ResponseCacheEntry entry, string outcome)
    {
        if (IsNotModified(context.Request, entry))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.Headers.Clear();
            CopyHeader(entry, context.Response, "Cache-Control");
            CopyHeader(entry, context.Response, "Expires");
            CopyHeader(entry, context.Response, "Vary");
            if (entry.ETag is not null) context.Response.Headers.ETag = entry.ETag;
            if (entry.LastModified is not null) context.Response.Headers.LastModified = entry.LastModified.Value.ToString("R");
            context.Response.Headers["X-LyWaf-Cache"] = outcome;
            return;
        }
        context.Response.Headers.Clear();
        foreach (var pair in entry.Headers) context.Response.Headers[pair.Key] = pair.Value;
        context.Response.StatusCode = entry.StatusCode;
        context.Response.ContentLength = entry.Length;
        context.Response.Headers.Age = Math.Max(0, (long)(DateTimeOffset.UtcNow - entry.StoredAt).TotalSeconds).ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-LyWaf-Cache"] = outcome;
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await using var body = OpenBody(entry.BodyPath);
        await body.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private void QueueRefresh(HttpRequest request, ResponseCacheEntry entry)
    {
        if (!Uri.TryCreate($"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}", UriKind.Absolute, out var uri)) return;
        refreshService.TryQueue(new ResponseCacheRefreshRequest(entry.Key, uri,
            entry.VaryHeaders.ToDictionary(x => x, x => request.Headers[x].ToString(), StringComparer.OrdinalIgnoreCase)));
    }

    private static bool IsNotModified(HttpRequest request, ResponseCacheEntry entry)
    {
        var tags = request.Headers.IfNoneMatch.ToString();
        if (entry.ETag is not null && tags.Split(',').Any(x => x.Trim() is "*" || x.Trim() == entry.ETag)) return true;
        var modifiedSince = TryDate(request.Headers.IfModifiedSince.ToString());
        return modifiedSince is not null && entry.LastModified is not null && entry.LastModified <= modifiedSince;
    }

    private static FileStream OpenBody(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static DateTimeOffset? TryDate(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    private static void RestoreHeader(IHeaderDictionary headers, string name, string value)
    {
        if (string.IsNullOrEmpty(value)) headers.Remove(name); else headers[name] = value;
    }
    private static void CopyHeader(ResponseCacheEntry entry, HttpResponse response, string name)
    {
        if (entry.Headers.TryGetValue(name, out var values)) response.Headers[name] = values;
    }
    private static bool? ReadBool(IReadOnlyDictionary<string, string>? metadata, string key)
        => metadata is not null && metadata.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : null;
    private static int? ReadInt(IReadOnlyDictionary<string, string>? metadata, string key)
        => metadata is not null && metadata.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : null;
}
