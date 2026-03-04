using System.Text.Json;
using NLog;

namespace LyWaf.Services.AuditLog;

/// <summary>
/// 审计日志条目
/// </summary>
public class AuditLogEntry
{
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 审计日志服务接口
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// 记录一条审计日志
    /// </summary>
    void Log(string username, string action, string ip);

    /// <summary>
    /// 分页查询审计日志（最新在前）
    /// </summary>
    (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50);
}

/// <summary>
/// 审计日志服务实现
/// 存储格式: JSON Lines（logs/audit.jsonl）
/// </summary>
public class AuditLogService : IAuditLogService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private static readonly string LogFilePath = Path.Combine(Directory.GetCurrentDirectory(), "logs", "audit.jsonl");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _writeLock = new();

    // 内存缓存（最新在前），避免频繁读文件
    private List<AuditLogEntry> _cache = [];
    private bool _cacheLoaded;

    public AuditLogService()
    {
        // 确保日志目录存在
        var dir = Path.GetDirectoryName(LogFilePath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Log(string username, string action, string ip)
    {
        var entry = new AuditLogEntry
        {
            Username = username,
            Action = action,
            Ip = ip,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOptions);

            lock (_writeLock)
            {
                File.AppendAllText(LogFilePath, line + "\n");

                // 同步更新缓存（插入到头部）
                EnsureCacheLoaded();
                _cache.Insert(0, entry);
            }

            _logger.Debug("审计日志: [{Username}] {Action} (IP: {Ip})", username, action, ip);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "写入审计日志失败");
        }
    }

    public (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50)
    {
        lock (_writeLock)
        {
            EnsureCacheLoaded();

            var total = _cache.Count;
            var items = _cache
                .Skip(offset)
                .Take(limit)
                .ToList();

            return (items, total);
        }
    }

    /// <summary>
    /// 确保缓存已从文件加载
    /// </summary>
    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        _cache = [];

        if (File.Exists(LogFilePath))
        {
            try
            {
                var lines = File.ReadAllLines(LogFilePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<AuditLogEntry>(line, JsonOptions);
                        if (entry != null) _cache.Add(entry);
                    }
                    catch
                    {
                        // 跳过损坏的行
                    }
                }

                // 逆序：最新在前
                _cache.Reverse();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "加载审计日志缓存失败");
            }
        }

        _cacheLoaded = true;
    }
}
