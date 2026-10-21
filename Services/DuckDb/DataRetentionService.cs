using Microsoft.Extensions.Options;
using NLog;
using LyWaf.Services.Metrics;

namespace LyWaf.Services.DuckDb;

/// <summary>
/// 数据保留策略服务
/// 定期清理过期的详细数据和聚合数据
/// </summary>
public class DataRetentionService : BackgroundService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IDuckDbService _db;
    private readonly DuckDbOptions _options;
    private readonly PrometheusMetrics _metrics;

    public DataRetentionService(IDuckDbService db, IOptions<DuckDbOptions> options, PrometheusMetrics metrics)
    {
        _db = db;
        _options = options.Value;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_db.IsEnabled) return;

        // 启动后等待一段时间再开始清理
        if (!await LyWaf.Services.Lifecycle.ShutdownWait.DelayAsync(TimeSpan.FromMinutes(5), stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                RunRetention();
            }
            catch (Exception ex)
            {
                _metrics.ObserveDuckDbOperation("retention", false, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
                _logger.Error(ex, "数据保留策略执行失败");
            }

            // 每天执行一次
            await LyWaf.Services.Lifecycle.ShutdownWait.DelayAsync(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private void RunRetention()
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var success = true;
        var detailCutoff = DateTime.UtcNow.AddDays(-_options.DetailRetentionDays);
        var aggregateCutoff = DateTime.UtcNow.AddDays(-_options.AggregateRetentionDays);

        _logger.Info("开始数据清理: 详细数据保留 {}天, 聚合数据保留 {}天",
            _options.DetailRetentionDays, _options.AggregateRetentionDays);

        var conn = _db.GetConnection();

        // 清理详细数据（保留 DetailRetentionDays）
        success &= ExecuteDelete(conn, "DELETE FROM traffic_snapshots WHERE snapshot_time < $1", detailCutoff, "traffic_snapshots");
        success &= ExecuteDelete(conn, "DELETE FROM qps_snapshots WHERE snapshot_time < $1", detailCutoff, "qps_snapshots");
        success &= ExecuteDelete(conn, "DELETE FROM bandwidth_snapshots WHERE snapshot_time < $1", detailCutoff, "bandwidth_snapshots");
        success &= ExecuteDelete(conn, "DELETE FROM api_timing_snapshots WHERE snapshot_time < $1", detailCutoff, "api_timing_snapshots");
        success &= ExecuteDelete(conn, "DELETE FROM endpoint_stats_snapshots WHERE snapshot_time < $1", detailCutoff, "endpoint_stats_snapshots");

        // 清理安全事件时间片（保留 AggregateRetentionDays）
        success &= ExecuteDelete(conn, "DELETE FROM security_timeslots WHERE slot_time < $1", aggregateCutoff, "security_timeslots");

        // 清理拦截事件（保留 DetailRetentionDays * 3）
        var interceptCutoff = DateTime.UtcNow.AddDays(-_options.DetailRetentionDays * 3);
        success &= ExecuteDelete(conn, "DELETE FROM intercept_events WHERE last_hit_time < $1", interceptCutoff, "intercept_events");

        // 清理攻击源（保留 AggregateRetentionDays）
        success &= ExecuteDelete(conn, "DELETE FROM security_attack_sources WHERE last_attack_time < $1", aggregateCutoff, "security_attack_sources");

        _logger.Info("数据清理完成");
        _metrics.ObserveDuckDbOperation("retention", success, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
    }

    private bool ExecuteDelete(DuckDB.NET.Data.DuckDBConnection conn, string sql, DateTime cutoff, string tableName)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(cutoff));
            var deleted = cmd.ExecuteNonQuery();
            _metrics.RecordDuckDbRetentionRows(tableName, deleted);
            if (deleted > 0)
                _logger.Info("清理 {}: 删除 {} 行", tableName, deleted);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "清理 {} 失败", tableName);
            return false;
        }
    }
}
