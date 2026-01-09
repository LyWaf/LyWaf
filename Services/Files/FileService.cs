using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace LyWaf.Services.Files;

public interface IFileService
{
    Task<FileGetInfo?> GetFileRealAsync(HttpRequest request, string host, string prefix, string path);
    Task<FileServiceResult> GetFileAsync(string host, string prefix, string path, RangeHeaderValue? range = null);
    Task<FileMetadata> GetFileMetadataAsync(string path);
}
public class FileService : IFileService
{
    private FileProviderOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FileService> _logger;

    private readonly static HashSet<string> PreCompressExtension = [".gz", ".zst", ".br"];

    public FileService(
        IOptionsMonitor<FileProviderOptions> options, IMemoryCache cache,
        ILogger<FileService> logger)
    {
        _options = options.CurrentValue.PreDeal();
        // 可以订阅变更，但需注意生命周期和内存泄漏
        options.OnChange(newConfig =>
        {
            _options = newConfig.PreDeal();
        });
        _cache = cache;
        _logger = logger;
    }

    private string? TryGetConfig(string host, string prefix, [MaybeNullWhen(false)] out FileEveryConfig? config)
    {

        string key = $"{host}#{prefix}";
        if (_options.Everys.TryGetValue(key, out config))
        {
            return key;
        }
        if (_options.Everys.TryGetValue(prefix, out config))
        {
            return prefix;
        }
        return null;
    }

