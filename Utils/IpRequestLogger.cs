using System.Collections.Concurrent;
using System.Text;
using NLog;

namespace LyWaf.Utils;

/// <summary>
/// IP 请求日志记录器
/// 将指定 IP 的完整请求内容记录到文件中，每个请求最多记录 200KB
/// 日志解析和查询统一使用 LogUtil
/// </summary>
public static class IpRequestLogger
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
    public static Task<int> CountEntriesAsync(string ip)
        => LogUtil.CountEntriesAsync(GetLogFilePath(ip));

    /// <summary>
    /// 分页读取日志条目（倒序：最新在前）
    /// </summary>
    public static async Task<List<LogEntry>> ReadEntriesAsync(string ip, int offset = 0, int limit = 50)
    {
        var (entries, _) = await LogUtil.ParseLogFileAsync(GetLogFilePath(ip), offset, limit);
        return entries;
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
