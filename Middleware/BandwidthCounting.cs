using LyWaf.Shared;
using LyWaf.Utils;

namespace LyWaf.Middleware;

/// <summary>
/// 精确统计每个请求的入站/出站字节数
/// 放在管道最外层，包装 Response.Body 为 CountingStream
/// </summary>
public class BandwidthCountingMiddleware
{
    private readonly RequestDelegate _next;

    public BandwidthCountingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 包装 Response.Body
        var originalBody = context.Response.Body;
        var countingResponse = new CountingStream(originalBody);
        context.Response.Body = countingResponse;

        try
        {
            await _next(context);
        }
        finally
        {
            // 恢复原始 Body
            context.Response.Body = originalBody;

            // 计算入站字节
            long inboundBytes = EstimateRequestHeaderSize(context.Request);
            // Request body：优先 Body.Position（EnableBuffering 后可 seek）
            if (context.Request.Body.CanSeek)
                inboundBytes += context.Request.Body.Position;
            else if (context.Request.ContentLength is > 0)
                inboundBytes += context.Request.ContentLength.Value;

            // 出站字节：CountingStream 精确统计 + 响应头估算
            long outboundBytes = countingResponse.BytesWritten;
            outboundBytes += EstimateResponseHeaderSize(context.Response);

            SharedData.Bandwidth.Record(inboundBytes, outboundBytes);
        }
    }

    private static long EstimateRequestHeaderSize(HttpRequest request)
    {
        // "GET /path HTTP/1.1\r\n"
        long size = (request.Method?.Length ?? 3) + 1
                    + (request.Path.Value?.Length ?? 0)
                    + (request.QueryString.Value?.Length ?? 0) + 12;
        foreach (var header in request.Headers)
        {
            size += header.Key.Length + 4; // ": " + "\r\n"
            foreach (var v in header.Value)
                size += v?.Length ?? 0;
        }
        return size;
    }

    private static long EstimateResponseHeaderSize(HttpResponse response)
    {
        long size = 20; // "HTTP/1.1 200 OK\r\n\r\n"
        foreach (var header in response.Headers)
        {
            size += header.Key.Length + 4;
            foreach (var v in header.Value)
                size += v?.Length ?? 0;
        }
        return size;
    }
}
