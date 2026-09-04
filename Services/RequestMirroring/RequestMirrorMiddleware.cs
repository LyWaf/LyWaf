using Yarp.ReverseProxy.Model;

namespace LyWaf.Services.RequestMirroring;

/// <summary>
/// 按 Route 策略生成有限大小的请求快照并尝试加入镜像队列。
/// 入队、发送和镜像响应均不会改变主请求的状态码或完成时间。
/// </summary>
public sealed class RequestMirrorMiddleware(RequestDelegate next, RequestMirrorDispatcher dispatcher)
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Host", "Content-Length"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.Metadata;
        var policy = RequestMirrorPolicy.FromMetadata(metadata);
        if (ShouldMirror(context, policy))
        {
            var snapshot = await CreateSnapshotAsync(context, policy, context.RequestAborted);
            if (snapshot != null) dispatcher.TryEnqueue(snapshot);
        }
        await next(context);
    }

    internal static bool ShouldMirror(HttpContext context, RequestMirrorPolicy policy)
    {
        if (!policy.Enabled || policy.Target == null || !policy.Methods.Contains(context.Request.Method)) return false;
        if (context.WebSockets.IsWebSocketRequest) return false;
        if (context.Request.Headers.ContainsKey("X-LyWaf-Mirror")) return false;
        if (context.Request.ContentLength is { } length && length > policy.MaxBodyBytes) return false;
        if (HasBody(context.Request.Method) && context.Request.ContentLength is null) return false;
        return policy.Percentage >= 100 || Random.Shared.NextDouble() * 100 < policy.Percentage;
    }

    internal static async Task<MirroredRequest?> CreateSnapshotAsync(HttpContext context,
        RequestMirrorPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.Target == null) return null;
        byte[]? body = null;
        if (context.Request.ContentLength is > 0 and var length)
        {
            if (length > policy.MaxBodyBytes || length > int.MaxValue) return null;
            context.Request.EnableBuffering(Math.Min((int)length, 64 * 1024), policy.MaxBodyBytes);
            body = new byte[(int)length];
            try
            {
                var offset = 0;
                while (offset < body.Length)
                {
                    var read = await context.Request.Body.ReadAsync(body.AsMemory(offset), cancellationToken);
                    if (read == 0) return null;
                    offset += read;
                }
            }
            finally
            {
                context.Request.Body.Position = 0;
            }
        }

        var headers = context.Request.Headers
            .Where(item => policy.AllowedHeaders.Contains(item.Key) && !HopByHopHeaders.Contains(item.Key) &&
                           !item.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static item => item.Key,
                static item => item.Value.Where(static value => value != null).Select(static value => value!).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        headers["X-LyWaf-Mirror"] = ["true"];
        var relative = context.Request.PathBase.Add(context.Request.Path) + context.Request.QueryString;
        return new MirroredRequest(new HttpMethod(context.Request.Method), new Uri(policy.Target, relative), headers,
            body, context.Request.ContentType);
    }

    private static bool HasBody(string method) => method is "POST" or "PUT" or "PATCH";
}
