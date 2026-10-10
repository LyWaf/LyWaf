using System.IO.Compression;
using LyWaf.Plugins.Core;
using Microsoft.Extensions.Options;

namespace LyWaf.Plugins.Sys.HttpUpload;

/// <summary>Streams uploads to staging storage and publishes only complete new files/directories.</summary>
public sealed class HttpUploadMiddleware(RequestDelegate next, IOptionsMonitor<HttpUploadOptions> options,
    IPluginManager manager, ILogger<HttpUploadMiddleware> logger)
{
    // Bound disk and extraction work across all requests, without retaining an unbounded waiting queue.
    private static readonly SemaphoreSlim Slots = new(2);

    public async Task InvokeAsync(HttpContext context)
    {
        var settings = options.CurrentValue;
        if (!context.Request.Path.Equals(settings.Path, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }
        if (!settings.Enabled || !manager.IsPermissionGranted("http-upload", PluginPermission.DataDirectory)
            || !manager.IsPermissionGranted("http-upload", PluginPermission.SensitiveConfiguration)
            || settings.Key.Length < 32 || settings.SignatureWindowSeconds is < 1 or > 3600)
        {
            context.Response.StatusCode = 503;
            return;
        }
        if (!IsHostAllowed(context.Request.Host.Host, settings.Hosts)
            || (settings.RequireHttps && !context.Request.IsHttps))
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.Headers.Allow = "POST";
            context.Response.StatusCode = 405;
            return;
        }
        if (!await Slots.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = 429;
            return;
        }
        string? staging = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            var token = deadline.Token;
            var form = await UploadForm.ReadAsync(context.Request, Math.Max(settings.MaxUploadBytes, 1024), token);
            var name = form.Fields["filename"];
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!UploadSignature.Verify(settings.Key, name, form.Fields["size"], form.Fields["timestamp"],
                form.Fields["nonce"], form.Fields["signature"], now, settings.SignatureWindowSeconds, out var expectedBytes))
            {
                context.Response.StatusCode = 401;
                return;
            }
            if (expectedBytes > settings.MaxUploadBytes) throw new UploadLimitException();
            if (string.IsNullOrWhiteSpace(settings.Directory) || !Path.IsPathFullyQualified(settings.Directory))
            {
                logger.LogWarning("HTTP upload rejected: Directory must be configured as an absolute path");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new {
                    code = "upload_directory_not_configured",
                    message = "HTTP 上传插件未配置有效的 Directory，请在后台设置服务器绝对目录。"
                }, context.RequestAborted);
                return;
            }
            if (!UploadSignature.Consume(settings.NonceDirectory, form.Fields["nonce"], now))
            {
                context.Response.StatusCode = 409;
                return;
            }
            var extractValue = form.Fields.GetValueOrDefault("extract", "false");
            if (extractValue is not ("" or "true" or "false")) throw new InvalidDataException();
            var extract = extractValue == "true";
            var configured = settings.Directory;
            if (!Path.IsPathFullyQualified(configured)
                || !IsSafeName(name) || (extract && !settings.AllowZip)
                || settings.MaxUploadBytes <= 0 || settings.MaxExtractedBytes <= 0 || settings.MaxEntries <= 0)
                throw new InvalidDataException();
            var root = Path.GetFullPath(configured);
            CheckDirectory(root);
            Directory.CreateDirectory(root);
            CheckDirectory(root);
            var directory = form.Fields.GetValueOrDefault("directory", "");
            var destination = ResolveDestination(root, directory, name, extract);
            CheckDirectory(Path.GetDirectoryName(destination)!);
            for (var ancestor = Path.GetDirectoryName(destination); ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if (File.Exists(Path.Combine(ancestor, ".lywaf-current")))
                    throw new InvalidDataException("Cannot modify a child of a versioned publication.");
                if (Path.GetFullPath(ancestor).Equals(Path.TrimEndingDirectorySeparator(root),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            }
            if (File.Exists(destination) || (!extract && Directory.Exists(destination)))
            {
                context.Response.StatusCode = 409;
                return;
            }
            staging = Path.Combine(root, ".upload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var payload = Path.Combine(staging, "payload");
            await using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var actualBytes = await CopyBoundedAsync(form.File, output, expectedBytes, token);
                if (actualBytes != expectedBytes) throw new InvalidDataException("File size does not match signature.");
            }
            if (await form.Reader.ReadNextSectionAsync(token) != null)
                throw new InvalidDataException("File must be the final form section.");
            var parent = Path.GetDirectoryName(destination)!;
            CheckDirectory(parent);
            Directory.CreateDirectory(parent);
            CheckDirectory(parent);
            if (extract)
            {
                var unpacked = Path.Combine(staging, "unpacked");
                Directory.CreateDirectory(unpacked);
                await ExtractAsync(payload, unpacked, settings, token);
                CheckDirectory(parent);
                LyWaf.Services.Files.DirectoryPublication.Publish(root, destination, unpacked);
            }
            else
            {
                CheckDirectory(parent);
                File.Move(payload, destination, false);
            }
            logger.LogInformation("HTTP upload published: name {Name}, directory {Directory}, zip {Extract}", name, directory, extract);
            context.Response.StatusCode = 201;
            await context.Response.WriteAsJsonAsync(new { name, directory, extracted = extract }, context.RequestAborted);
        }
        catch (UploadLimitException) { context.Response.StatusCode = 413; }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "HTTP upload rejected due to invalid form or archive");
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new {
                code = "invalid_upload", message = "上传表单、文件大小或 ZIP 内容不合法，请按请求 ID 查询服务端日志。",
                requestId = context.TraceIdentifier
            }, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException) { context.Response.StatusCode = 408; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "HTTP upload could not be published");
            context.Response.StatusCode = 400;
        }
        finally
        {
            if (staging != null)
            {
                try { CheckDirectory(staging); Directory.Delete(staging, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning(ex, "HTTP upload staging cleanup failed"); }
            }
            Slots.Release();
        }
    }

    /// <summary>Matches host names without ports; an empty allowlist denies all hosts.</summary>
    public static bool IsHostAllowed(string host, IEnumerable<string>? hosts)
    {
        if (hosts == null) return false;
        host = host.TrimEnd('.');
        return hosts.Any(pattern => pattern != null && (pattern.Trim() == "*"
            || (!string.IsNullOrEmpty(host) && string.Equals(host, pattern.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Resolves a client subdirectory without allowing absolute paths or parent traversal.</summary>
    public static string ResolveDestination(string root, string directory, string filename, bool extract)
    {
        if (!IsSafeName(filename)) throw new InvalidDataException("Invalid filename.");
        if (directory.Length == 0) return Path.Combine(root, filename);
        var parts = directory.Split('/');
        if (directory.Length > 1000 || parts.Length > 16 || parts.Any(p => !IsSafeName(p)))
            throw new InvalidDataException("Invalid relative directory.");
        var folder = Path.Combine(root, Path.Combine(parts));
        return extract ? folder : Path.Combine(folder, filename);
    }

    public static bool IsSafeName(string name) => name.Length is > 0 and <= 200
        && name != "." && name != ".." && !name.EndsWith('.') && !name.EndsWith(' ')
        && !name.StartsWith(".upload-", StringComparison.OrdinalIgnoreCase)
        && !LyWaf.Services.Files.DirectoryPublication.IsInternalName(name)
        && !name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))
        && !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase);

    internal static void CheckDirectory(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if ((current.Exists || current.LinkTarget != null) && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Upload directories cannot contain symbolic links or junctions.");
    }

    public static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            if (count > limit - total) throw new UploadLimitException();
            total += count;
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return total;
    }

    public static async Task ExtractAsync(string archivePath, string directory, HttpUploadOptions settings, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > settings.MaxEntries) throw new UploadLimitException();
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = entry.FullName.TrimEnd('/');
            var parts = path.Split('/');
            if (parts.Length > 32 || parts.Any(p => !IsSafeName(p)) || !seen.Add(path)
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Unsafe ZIP entry.");
            var target = Path.Combine(directory, Path.Combine(parts));
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            if (entry.Length > settings.MaxExtractedBytes - total) throw new UploadLimitException();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            total += await CopyBoundedAsync(input, output, settings.MaxExtractedBytes - total, token);
        }
    }

    public sealed class UploadLimitException : IOException { }
}
