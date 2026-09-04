using System.Buffers;
using System.Text.Json;

namespace LyWaf.Services.Discovery;

/// <summary>以固定上限流式接收远程 JSON，防止发现端异常响应形成短期内存炸弹。</summary>
internal static class BoundedJsonResponse
{
    public static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        maxBytes = Math.Max(1, maxBytes);
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"服务发现响应超过 {maxBytes} 字节限制");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream((int)Math.Min(maxBytes, 64 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                total += read;
                if (total > maxBytes) throw new InvalidDataException($"服务发现响应超过 {maxBytes} 字节限制");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            output.Position = 0;
            return await JsonDocument.ParseAsync(output, cancellationToken: cancellationToken);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