    public async Task<FileGetInfo?> GetFileRealAsync(HttpRequest request, string host, string prefix, string path)
    {
        var key = TryGetConfig(host, prefix, out var val);
        if (key == null)
        {
            return null;
        }
        var accepts = request.Headers.AcceptEncoding.ToString().Split(",").Select(item => item.Trim()).ToArray();
        var safePath = GetSafePath(val!.BasePath, path);
        var cacheKey = $"exists_{key}_{safePath}";
        if (val!.PreCompressed)
        {
            cacheKey += "_" + accepts.ToString();
        }

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _options.CacheDuration;
            if (val.TryFiles != null)
            {
                foreach (var f in val.TryFiles)
                {
                    var v = f.Replace("$path", path);
                    var safePath = GetSafePath(val.BasePath, v);

                    var real = GetRealPath(safePath, val, accepts);
                    if (real != null)
                    {
                        return real;
                    }
                }
                return null;
            }
            else
            {
                var safePath = GetSafePath(val.BasePath, path);
                return GetRealPath(safePath, val, accepts);
            }
        });
    }

    public async Task<FileServiceResult> GetFileAsync(string host, string prefix, string real, RangeHeaderValue? range = null)
    {
        if (!File.Exists(real))
            return FileServiceResult.NotFound();

        var fileInfo = new FileInfo(real);
        var metadata = await GetFileMetadataAsync(real);
        if (metadata.IsNotSupport())
        {
            return FileServiceResult.NoSupportContentType();
        }
        var key = TryGetConfig(host, prefix, out var val);
        if (key == null)
        {
            return FileServiceResult.NotFound();
        }

        if (val!.MaxFileSize * 1024 < metadata.Size)
        {
            return FileServiceResult.FileToBigger();
        }

        if (range != null)
        {
            return await HandleRangeRequest(fileInfo, range, metadata);
        }

        return await HandleFullFileRequest(fileInfo, metadata);
    }

    private static async Task<FileServiceResult> HandleFullFileRequest(FileInfo fileInfo, FileMetadata metadata)
    {
        var fileStream = new FileStream(fileInfo.FullName,
            FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new FileServiceResult
        {
            Stream = fileStream,
            ContentType = metadata.ContentType,
            FileLength = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            ETag = metadata.ETag,
            StatusCode = StatusCodes.Status200OK
        };
    }

    private static async Task<FileServiceResult> HandleRangeRequest(FileInfo fileInfo,
        RangeHeaderValue range, FileMetadata metadata)
    {
        long start = 0, end = fileInfo.Length - 1;

        if (range.Ranges.Count > 0)
        {
            var rangeItem = range.Ranges.First();
            if (rangeItem.From.HasValue)
            {
                start = rangeItem.From.Value;
                end = rangeItem.To ?? fileInfo.Length - 1;
            }
            else if (rangeItem.To.HasValue)
            {
                start = fileInfo.Length - rangeItem.To.Value;
                end = fileInfo.Length - 1;
            }
        }

        // 边界检查
        if (start < 0) start = 0;
        if (end >= fileInfo.Length) end = fileInfo.Length - 1;
        if (start > end) start = end;

        var contentLength = end - start + 1;
        var fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read);
        fileStream.Seek(start, SeekOrigin.Begin);

        return new FileServiceResult
        {
            Stream = new LimitedStream(fileStream, start, contentLength),
            ContentType = metadata.ContentType,
            FileLength = contentLength,
            TotalLength = fileInfo.Length,
            LastModified = fileInfo.LastWriteTimeUtc,
            ETag = metadata.ETag,
            StatusCode = StatusCodes.Status206PartialContent,
            RangeStart = start,
            RangeEnd = end
        };
    }

    public async Task<FileMetadata> GetFileMetadataAsync(string real)
    {
        var cacheKey = $"metadata_{real}";

        var meta = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _options.CacheDuration;

            var fileInfo = new FileInfo(real);
            return new FileMetadata
            {
                ContentType = GetContentType(fileInfo),
                LastModified = fileInfo.LastWriteTimeUtc,
                Size = fileInfo.Length,
                ETag = GenerateETag(fileInfo)
            };
        });
        return meta ?? throw new FileNotFoundException("文件不存在");
    }

    private static string GetSafePath(string basePath, string requestedPath)
    {
        if (requestedPath == "/")
        {
            return Path.GetFullPath(basePath);
        }
        var fullPath = Path.GetFullPath(Path.Combine(basePath, requestedPath));
        if (!fullPath.StartsWith(Path.GetFullPath(basePath)))
            throw new UnauthorizedAccessException("Access denied");

        return fullPath;
    }

    private static FileGetInfo? GetRealPath(string path, FileEveryConfig fileEveryConfig, string[] accepts)
    {
        if (fileEveryConfig.PreCompressed)
        {
            foreach (var accept in accepts)
            {
                var extension = accept switch
                {
                    "gzip" => ".gz",
                    "zstd" => ".zst",
                    "br" => ".br",
                    _ => "",
                };
                if (extension.Length == 0)
                {
                    continue;
                }

                string realPath = path + extension;
                if (File.Exists(realPath))
                {
                    return new FileGetInfo(realPath, false, accept);
                }
            }
        }
        if (File.Exists(path))
        {
            return new FileGetInfo(path);
        }

        if (Directory.Exists(path))
        {
            foreach (var def in fileEveryConfig.Default)
            {
                var subPath = GetSafePath(path, def);
                if (File.Exists(subPath))
                {
                    return new FileGetInfo(subPath);
                }
            }
            if (fileEveryConfig.Browse)
            {
                return new FileGetInfo(path, true);
            }
        }
        return null;
    }

    private string GetContentType(FileInfo info)
    {
        var extension = info.Extension.ToLower();
        // 压缩格式, 再往前寻找最真正的后缀
        if (PreCompressExtension.Contains(extension))
        {
            string full = info.FullName[..(info.FullName.Length - extension.Length)];
            var lastDot = full.LastIndexOf('.');
            if (lastDot != -1)
            {
                extension = full[lastDot..].ToLower();
            }
        }
        if (_options.MimeExtensions.TryGetValue(extension, out var val))
        {
            return val;
        }

        if (_options.RemoveExtensions.Contains(extension))
        {
            return FileMetadata.UnspportContentType;
        }

        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".zip" => "application/zip",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream"
        };
    }

    private static string GenerateETag(FileInfo fileInfo)
    {
        var info = $"{fileInfo.LastWriteTimeUtc.Ticks}_{fileInfo.Length}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(info))
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }
}