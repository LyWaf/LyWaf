using System.Text;
using System.Text.RegularExpressions;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;

namespace LyWaf.Utils;

public static partial class LogUtil
{
    #region NLog 配置

    public static void ConfigureNLog(string projectRoot, string? accessLog, string? errorLog, bool perfLog)
    {
        var config = new LoggingConfiguration();

        var consoleTarget = new ColoredConsoleTarget("console")
        {
            Layout = @"${date:format=HH\:mm\:ss.fff} ${level:padding=-5} ${logger:shortName=true}: ${message} ${exception:format=shortType,message}"
        };

        consoleTarget.RowHighlightingRules.Add(
            new ConsoleRowHighlightingRule("level == LogLevel.Error",
                ConsoleOutputColor.Red, ConsoleOutputColor.Black));
        consoleTarget.RowHighlightingRules.Add(
            new ConsoleRowHighlightingRule("level == LogLevel.Warn",
                ConsoleOutputColor.Yellow, ConsoleOutputColor.Black));

        config.AddTarget(consoleTarget);

        accessLog ??= "ly_${shortdate}.log";
        errorLog ??= "ly_${shortdate}_err.log";
        accessLog = accessLog.Replace("#", "$");
        errorLog = errorLog.Replace("#", "$");
        var fileTarget = new FileTarget("file")
        {
            FileName = Path.Combine(projectRoot, "logs", accessLog),
            ArchiveFileName = Path.Combine(projectRoot, "logs", "archive", "{#}.log"),
            ArchiveEvery = FileArchivePeriod.Day,
            MaxArchiveFiles = 30,
            Layout = "${longdate} ${level:uppercase=true} ${logger} ${message} ${exception:format=tostring}",
            KeepFileOpen = perfLog,  // 非独占模式，每次写入后关闭文件
            Encoding = System.Text.Encoding.UTF8
        };
        config.AddTarget(fileTarget);

        var errorFileTarget = new FileTarget("errorFile")
        {
            FileName = Path.Combine(projectRoot, "logs", errorLog),
            Layout = "${longdate} ${level:uppercase=true} ${logger} ${message} ${exception:format=tostring}",
            KeepFileOpen = perfLog,  // 非独占模式，每次写入后关闭文件
        };
        config.AddTarget(errorFileTarget);

        // 添加规则
        config.AddRuleForOneLevel(NLog.LogLevel.Error, errorFileTarget);
        config.AddRuleForAllLevels(consoleTarget);
        config.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, fileTarget);

