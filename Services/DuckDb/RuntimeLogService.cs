using System.Diagnostics;
using System.Runtime.InteropServices;
using LyWaf.Shared;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.DuckDb;

/// <summary>
/// 运行日志服务：记录进程的启动和停止信息到 DuckDB
/// </summary>
public class RuntimeLogService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IDuckDbService _db;
    private readonly DuckDbOptions _options;

    /// <summary>当前运行记录的 ID（启动时写入后获得）</summary>
    private long _currentRunId;

    /// <summary>进程启动时间</summary>
    private readonly DateTime _startTime;

    /// <summary>恢复后的基线值（用于计算当次运行的增量）</summary>
    private long _baselineTotalRequests;
    private long _baselineInterceptCount;

    public RuntimeLogService(IDuckDbService db, IOptions<DuckDbOptions> options)
    {
        _db = db;
        _options = options.Value;
        _startTime = DateTime.UtcNow;
    }

    /// <summary>
    /// 记录进程启动（在应用启动早期调用）
    /// </summary>
    public void RecordStartup()
    {
        if (!_db.IsEnabled) return;

        try
        {
            var conn = _db.GetConnection();

            // 先标记上一次未正常关闭的记录
            MarkAbnormalExits(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO runtime_logs
                (start_time, dotnet_version, os_description, process_id)
                VALUES ($1, $2, $3, $4)";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(_startTime));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(RuntimeInformation.FrameworkDescription));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(RuntimeInformation.OSDescription));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(Environment.ProcessId));
            cmd.ExecuteNonQuery();

            // 获取刚插入的记录 ID
            using var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT MAX(id) FROM runtime_logs";
            var result = idCmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                _currentRunId = Convert.ToInt64(result);
            }

            _logger.Info("运行日志：已记录进程启动 (ID={RunId})", _currentRunId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "记录进程启动失败");
        }
    }

    /// <summary>
    /// 捕获恢复后的基线值（在 StatisticRecoveryService 恢复完成后调用）
    /// 后续 shutdown 时用当前值减去基线，得到当次运行的增量
    /// </summary>
    public void CaptureBaseline()
    {
        try
        {
            var trafficSnapshot = SharedData.Traffic.GetSnapshot();
            var securitySnapshot = SharedData.Security.GetSnapshot();

            _baselineTotalRequests = trafficSnapshot.TotalRequests;
            _baselineInterceptCount = securitySnapshot.WafInterceptCount + securitySnapshot.BlacklistBlockCount
                + securitySnapshot.CcAttackCount + securitySnapshot.CrawlerDetectCount
                + securitySnapshot.GeoBlockCount + securitySnapshot.RateLimitCount;

            _logger.Info("运行日志：已捕获基线 (请求={Req}, 拦截={Int})", _baselineTotalRequests, _baselineInterceptCount);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "捕获基线失败");
        }
    }

    /// <summary>
    /// 记录进程正常关闭（在 ApplicationStopping 中调用）
    /// </summary>
    public void RecordShutdown()
    {
        if (!_db.IsEnabled || _currentRunId <= 0) return;

        try
        {
            var now = DateTime.UtcNow;
            var duration = (long)(now - _startTime).TotalSeconds;

            // 收集内存信息
            var process = Process.GetCurrentProcess();
            var peakMemoryMb = process.PeakWorkingSet64 / (1024.0 * 1024.0);
            var currentMemoryMb = process.WorkingSet64 / (1024.0 * 1024.0);

            // GC 信息
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);

            // 业务统计（减去恢复基线，得到当次运行的增量）
            var trafficSnapshot = SharedData.Traffic.GetSnapshot();
            var securitySnapshot = SharedData.Security.GetSnapshot();
            var totalRequests = Math.Max(0, trafficSnapshot.TotalRequests - _baselineTotalRequests);
            var totalIntercepts = Math.Max(0,
                securitySnapshot.WafInterceptCount + securitySnapshot.BlacklistBlockCount
                + securitySnapshot.CcAttackCount + securitySnapshot.CrawlerDetectCount
                + securitySnapshot.GeoBlockCount + securitySnapshot.RateLimitCount
                - _baselineInterceptCount);

            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE runtime_logs SET
                stop_time = $1,
                exit_reason = '正常关闭',
                duration_seconds = $2,
                peak_memory_mb = $3,
                final_memory_mb = $4,
                gc_gen0 = $5,
                gc_gen1 = $6,
                gc_gen2 = $7,
                total_requests = $8,
                total_intercepts = $9
                WHERE id = $10";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(now));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(duration));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(peakMemoryMb));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(currentMemoryMb));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter((long)gen0));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter((long)gen1));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter((long)gen2));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(totalRequests));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(totalIntercepts));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(_currentRunId));
            cmd.ExecuteNonQuery();

            _logger.Info("运行日志：已记录进程关闭 (ID={RunId}, 运行{Duration}秒, 峰值内存{PeakMb:F1}MB)",
                _currentRunId, duration, peakMemoryMb);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "记录进程关闭失败");
        }
    }

    /// <summary>
    /// 查询运行日志（分页）
    /// </summary>
    public (List<RuntimeLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 20)
    {
        var items = new List<RuntimeLogEntry>();
        var total = 0;

        if (!_db.IsEnabled) return (items, total);

        try
        {
            var conn = _db.GetConnection();

            // 获取总数
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM runtime_logs";
            var countResult = countCmd.ExecuteScalar();
            total = Convert.ToInt32(countResult);

            // 获取分页数据
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, start_time, stop_time, exit_reason, duration_seconds,
                peak_memory_mb, final_memory_mb, gc_gen0, gc_gen1, gc_gen2,
                total_requests, total_intercepts, dotnet_version, os_description, process_id
                FROM runtime_logs ORDER BY start_time DESC LIMIT $1 OFFSET $2";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(limit));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(offset));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new RuntimeLogEntry
                {
                    Id = reader.GetInt64(0),
                    StartTime = reader.GetDateTime(1),
                    StopTime = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    ExitReason = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    DurationSeconds = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    PeakMemoryMb = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                    FinalMemoryMb = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                    GcGen0 = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                    GcGen1 = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                    GcGen2 = reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                    TotalRequests = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
                    TotalIntercepts = reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
                    DotnetVersion = reader.IsDBNull(12) ? "" : reader.GetString(12),
                    OsDescription = reader.IsDBNull(13) ? "" : reader.GetString(13),
                    ProcessId = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查询运行日志失败");
        }

        return (items, total);
    }

    /// <summary>
    /// 标记之前未正常关闭的记录（stop_time 为 NULL 的）
    /// </summary>
    private static void MarkAbnormalExits(DuckDB.NET.Data.DuckDBConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE runtime_logs SET
                exit_reason = '异常退出',
                stop_time = start_time,
                duration_seconds = 0
                WHERE stop_time IS NULL";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            var logger = LogManager.GetCurrentClassLogger();
            logger.Warn(ex, "标记异常退出记录失败");
        }
    }
}

/// <summary>
/// 运行日志条目
/// </summary>
public class RuntimeLogEntry
{
    public long Id { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? StopTime { get; set; }
    public string ExitReason { get; set; } = "";
    public long DurationSeconds { get; set; }
    public double PeakMemoryMb { get; set; }
    public double FinalMemoryMb { get; set; }
    public long GcGen0 { get; set; }
    public long GcGen1 { get; set; }
    public long GcGen2 { get; set; }
    public long TotalRequests { get; set; }
    public long TotalIntercepts { get; set; }
    public string DotnetVersion { get; set; } = "";
    public string OsDescription { get; set; } = "";
    public int ProcessId { get; set; }
}
