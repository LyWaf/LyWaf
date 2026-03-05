using System.Text;
using LyWaf.Shared;

namespace LyWaf.Utils;

/// <summary>
/// HTTP 请求/响应体捕获工具
/// 通过 LyWafCache 共享已捕获的内容，避免多个中间件/插件重复读取流
/// </summary>
public static class HttpUtil
{
    /// <summary>默认最大捕获大小 (64KB)</summary>
    public const int DefaultMaxBodySize = 65536;

    /// <summary>
    /// 捕获请求体。优先从 LyWafCache 读取缓存，否则读取流并存入缓存。
    /// 读取后自动重置流位置，确保后续中间件仍可读取。
    /// </summary>
    public static async Task<string?> CaptureRequestBodyAsync(HttpContext context, int maxBytes = DefaultMaxBodySize)
    {
        // 已有缓存直接返回
        if (context.Items.TryGetValue(LyWafCache.RequestBody, out var cached) && cached is string s && s.Length > 0)
            return s;

        var request = context.Request;
        if (request.ContentLength <= 0 && request.ContentType == null)
            return null;

        try
        {
            request.EnableBuffering();
            var body = await ReadStreamAsync(request.Body, maxBytes);
            request.Body.Position = 0;

            if (!string.IsNullOrEmpty(body))
                context.Items[LyWafCache.RequestBody] = body;

            return body;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 捕获响应体。包装 Response.Body 流，执行 next()，读取响应体并存入 LyWafCache。
    /// 如果内层已捕获（缓存存在），直接复用缓存内容，不重复解析流。
    /// </summary>
    /// <param name="context">HTTP 上下文</param>
    /// <param name="next">后续管道（内部会调用）</param>
    /// <param name="maxBytes">最大捕获大小，默认 65536</param>
    /// <returns>响应体字符串，可能为 null</returns>
    public static async Task<string?> CaptureResponseBodyAsync(HttpContext context, Func<Task> next, int maxBytes = DefaultMaxBodySize)
    {
        // 已有缓存：仍需调用 next，但无需包装流
        if (context.Items.TryGetValue(LyWafCache.ResponseBody, out var cached) && cached is string s && s.Length > 0)
        {
            await next();
            return s;
        }

        // 包装响应流
        var originalBody = context.Response.Body;
        using var memStream = new MemoryStream();
        context.Response.Body = memStream;

        try
        {
            await next();

            string? responseBody = null;

            // next() 内部可能已捕获（如内层插件），再次检查缓存
            if (context.Items.TryGetValue(LyWafCache.ResponseBody, out var innerCached) && innerCached is string inner && inner.Length > 0)
            {
                responseBody = inner;
            }
            else if (memStream.Length > 0)
            {
                memStream.Position = 0;
                responseBody = await ReadStreamAsync(memStream, maxBytes);
                if (!string.IsNullOrEmpty(responseBody))
                    context.Items[LyWafCache.ResponseBody] = responseBody;
            }

            // 将响应复制回原始流
            memStream.Position = 0;
            await memStream.CopyToAsync(originalBody);

            return responseBody;
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    /// <summary>
    /// 读取流内容为字符串（最多 maxBytes 字符）。
    /// 读取后恢复流位置（如果流支持 Seek）。
    /// </summary>
    public static async Task<string> ReadStreamAsync(Stream stream, int maxBytes = DefaultMaxBodySize)
    {
        var originalPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek) stream.Position = 0;

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var buffer = new char[maxBytes];
            var charsRead = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, charsRead);

            if (stream.CanSeek && stream.Length > maxBytes)
                content += $"\n[... 截断，原始大小: {stream.Length} 字节 ...]";

            return content;
        }
        finally
        {
            if (stream.CanSeek) stream.Position = originalPosition;
        }
    }
}
