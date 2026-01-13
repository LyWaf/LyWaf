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

        // 使用缓冲方式进行压缩
        var originalBodyStream = context.Response.Body;
        using var bufferStream = new MemoryStream();
        context.Response.Body = bufferStream;

        try
        {
            await next(context);

            // 重置流位置准备读取
            bufferStream.Seek(0, SeekOrigin.Begin);
            var originalLength = bufferStream.Length;

            // 恢复原始流
            context.Response.Body = originalBodyStream;

            // 检查是否应该压缩
            if (originalLength > 0 && ShouldCompress(context, originalLength))
            {
                // 压缩响应
                using var compressedStream = new MemoryStream();
                await CompressAsync(bufferStream, compressedStream, algorithm);

                var compressedLength = compressedStream.Length;

                // 只有压缩后确实更小才使用压缩版本
                if (compressedLength < originalLength)
                {
                    compressedStream.Seek(0, SeekOrigin.Begin);

                    // 修改响应头（必须在写入内容之前）
                    context.Response.Headers.Remove("Content-Length");
                    context.Response.Headers.ContentEncoding = GetEncodingName(algorithm);
                    context.Response.ContentLength = compressedLength;

                    // 添加 Vary 头
                    var vary = context.Response.Headers.Vary.ToString();
                    if (!vary.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.Headers.Append("Vary", "Accept-Encoding");
                    }

                    // 写入压缩内容
                    await compressedStream.CopyToAsync(originalBodyStream);

                    _logger.Debug("响应已压缩 ({Algorithm}): {Path}, {Original} -> {Compressed} bytes ({Ratio:P1})",
                        algorithm, context.Request.Path, originalLength, compressedLength,
                        1.0 - (double)compressedLength / originalLength);
                    return;
                }
                else
                {
                    bufferStream.Seek(0, SeekOrigin.Begin);
                }
            }

            // 不压缩，写入原始内容
            await bufferStream.CopyToAsync(originalBodyStream);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "响应压缩失败: {Path}", context.Request.Path);

            // 确保恢复原始流
            context.Response.Body = originalBodyStream;

            // 尝试写入原始内容（如果可能）
            try
            {
                if (bufferStream.Length > 0 && bufferStream.CanSeek)
                {
                    bufferStream.Seek(0, SeekOrigin.Begin);
                    await bufferStream.CopyToAsync(originalBodyStream);
                }
            }
            catch
            {
                // 忽略，可能响应已经开始
            }
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