        // 应用配置
        LogManager.Configuration = config;
    }

    #endregion

    #region 日志文件解析

    /// <summary>
    /// 条目分隔符正则（匹配 ========== [2026-03-05 12:00:00.000] ==========）
    /// </summary>
    [GeneratedRegex(@"^==========\s+\[(.+?)\]\s+==========", RegexOptions.Multiline)]
    public static partial Regex EntrySeparatorRegex();

    /// <summary>
    /// 统计日志文件中的条目总数（流式扫描，不加载整个文件）
    /// </summary>
    public static async Task<int> CountEntriesAsync(string filePath)
    {
        if (!File.Exists(filePath)) return 0;

        using var scanner = new ReverseLogScanner(filePath);
        return await scanner.CountEntriesAsync();
    }

    /// <summary>
    /// 分页读取日志条目（倒序：最新在前，支持关键字搜索）
    /// 使用流式索引扫描，避免将整个文件加载到内存。
    /// </summary>
    public static async Task<(List<LogEntry> entries, int total)> ParseLogFileAsync(
        string filePath, int offset = 0, int limit = 20, string? search = null,
        DateTime? startTime = null, DateTime? endTime = null,
        string? host = null, string? clientIp = null)
    {
        if (!File.Exists(filePath))
            return ([], 0);

        using var scanner = new ReverseLogScanner(filePath);
        return await scanner.QueryAsync(offset, limit, search, startTime, endTime, host, clientIp);
    }

    /// <summary>
    /// 提取日志文件中所有不重复的 Host 值（已排序）
    /// </summary>
    public static async Task<List<string>> GetUniqueHostsAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return [];

        using var scanner = new ReverseLogScanner(filePath);
        return await scanner.GetUniqueHostsAsync();
    }

    /// <summary>
    /// 解析单条日志条目
    /// 支持格式: --- Headers ---, --- Body ---, --- Request Body ---, --- Response Body ---
    /// </summary>
    public static LogEntry ParseLogEntry(string block, int index, string? time = null)
    {
        var entry = new LogEntry { Index = index, Raw = block };
        var lines = block.Split('\n');

        // 第一行: ========== [时间] ==========
        if (time != null)
        {
            entry.Time = time;
        }
        else
        {
            var headerMatch = EntrySeparatorRegex().Match(lines[0]);
            if (headerMatch.Success)
                entry.Time = headerMatch.Groups[1].Value.Trim();
        }

        // 第二行: METHOD PATH PROTOCOL
        if (lines.Length > 1)
        {
            var requestLine = lines[1].Trim();
            entry.RequestLine = requestLine;
            var parts = requestLine.Split(' ', 3);
            if (parts.Length >= 2)
            {
                entry.Method = parts[0];
                entry.Url = parts[1];
            }
        }

        // 第三行: Host
        if (lines.Length > 2)
        {
            var hostLine = lines[2].Trim();
            if (hostLine.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                entry.Host = hostLine["Host:".Length..].Trim();
        }

        // 提取 Client-IP、Scheme 和 Status/Duration
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Client-IP:", StringComparison.OrdinalIgnoreCase))
            {
                entry.ClientIp = trimmed["Client-IP:".Length..].Trim();
            }
            else if (trimmed.StartsWith("Scheme:", StringComparison.OrdinalIgnoreCase))
            {
                entry.Scheme = trimmed["Scheme:".Length..].Trim();
            }
            else if (trimmed.StartsWith("Reason:", StringComparison.OrdinalIgnoreCase))
            {
                entry.Reason = trimmed["Reason:".Length..].Trim();
            }
            else if (trimmed.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
            {
                // 格式: "Status: 200" 或 "Status: 200 | Duration: 12.5ms"
                var statusStr = trimmed["Status:".Length..].Trim();
                var pipeIdx = statusStr.IndexOf('|');
                if (pipeIdx >= 0)
                {
                    var codePart = statusStr[..pipeIdx].Trim();
                    var durationPart = statusStr[(pipeIdx + 1)..].Trim();
                    if (int.TryParse(codePart, out var code)) entry.StatusCode = code;
                    if (durationPart.StartsWith("Duration:", StringComparison.OrdinalIgnoreCase))
                        entry.Duration = durationPart["Duration:".Length..].Trim();
                    else
                        entry.Duration = durationPart;
                }
                else
                {
                    if (int.TryParse(statusStr, out var code)) entry.StatusCode = code;
                }
            }
        }

        // 解析区域标记
        var headersStart = -1;
        var reqBodyStart = -1;
        var resBodyStart = -1;
        var bodyStart = -1; // 兼容 "--- Body ---"
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "--- Headers ---") headersStart = i + 1;
            else if (trimmed == "--- Request Body ---") reqBodyStart = i + 1;
            else if (trimmed == "--- Response Body ---") resBodyStart = i + 1;
            else if (trimmed == "--- Body ---") bodyStart = i + 1;
        }

        // 提取 headers（排除 Status:/Client-IP: 等元数据行）
        if (headersStart >= 0)
        {
            var headerLines = new List<string>();
            for (int i = headersStart; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("---")) break;
                if (string.IsNullOrEmpty(trimmed)) continue;
                // 跳过非 HTTP 头的元数据行
                if (trimmed.StartsWith("Status:", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.StartsWith("Client-IP:", StringComparison.OrdinalIgnoreCase)) continue;
                headerLines.Add(lines[i].TrimEnd());
            }
            entry.Headers = string.Join("\n", headerLines);
        }

        // 提取 request body（或兼容 "--- Body ---"）
        var rbStart = reqBodyStart >= 0 ? reqBodyStart : bodyStart;
        if (rbStart >= 0)
        {
            var bodyLines = new List<string>();
            for (int i = rbStart; i < lines.Length; i++)
            {
                if (lines[i].Trim().StartsWith("---")) break;
                bodyLines.Add(lines[i]);
            }
            entry.RequestBody = string.Join("\n", bodyLines).TrimEnd();
        }

        // 提取 response body
        if (resBodyStart >= 0)
        {
            var bodyLines = new List<string>();
            for (int i = resBodyStart; i < lines.Length; i++)
            {
                if (lines[i].Trim().StartsWith("---")) break;
                bodyLines.Add(lines[i]);
            }
            entry.ResponseBody = string.Join("\n", bodyLines).TrimEnd();
        }

        return entry;
    }

    /// <summary>
    /// 列举目录中的日志文件
    /// </summary>
    public static List<LogFile> ListLogFiles(string directory, string pattern = "*.log")
    {
        if (!Directory.Exists(directory))
            return [];

        return Directory.GetFiles(directory, pattern)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new LogFile
            {
                Name = f.Name,
                Size = f.Length,
                LastModified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
            })
            .ToList();
    }

    /// <summary>
    /// 清空日志文件（保留文件，清除所有内容）
    /// </summary>
    public static async Task ClearLogFileAsync(string filePath)
    {
        if (File.Exists(filePath))
            await File.WriteAllTextAsync(filePath, "", Encoding.UTF8);
    }

    /// <summary>
    /// 删除日志文件中的指定条目（按原始索引，流式处理避免大内存分配）
    /// </summary>
    public static async Task<bool> DeleteLogEntryAsync(string filePath, int entryIndex)
    {
        if (!File.Exists(filePath)) return false;

        using var scanner = new ReverseLogScanner(filePath);
        return await scanner.DeleteEntryAsync(filePath, entryIndex);
    }

    #endregion
}

/// <summary>
/// 统一的结构化日志条目
/// </summary>
public class LogEntry
{
    public int Index { get; set; }
    public string Time { get; set; } = "";
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public string RequestLine { get; set; } = "";
    public string? Headers { get; set; }
    public string? RequestBody { get; set; }
    public string? ResponseBody { get; set; }
    /// <summary>HTTP 状态码</summary>
    public int? StatusCode { get; set; }
    /// <summary>响应时间（如 "12.5ms"）</summary>
    public string? Duration { get; set; }
    public string? ClientIp { get; set; }
    /// <summary>请求协议（http/https）</summary>
    public string? Scheme { get; set; }
    /// <summary>拦截原因</summary>
    public string? Reason { get; set; }
    public string Raw { get; set; } = "";
}

/// <summary>
/// 日志文件信息
/// </summary>
public class LogFile
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string LastModified { get; set; } = "";
}
