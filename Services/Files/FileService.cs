using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace LyWaf.Services.Files;

/// <summary>解析和校验静态文件路径，并生成不持有文件句柄的发送描述。</summary>
public interface IFileService
{
    Task<FileGetInfo?> GetFileRealAsync(HttpRequest request, FileEveryConfig config, string requestPath, string path);
    Task<FileServiceResult> GetFileAsync(FileEveryConfig config, string requestPath, string realPath, RangeHeaderValue? range = null);
    Task<FileMetadata> GetFileMetadataAsync(string path);
    bool IsVisible(FileEveryConfig config, string path);
}

/// <summary>静态文件安全解析、MIME 识别和字节范围规范化服务。</summary>
public sealed class FileService : IFileService
{
    private static readonly HashSet<string> PreCompressedExtensions = [".gz", ".zst", ".br"];
    private FileProviderOptions _options;

    public FileService(IOptionsMonitor<FileProviderOptions> options)
    {
        _options = options.CurrentValue;
        options.OnChange(value => _options = value);
    }

    public Task<FileGetInfo?> GetFileRealAsync(HttpRequest request, FileEveryConfig config, string requestPath, string path)
    {
        if (!TryGetSafePath(config, path, out var safePath)) return Task.FromResult<FileGetInfo?>(null);
        var encodings = AcceptedEncodings(request);

        if (config.TryFiles is { Length: > 0 })
        {
            FileGetInfo? directory = null;
            foreach (var template in config.TryFiles)
            {
                if (!TryGetSafePath(config, template.Replace("$path", path, StringComparison.Ordinal), out var candidate)) continue;
                var resolved = GetRealPath(candidate, false, config, encodings);
                if (resolved is { IsDirectory: false }) return Task.FromResult<FileGetInfo?>(resolved);
                directory ??= resolved;
            }
            return Task.FromResult(directory);
        }

        return Task.FromResult(GetRealPath(safePath, requestPath.EndsWith('/'), config, encodings));
    }

    public async Task<FileServiceResult> GetFileAsync(FileEveryConfig config, string requestPath, string realPath,
        RangeHeaderValue? range = null)
    {
        if (!IsAllowedExistingPath(config, realPath) || !File.Exists(realPath)) return FileServiceResult.NotFound();
        var fileInfo = new FileInfo(realPath);
        var metadata = await GetFileMetadataAsync(realPath);
        if (metadata.IsNotSupport() || IsDeniedExtension(config, realPath)) return FileServiceResult.NoSupportContentType();
        if (metadata.Size > Math.Max(0, config.MaxFileSize)) return FileServiceResult.FileTooLarge();

        var result = new FileServiceResult
        {
            PhysicalPath = fileInfo.FullName,
            ContentType = metadata.ContentType,
            ContentLength = fileInfo.Length,
            TotalLength = fileInfo.Length,
            LastModified = metadata.LastModified,
            ETag = metadata.ETag
        };
        if (range is null || !_options.EnableRangeProcessing) return result;

        var ranges = NormalizeRanges(range, fileInfo.Length);
        if (ranges.Count == 0) return new FileServiceResult
        {
            PhysicalPath = result.PhysicalPath,
            ContentType = result.ContentType,
            TotalLength = result.TotalLength,
            LastModified = result.LastModified,
            ETag = result.ETag,
            StatusCode = StatusCodes.Status416RangeNotSatisfiable
        };
        if (ranges.Count == 1)
        {
            var item = ranges[0];
            return new FileServiceResult
            {
                PhysicalPath = result.PhysicalPath,
                ContentType = result.ContentType,
                ContentLength = item.Length,
                TotalLength = result.TotalLength,
                LastModified = result.LastModified,
                ETag = result.ETag,
                StatusCode = StatusCodes.Status206PartialContent,
                Ranges = ranges
            };
        }

        var boundary = $"lywaf-{Guid.NewGuid():N}";
        return new FileServiceResult
        {
            PhysicalPath = result.PhysicalPath,
            ContentType = $"multipart/byteranges; boundary={boundary}",
            TotalLength = result.TotalLength,
            LastModified = result.LastModified,
            ETag = result.ETag,
            StatusCode = StatusCodes.Status206PartialContent,
            Ranges = ranges,
            Boundary = boundary
        };
    }

    public Task<FileMetadata> GetFileMetadataAsync(string realPath)
    {
        var fileInfo = new FileInfo(realPath);
        if (!fileInfo.Exists) throw new FileNotFoundException("文件不存在", realPath);
        return Task.FromResult(new FileMetadata
        {
            ContentType = GetContentType(fileInfo),
            LastModified = fileInfo.LastWriteTimeUtc,
            Size = fileInfo.Length,
            ETag = GenerateETag(fileInfo)
        });
    }

