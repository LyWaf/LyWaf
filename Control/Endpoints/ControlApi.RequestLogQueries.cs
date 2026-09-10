using LyWaf.Services.ManagementApi;
using LyWaf.Utils;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LyWaf.Control;

public static partial class ControlApi
{
    // Only the caller's log family is readable; a basename alone must not grant access to other files.
    private static async Task<Results<Ok<ManagementPage<LogEntry>>, NotFound<ApiErrorEnvelope>>> QueryRequestLog(HttpContext context, string prefix, bool capture = false)
    {
        var query = ManagementListQuery.Parse(context.Request.Query, ["time"], "time", descending: true);
        var path = ResolveRequestLogPath(Value("file"), prefix);
        if (!File.Exists(path)) return TypedResults.NotFound(ManagementApiContract.CreateError(context, "not_found", "日志文件不存在"));
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("不能读取链接文件", "file");
        using var scanner = new ReverseLogScanner(path);
        return TypedResults.Ok(await scanner.QueryAsync(query, context.RequestAborted,
            capture ? Value("host") : null, capture ? Value("clientIp") : null));

        string? Value(string name)
        {
            if (!context.Request.Query.TryGetValue(name, out var values)) return null;
            if (values.Count != 1 || values[0]?.Length > 255)
                throw new ArgumentException("查询参数无效", name);
            return values[0];
        }
    }

    internal static string ResolveRequestLogPath(string? file, string prefix)
    {
        if (string.IsNullOrEmpty(file) || file.Length > 255 || file != Path.GetFileName(file) || file.Contains(':') ||
            file.Contains('\\') || file.Contains('/') || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !file.StartsWith(prefix, StringComparison.Ordinal) || !file.EndsWith(".log", StringComparison.Ordinal) || file.Contains(".."))
            throw new ArgumentException("无效的日志文件名", "file");
        var path = Path.Combine(AppContext.BaseDirectory, "logs/request_logger", file);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("不能访问链接文件", "file");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("不能访问链接目录", "file");
        return path;
    }
}
