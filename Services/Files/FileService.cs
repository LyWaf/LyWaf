using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace LyWaf.Services.Files;

public interface IFileService
{
    Task<FileGetInfo?> GetFileRealAsync(HttpRequest request, string host, string prefix, string path);
    Task<FileServiceResult> GetFileAsync(string host, string prefix, string requestPath, string realPath, RangeHeaderValue? range = null);
    Task<FileMetadata> GetFileMetadataAsync(string path);
}
public class FileService : IFileService
{
    private FileProviderOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FileService> _logger;

    private readonly static HashSet<string> PreCompressExtension = [".gz", ".zst", ".br"];
    
    // localhost, 127.0.0.1, [::1] 互相等价
    private static readonly HashSet<string> LocalhostAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "[::1]",
        "::1"
    };

    /// <summary>
    /// 从 host:port 格式中提取主机名部分
    /// </summary>
    private static string ExtractHostname(string hostWithPort)
    {
        if (string.IsNullOrEmpty(hostWithPort))
            return hostWithPort;
        
        // 处理 IPv6 地址格式 [::1]:port
        if (hostWithPort.StartsWith('['))
        {
            var bracketEnd = hostWithPort.IndexOf(']');
            if (bracketEnd > 0)
            {
                return hostWithPort[..(bracketEnd + 1)];
            }
        }
        
        // 处理普通的 host:port 格式
        var colonIndex = hostWithPort.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(hostWithPort[(colonIndex + 1)..], out _))
        {
            return hostWithPort[..colonIndex];
        }
        
        return hostWithPort;
    }
    
    /// <summary>
    /// 从 host:port 格式中提取端口部分
    /// </summary>
    private static int? ExtractPort(string hostWithPort)
    {
        if (string.IsNullOrEmpty(hostWithPort))
            return null;
        
        // 处理 IPv6 地址格式 [::1]:port
        if (hostWithPort.StartsWith('['))
        {
            var bracketEnd = hostWithPort.IndexOf(']');
            if (bracketEnd > 0 && bracketEnd + 1 < hostWithPort.Length && hostWithPort[bracketEnd + 1] == ':')
            {
                if (int.TryParse(hostWithPort[(bracketEnd + 2)..], out var port))
                {
                    return port;
                }
            }
            return null;
        }
        
        // 处理普通的 host:port 格式
        var colonIndex = hostWithPort.LastIndexOf(':');
        if (colonIndex > 0 && int.TryParse(hostWithPort[(colonIndex + 1)..], out var p))
        {
            return p;
        }
        
        return null;
    }

    /// <summary>
    /// 判断是否为本地主机地址（不含端口）
    /// </summary>
    private static bool IsLocalhost(string hostname) => LocalhostAliases.Contains(hostname);
    
    /// <summary>
    /// 判断两个主机是否匹配（考虑本地回环地址等价，支持带端口格式）
    /// </summary>
    private static bool HostEquals(string host1, string host2)
    {
        if (host1.Equals(host2, StringComparison.OrdinalIgnoreCase))
            return true;
        
        // 提取主机名和端口
        var hostname1 = ExtractHostname(host1);
        var hostname2 = ExtractHostname(host2);
        var port1 = ExtractPort(host1);
        var port2 = ExtractPort(host2);
        
        // 端口必须匹配（如果都有端口的话）
        if (port1.HasValue && port2.HasValue && port1.Value != port2.Value)
            return false;
        
        // 主机名精确匹配
        if (hostname1.Equals(hostname2, StringComparison.OrdinalIgnoreCase))
            return true;
        
        // localhost, 127.0.0.1, [::1] 互相匹配
        return IsLocalhost(hostname1) && IsLocalhost(hostname2);
    }

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

    private string? TryGetConfig(string host, string prefix, string? path, [MaybeNullWhen(false)] out FileEveryConfig? config)
    {
        // 先尝试精确匹配
        string key = $"{host}#{prefix}";
        if (_options.Everys.TryGetValue(key, out config))
        {
            return key;
        }
        
        // 尝试本地回环地址的等价匹配（带端口）
        var hostname = ExtractHostname(host);
        var port = ExtractPort(host);
        if (IsLocalhost(hostname))
        {
            foreach (var alias in LocalhostAliases)
            {
                // 带端口的别名
                if (port.HasValue)
                {
                    var aliasKeyWithPort = $"{alias}:{port}#{prefix}";
                    if (_options.Everys.TryGetValue(aliasKeyWithPort, out config))
                    {
                        return aliasKeyWithPort;
                    }
                }
                // 不带端口的别名
                var aliasKey = $"{alias}#{prefix}";
                if (_options.Everys.TryGetValue(aliasKey, out config))
                {
                    return aliasKey;
                }
            }
        }
        
        if (_options.Everys.TryGetValue(prefix, out config))
        {
            return prefix;
        }

        var normalizedPrefix = prefix.TrimEnd('/');
        if (!normalizedPrefix.StartsWith('/'))
        {
            normalizedPrefix = "/" + normalizedPrefix;
        }

        // 完整路径用于正则匹配（正则需要匹配完整路径包括文件名）
        var fullPath = normalizedPrefix;
        if (!string.IsNullOrEmpty(path))
        {
            fullPath = normalizedPrefix.TrimEnd('/') + "/" + path.TrimStart('/');
        }

        // 使用缓存来存储匹配结果，避免重复的正则匹配
        var cacheKey = $"file_config_{host}#{fullPath}";
        var matchedKey = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _options.CacheDuration;
            
            // 尝试前缀匹配和正则匹配
            // 优先匹配带 host 的键
            foreach (var kv in _options.Everys)
            {
                var everyKey = kv.Key;
                if (!everyKey.Contains('#'))
                    continue;
                    
                var parts = everyKey.Split('#', 2);
                // 使用 HostEquals 判断，支持本地回环地址等价
                if (!HostEquals(parts[0], host))
                    continue;
                    
                var pattern = parts[1];
                if (MatchPattern(normalizedPrefix, fullPath, pattern))
                {
                    return everyKey;
                }
            }
            
            // 匹配不带 host 的键
            foreach (var kv in _options.Everys)
            {
                var everyKey = kv.Key;
                if (everyKey.Contains('#'))
                    continue;
                    
                if (MatchPattern(normalizedPrefix, fullPath, everyKey))
                {
                    return everyKey;
                }
            }

            // 返回空字符串表示未匹配
            return string.Empty;
        });

        // 空字符串表示未匹配
        if (string.IsNullOrEmpty(matchedKey))
        {
            config = null;
            return null;
        }

        // 根据缓存的键获取配置
        if (_options.Everys.TryGetValue(matchedKey, out config))
        {
            return matchedKey;
        }

        config = null;
        return null;
    }

    /// <summary>
    /// 匹配路径模式
    /// 支持前缀匹配（如 /static/）和正则匹配（如 ^/show/[^/]*(\.png|\.jpg)$）
    /// </summary>
    /// <param name="prefix">前缀路径，用于前缀匹配</param>
    /// <param name="fullPath">完整路径（包含文件名），用于正则匹配</param>
    /// <param name="pattern">匹配模式</param>
    private static bool MatchPattern(string prefix, string fullPath, string pattern)
    {
        // 正则表达式匹配（以 ^ 开头）- 使用完整路径
        if (pattern.StartsWith('^'))
        {
            try
            {
                return Regex.IsMatch(fullPath, pattern);
            }
            catch
            {
                return false;
            }
        }
        
        // 前缀匹配（如 /static/）- 使用前缀
        // prefix: /static, pattern: /static/ -> true
        return prefix.StartsWith(pattern) || prefix + "/" == pattern;
    }

    public async Task<FileGetInfo?> GetFileRealAsync(HttpRequest request, string host, string prefix, string path)
    {
        var key = TryGetConfig(host, prefix, path, out var val);
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

    public async Task<FileServiceResult> GetFileAsync(string host, string prefix, string requestPath, string realPath, RangeHeaderValue? range = null)
    {
        if (!File.Exists(realPath))
            return FileServiceResult.NotFound();

        var fileInfo = new FileInfo(realPath);
        var metadata = await GetFileMetadataAsync(realPath);
        if (metadata.IsNotSupport())
        {
            return FileServiceResult.NoSupportContentType();
        }
        var key = TryGetConfig(host, prefix, requestPath, out var val);
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