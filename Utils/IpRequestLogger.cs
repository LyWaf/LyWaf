using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using NLog;

namespace LyWaf.Utils;

/// <summary>
/// IP 请求日志记录器
/// 将指定 IP 的完整请求内容记录到文件中，每个请求最多记录 200KB
/// 日志以 ========== [...] ========== 为分隔符，每段为一个请求条目
/// </summary>
public static partial class IpRequestLogger
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 每个请求最大记录字节数 (200KB)
    /// </summary>
    public const int MaxRequestBytes = 200 * 1024;

    /// <summary>
    /// 日志存储目录
    /// </summary>
    private static readonly string LogDir = Path.Combine(AppContext.BaseDirectory, "logs", "ip_capture");

    /// <summary>
    /// 用于文件写入的锁，避免同一 IP 的并发写入冲突
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    /// <summary>
    /// 条目分隔符正则（匹配 ========== [2026-02-27 12:00:00.000] ==========）
    /// </summary>
    [GeneratedRegex(@"^==========\s+\[(.+?)\]\s+==========", RegexOptions.Multiline)]
    private static partial Regex EntrySeparatorRegex();

    /// <summary>
    /// 将已捕获的日志条目写入文件（由中间件调用）
    /// </summary>
    public static async Task WriteLogEntryAsync(string clientIp, string logEntry)
    {
        try
        {
            Directory.CreateDirectory(LogDir);

            var safeIp = clientIp.Replace(':', '_').Replace('.', '_');
            var filePath = Path.Combine(LogDir, $"{safeIp}.log");

            var fileLock = _fileLocks.GetOrAdd(clientIp, _ => new SemaphoreSlim(1, 1));
            await fileLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(filePath, logEntry, Encoding.UTF8);
            }
            finally
            {
                fileLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "记录 IP {ip} 的请求日志失败", clientIp);
        }
    }

    /// <summary>
    /// 获取指定 IP 的日志文件路径
    /// </summary>
    public static string GetLogFilePath(string ip)
    {
        var safeIp = ip.Replace(':', '_').Replace('.', '_');
        return Path.Combine(LogDir, $"{safeIp}.log");
    }

    /// <summary>
    /// 获取指定 IP 的日志文件信息
    /// </summary>
    public static (bool exists, long sizeBytes, DateTime? lastWriteTime) GetLogFileInfo(string ip)
    {
        var path = GetLogFilePath(ip);
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            return (true, info.Length, info.LastWriteTime);
        }
        return (false, 0, null);
    }

    /// <summary>
    /// 统计日志文件中的条目总数
    /// </summary>
    public static async Task<int> CountEntriesAsync(string ip)
    {
        var path = GetLogFilePath(ip);
        if (!File.Exists(path)) return 0;

        var content = await File.ReadAllTextAsync(path, Encoding.UTF8);
        return EntrySeparatorRegex().Matches(content).Count;
    }

    /// <summary>
    /// 分页读取日志条目（按条目分割，支持 offset + limit）
    /// 返回结构化的条目列表
    /// </summary>
    public static async Task<List<LogEntry>> ReadEntriesAsync(string ip, int offset = 0, int limit = 50)
    {
        var path = GetLogFilePath(ip);
        if (!File.Exists(path)) return [];

        var content = await File.ReadAllTextAsync(path, Encoding.UTF8);
        var matches = EntrySeparatorRegex().Matches(content);

        if (matches.Count == 0) return [];

        var entries = new List<LogEntry>();

        // 倒序遍历：最新的条目在最前面
        for (int ri = 0; ri < matches.Count; ri++)
        {
            if (ri < offset) continue;
            if (entries.Count >= limit) break;

            // 将倒序索引映射到原始索引
            var i = matches.Count - 1 - ri;
            var startIdx = matches[i].Index;
            var endIdx = (i + 1 < matches.Count) ? matches[i + 1].Index : content.Length;
            var block = content[startIdx..endIdx].TrimEnd();

            var entry = ParseEntry(block, i);
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// 解析一个日志条目块为结构化对象
    /// </summary>
    private static LogEntry ParseEntry(string block, int index)
    {
        var entry = new LogEntry { Index = index, Raw = block };
        var lines = block.Split('\n');

        // 第一行: ========== [时间] ==========
        var headerMatch = EntrySeparatorRegex().Match(lines[0]);
        if (headerMatch.Success)
        {
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
            {
                entry.Host = hostLine[5..].Trim();
            }
        }

        // 解析 Headers 和 Body 区域
        var headersStart = -1;
        var bodyStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "--- Headers ---") headersStart = i + 1;
            else if (trimmed == "--- Body ---") bodyStart = i + 1;
        }

        // 提取 headers
        if (headersStart >= 0)
        {
            var headersEnd = bodyStart >= 0 ? bodyStart - 1 : lines.Length;
            var headerLines = new List<string>();
            for (int i = headersStart; i < headersEnd; i++)
            {
                var line = lines[i].TrimEnd();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.Trim().StartsWith("---")) break;
                headerLines.Add(line);
            }
            entry.Headers = string.Join("\n", headerLines);
        }

        // 提取 body
        if (bodyStart >= 0)
        {
            var bodyLines = new List<string>();
            for (int i = bodyStart; i < lines.Length; i++)
            {
                bodyLines.Add(lines[i]);
            }
            entry.Body = string.Join("\n", bodyLines).TrimEnd();
        }

        return entry;
    }

    /// <summary>
    /// 删除指定 IP 的日志文件
    /// </summary>
    public static bool DeleteLog(string ip)
    {
        var path = GetLogFilePath(ip);
        if (File.Exists(path))
        {
            File.Delete(path);
            return true;
        }
        return false;
    }
}

/// <summary>
/// 结构化日志条目
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
    public string? Body { get; set; }
    public string Raw { get; set; } = "";
}