    internal static bool TryGetSafePath(FileEveryConfig config, string requestedPath, out string fullPath)
    {
        fullPath = string.Empty;
        if (requestedPath.IndexOf('\0') >= 0 || Path.IsPathRooted(requestedPath)) return false;
        try
        {
            var root = Path.GetFullPath(config.BasePath);
            var relative = requestedPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, relative));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!candidate.Equals(root, comparison) && !candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                return false;
            if (!config.ShowHidden && relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.StartsWith(".", StringComparison.Ordinal))) return false;
            if (!config.FollowLinks && ContainsReparsePoint(root, candidate)) return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<FileByteRange> NormalizeRanges(RangeHeaderValue range, long length)
    {
        if (!range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) || length <= 0 || range.Ranges.Count > 16) return [];
        var result = new List<FileByteRange>(range.Ranges.Count);
        foreach (var item in range.Ranges)
        {
            long start;
            long end;
            if (item.From.HasValue)
            {
                start = item.From.Value;
                if (start >= length) continue;
                end = Math.Min(item.To ?? length - 1, length - 1);
                if (end < start) continue;
            }
            else if (item.To is > 0)
            {
                var count = Math.Min(item.To.Value, length);
                start = length - count;
                end = length - 1;
            }
            else continue;
            result.Add(new FileByteRange(start, end));
        }
        return result;
    }

    private FileGetInfo? GetRealPath(string path, bool resolveIndex, FileEveryConfig config, IReadOnlyList<string> encodings)
    {
        if (config.PreCompressed)
        {
            foreach (var encoding in encodings)
            {
                var suffix = encoding switch { "br" => ".br", "zstd" => ".zst", "gzip" => ".gz", _ => null };
                if (suffix is not null && IsAllowedExistingPath(config, path + suffix) && File.Exists(path + suffix))
                    return new FileGetInfo(path + suffix, compress: encoding);
            }
        }
        if (IsAllowedExistingPath(config, path) && File.Exists(path) && !IsDeniedExtension(config, path)) return new FileGetInfo(path);
        if (!IsAllowedExistingPath(config, path) || !Directory.Exists(path)) return null;
        if (resolveIndex)
        {
            foreach (var name in config.Default)
            {
                if (!TryGetSafePath(new FileEveryConfig
                    {
                        BasePath = path, FollowLinks = config.FollowLinks, ShowHidden = config.ShowHidden,
                        DeniedExtensions = config.DeniedExtensions
                    }, name, out var index)) continue;
                if (File.Exists(index) && !IsDeniedExtension(config, index)) return new FileGetInfo(index);
            }
        }
        return new FileGetInfo(path, true);
    }

    private bool IsDeniedExtension(FileEveryConfig config, string path)
    {
        var extension = OriginalExtension(path);
        return config.DeniedExtensions.Contains(extension) || _options.RemoveExtensions.Contains(extension);
    }

    private bool IsAllowedExistingPath(FileEveryConfig config, string path)
    {
        var root = Path.GetFullPath(config.BasePath);
        var target = Path.GetFullPath(path);
        if (!config.ShowHidden && ContainsAttribute(root, target, FileAttributes.Hidden)) return false;
        return config.FollowLinks || !ContainsReparsePoint(root, target);
    }

    public bool IsVisible(FileEveryConfig config, string path) => !DirectoryPublication.IsInternalName(Path.GetFileName(path)) && IsAllowedExistingPath(config, path) &&
        (config.ShowHidden || !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal)) &&
        (Directory.Exists(path) || !IsDeniedExtension(config, path));

    private static bool ContainsReparsePoint(string root, string target)
        => ContainsAttribute(root, target, FileAttributes.ReparsePoint);

    private static bool ContainsAttribute(string root, string target, FileAttributes attribute)
    {
        var current = root;
        if (HasAttribute(current, attribute)) return true;
        var relative = Path.GetRelativePath(root, target);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or "." or "..") continue;
            current = Path.Combine(current, segment);
            if (HasAttribute(current, attribute)) return true;
        }
        return false;
    }

    private static bool HasAttribute(string path, FileAttributes attribute)
    {
        try { return (File.GetAttributes(path) & attribute) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static IReadOnlyList<string> AcceptedEncodings(HttpRequest request) => request.GetTypedHeaders().AcceptEncoding
        .Where(item => item.Quality.GetValueOrDefault(1) > 0)
        .OrderByDescending(item => item.Quality.GetValueOrDefault(1))
        .ThenBy(item => item.Value.Value switch { "br" => 0, "zstd" => 1, "gzip" => 2, _ => 3 })
        .Select(item => item.Value.Value ?? string.Empty)
        .ToArray();

    private string GetContentType(FileInfo info)
    {
        var extension = OriginalExtension(info.FullName);
        if (_options.MimeExtensions.TryGetValue(extension, out var value)) return value;
        if (_options.RemoveExtensions.Contains(extension)) return FileMetadata.UnspportContentType;
        return extension switch
        {
            ".avif" => "image/avif", ".css" => "text/css; charset=utf-8", ".gif" => "image/gif",
            ".htm" or ".html" => "text/html; charset=utf-8", ".ico" => "image/x-icon",
            ".jpeg" or ".jpg" => "image/jpeg", ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8", ".map" => "application/json",
            ".mp3" => "audio/mpeg", ".mp4" => "video/mp4", ".pdf" => "application/pdf",
            ".png" => "image/png", ".svg" => "image/svg+xml", ".txt" => "text/plain; charset=utf-8",
            ".wasm" => "application/wasm", ".webm" => "video/webm", ".webp" => "image/webp",
            ".woff" => "font/woff", ".woff2" => "font/woff2", ".xml" => "application/xml",
            ".zip" => "application/zip", _ => "application/octet-stream"
        };
    }

    private static string OriginalExtension(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!PreCompressedExtensions.Contains(extension)) return extension;
        return Path.GetExtension(path[..^extension.Length]).ToLowerInvariant();
    }

    private static string GenerateETag(FileInfo fileInfo)
    {
        var value = $"{fileInfo.LastWriteTimeUtc.Ticks:x}-{fileInfo.Length:x}";
        return $"\"{value}\"";
    }
}
