using System.IO.Compression;
using LyWaf.Services.Compress;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Middleware;

/// <summary>
/// 压缩算法类型
/// </summary>
public enum CompressionAlgorithm
{
    None,
    Gzip,
    Brotli
}

/// <summary>
/// 响应压缩中间件
/// 支持 Gzip 和 Brotli 压缩，MinSize 阈值控制
/// </summary>
public class ResponseCompressMiddleware : IMiddleware
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly CompressOptions _options;
    private readonly HashSet<string> _compressibleMimeTypes;

    public ResponseCompressMiddleware(IOptionsMonitor<CompressOptions> options)
    {
        _options = options.CurrentValue;
        _compressibleMimeTypes = new HashSet<string>(_options.MimeTypes, StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // 未启用压缩，直接跳过
        if (!_options.Enabled)
        {
            await next(context);
            return;
        }

        // 检查客户端支持的压缩算法
        var algorithm = GetPreferredAlgorithm(context);
        if (algorithm == CompressionAlgorithm.None)
        {
            await next(context);
            return;
        }

        // 检查 HTTPS 压缩设置
        if (context.Request.IsHttps && !_options.EnableForHttps)
        {
            await next(context);
            return;
        }

        // 使用磁盘文件缓冲，避免大响应完整驻留托管堆。
        var originalBodyStream = context.Response.Body;
        var sourcePath = Path.Combine(Path.GetTempPath(), $"lywaf-compress-{Guid.NewGuid():N}.tmp");
        var compressedPath = Path.Combine(Path.GetTempPath(), $"lywaf-compress-{Guid.NewGuid():N}.tmp");
        await using var bufferStream = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        context.Response.Body = bufferStream;

        try
        {
            await next(context);
            await bufferStream.FlushAsync(context.RequestAborted);
            var originalLength = bufferStream.Length;
            context.Response.Body = originalBodyStream;

            if (originalLength > 0 && ShouldCompress(context, originalLength))
            {
                await using var compressedStream = new FileStream(compressedPath, FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                bufferStream.Position = 0;
                await CompressAsync(bufferStream, compressedStream, algorithm);
                await compressedStream.FlushAsync(context.RequestAborted);
                var compressedLength = compressedStream.Length;

                if (compressedLength < originalLength)
                {
                    compressedStream.Position = 0;
                    context.Response.Headers.Remove("Content-Length");
                    context.Response.Headers.ContentEncoding = GetEncodingName(algorithm);
                    context.Response.ContentLength = compressedLength;
                    var vary = context.Response.Headers.Vary.ToString();
                    if (!vary.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                        context.Response.Headers.Append("Vary", "Accept-Encoding");
                    await compressedStream.CopyToAsync(originalBodyStream, context.RequestAborted);

                    _logger.Debug("响应已压缩 ({Algorithm}): {Path}, {Original} -> {Compressed} bytes ({Ratio:P1})",
                        algorithm, context.Request.Path, originalLength, compressedLength,
                        1.0 - (double)compressedLength / originalLength);
                    return;
                }
            }

            bufferStream.Position = 0;
            await bufferStream.CopyToAsync(originalBodyStream, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "响应压缩失败: {Path}", context.Request.Path);
            context.Response.Body = originalBodyStream;
            throw;
        }
        finally
        {
            context.Response.Body = originalBodyStream;
            await bufferStream.DisposeAsync();
            try { File.Delete(sourcePath); } catch { }
            try { File.Delete(compressedPath); } catch { }
        }
    }

    /// <summary>
    /// 获取客户端首选的压缩算法
    /// 优先级: Brotli > Gzip
    /// </summary>
    private CompressionAlgorithm GetPreferredAlgorithm(HttpContext context)
    {
        var acceptEncoding = context.Request.Headers.AcceptEncoding.ToString();

        // 优先检查 Brotli（压缩率更高）
        if (_options.EnableBrotli && acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            return CompressionAlgorithm.Brotli;
        }

        // 其次检查 Gzip
        if (_options.EnableGzip && acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return CompressionAlgorithm.Gzip;
        }

        return CompressionAlgorithm.None;
    }

    /// <summary>
    /// 压缩数据
    /// </summary>
    private async Task CompressAsync(Stream source, Stream destination, CompressionAlgorithm algorithm)
    {
        var level = GetCompressionLevel();

        switch (algorithm)
        {
            case CompressionAlgorithm.Brotli:
                using (var brotliStream = new BrotliStream(destination, level, leaveOpen: true))
                {
                    await source.CopyToAsync(brotliStream);
                }
                break;

            case CompressionAlgorithm.Gzip:
            default:
                using (var gzipStream = new GZipStream(destination, level, leaveOpen: true))
                {
                    await source.CopyToAsync(gzipStream);
                }
                break;
        }
    }

    /// <summary>
    /// 获取编码名称
    /// </summary>
    private static string GetEncodingName(CompressionAlgorithm algorithm)
    {
        return algorithm switch
        {
            CompressionAlgorithm.Brotli => "br",
            CompressionAlgorithm.Gzip => "gzip",
            _ => "gzip"
        };
    }

    /// <summary>
    /// 判断是否应该压缩响应
    /// </summary>
    private bool ShouldCompress(HttpContext context, long contentLength)
    {
        // 检查内容大小是否达到阈值
        if (contentLength < _options.MinSize)
            return false;

        // 检查响应状态码（只压缩成功的响应）
        var statusCode = context.Response.StatusCode;
        if (statusCode < 200 || statusCode >= 405)
            return false;

        // 检查是否已经被压缩
        var existingEncoding = context.Response.Headers.ContentEncoding.ToString();
        if (!string.IsNullOrEmpty(existingEncoding))
            return false;

        // 检查 Content-Type
        var contentType = context.Response.ContentType;
        if (string.IsNullOrEmpty(contentType))
            return false;

        // 提取 MIME 类型（去掉 charset 等参数）
        var mimeType = contentType.Split(';')[0].Trim();

        return _compressibleMimeTypes.Contains(mimeType);
    }

    /// <summary>
    /// 获取压缩级别
    /// </summary>
    private CompressionLevel GetCompressionLevel()
    {
        return _options.Level.ToLowerInvariant() switch
        {
            "fastest" => CompressionLevel.Fastest,
            "optimal" => CompressionLevel.Optimal,
            "smallestsize" => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Fastest
        };
    }
}
