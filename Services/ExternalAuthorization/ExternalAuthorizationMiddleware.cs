using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using LyWaf.Services.Metrics;
using Yarp.ReverseProxy.Model;

namespace LyWaf.Services.ExternalAuthorization;

/// <summary>
/// 在回源前调用外部授权服务。调用失败按 Route 的 FailOpen/FailClosed 策略处理，
/// 授权响应只可写入显式允许的身份 Header。
/// </summary>
public sealed class ExternalAuthorizationMiddleware(RequestDelegate next, IHttpClientFactory clients,
    IOptionsMonitor<ExternalAuthorizationOptions> options, ExternalAuthorizationCache cache,
    PrometheusMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var route = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config;
        var settings = options.CurrentValue;
        var policy = ExternalAuthorizationPolicy.FromMetadata(route?.Metadata, settings);
        if (!policy.Enabled)
        {
            await next(context);
            return;
        }

        ExternalAuthorizationResponse? decision = null;
        var startedAt = Stopwatch.GetTimestamp();
        var cacheHit = false;
        try
        {
            if (policy.Endpoint == null) throw new InvalidOperationException("外部授权 Endpoint 无效");
            var request = await BuildRequestAsync(context, policy, settings, context.RequestAborted);
            var cacheKey = BuildCacheKey(route?.RouteId, request, settings.CacheKeyHeaders);
            var now = DateTimeOffset.UtcNow;
            if (!cache.TryGet(cacheKey, now, out decision))
            {
                decision = await AuthorizeAsync(policy, request, settings, context.RequestAborted);
                var ttl = Math.Clamp(decision.CacheSeconds ?? policy.CacheSeconds, 0, policy.CacheSeconds);
                cache.Set(cacheKey, decision, now, ttl, settings.MaxCacheEntries);
            }
            else cacheHit = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            if (policy.FailOpen)
            {
                metrics.ObserveExternalAuthorization("fail_open", Stopwatch.GetElapsedTime(startedAt));
                await next(context);
                return;
            }
            metrics.ObserveExternalAuthorization("fail_closed", Stopwatch.GetElapsedTime(startedAt));
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "外部授权服务不可用" }, context.RequestAborted);
            return;
        }

        if (!decision.Allowed)
        {
            metrics.ObserveExternalAuthorization(cacheHit ? "cache_hit" : "denied", Stopwatch.GetElapsedTime(startedAt));
            context.Response.StatusCode = decision.StatusCode is >= 400 and <= 599
                ? decision.StatusCode : StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { success = false, message = decision.Reason ?? "请求未获授权" },
                context.RequestAborted);
            return;
        }

        metrics.ObserveExternalAuthorization(cacheHit ? "cache_hit" : "allowed", Stopwatch.GetElapsedTime(startedAt));
        var allowedHeaders = settings.AllowedUpstreamHeaders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in decision.Headers ?? [])
            if (allowedHeaders.Contains(name)) context.Request.Headers[name] = value;
        await next(context);
    }

    internal static async Task<ExternalAuthorizationRequest> BuildRequestAsync(HttpContext context,
        ExternalAuthorizationPolicy policy, ExternalAuthorizationOptions settings, CancellationToken token)
    {
        var allowed = settings.AllowedRequestHeaders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var headerBytes = 0;
        var maxHeaderBytes = Math.Clamp(settings.MaxHeaderBytes, 0, 1_048_576);
        foreach (var item in context.Request.Headers.Where(item => allowed.Contains(item.Key)))
        {
            var values = item.Value.Where(static value => value != null).Select(static value => value!).ToArray();
            var bytes = Encoding.UTF8.GetByteCount(item.Key) + values.Sum(Encoding.UTF8.GetByteCount);
            if (headerBytes + bytes > maxHeaderBytes) throw new InvalidDataException("外部授权 Header 超过上限");
            headerBytes += bytes;
            headers[item.Key] = values;
        }

        byte[]? body = null;
        var truncated = context.Request.ContentLength > policy.MaxBodyBytes;
        if (!truncated && policy.MaxBodyBytes > 0 && HasBody(context.Request.Method))
        {
            context.Request.EnableBuffering(Math.Min(policy.MaxBodyBytes, 64 * 1024));
            try
            {
                using var output = new MemoryStream();
                var buffer = new byte[Math.Min(policy.MaxBodyBytes + 1, 64 * 1024)];
                while (output.Length <= policy.MaxBodyBytes)
                {
                    var remaining = Math.Min(buffer.Length, policy.MaxBodyBytes + 1 - (int)output.Length);
                    var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, remaining), token);
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                }
                if (output.Length > policy.MaxBodyBytes) truncated = true;
                else body = output.ToArray();
            }
            finally { context.Request.Body.Position = 0; }
        }
        return new ExternalAuthorizationRequest(context.Request.Method, context.Request.Scheme,
            context.Request.Host.ToString(), context.Request.PathBase.Add(context.Request.Path) + context.Request.QueryString,
            headers, body == null ? null : Convert.ToBase64String(body), truncated);
    }

    internal async Task<ExternalAuthorizationResponse> AuthorizeAsync(ExternalAuthorizationPolicy policy,
        ExternalAuthorizationRequest payload, ExternalAuthorizationOptions settings, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, policy.Endpoint)
        {
            Content = JsonContent.Create(payload)
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(policy.TimeoutMilliseconds);
        using var response = await clients.CreateClient("external-authorization")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(
            $"外部授权返回 HTTP {(int)response.StatusCode}");
        var maxBytes = Math.Clamp(settings.MaxResponseBytes, 1024, 1_048_576);
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("外部授权响应超过上限");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(maxBytes + 1, 16 * 1024)];
        while (output.Length <= maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, maxBytes + 1 - (int)output.Length)), token);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        if (output.Length > maxBytes) throw new InvalidDataException("外部授权响应超过上限");
        return JsonSerializer.Deserialize<ExternalAuthorizationResponse>(output.ToArray(),
                   new JsonSerializerOptions(JsonSerializerDefaults.Web))
               ?? throw new InvalidDataException("外部授权响应为空");
    }

    internal static string BuildCacheKey(string? routeId, ExternalAuthorizationRequest request,
        IReadOnlyList<string> cacheKeyHeaders)
    {
        var builder = new StringBuilder().Append(routeId).Append('\n').Append(request.Method).Append('\n')
            .Append(request.Host).Append('\n').Append(request.Path).Append('\n').Append(request.BodyBase64);
        foreach (var header in cacheKeyHeaders.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            if (request.Headers.TryGetValue(header, out var values)) builder.Append('\n').Append(header).Append('=')
                .AppendJoin('\0', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static bool HasBody(string method) => method is "POST" or "PUT" or "PATCH";
}
