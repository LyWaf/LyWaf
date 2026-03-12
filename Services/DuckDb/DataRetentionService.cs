using Microsoft.Extensions.Options;
using NLog;

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

    public DataRetentionService(IDuckDbService db, IOptions<DuckDbOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_db.IsEnabled) return;

        // 启动后等待一段时间再开始清理
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunRetention();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "数据保留策略执行失败");
            }

            // 每天执行一次
            try
            {
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RunRetention()
    {
        var detailCutoff = DateTime.UtcNow.AddDays(-_options.DetailRetentionDays);
        var aggregateCutoff = DateTime.UtcNow.AddDays(-_options.AggregateRetentionDays);

        _logger.Info("开始数据清理: 详细数据保留 {}天, 聚合数据保留 {}天",
            _options.DetailRetentionDays, _options.AggregateRetentionDays);

        var conn = _db.GetConnection();

        // 清理详细数据（保留 DetailRetentionDays）
        ExecuteDelete(conn, "DELETE FROM traffic_snapshots WHERE snapshot_time < $1", detailCutoff, "traffic_snapshots");
        ExecuteDelete(conn, "DELETE FROM qps_snapshots WHERE snapshot_time < $1", detailCutoff, "qps_snapshots");
        ExecuteDelete(conn, "DELETE FROM bandwidth_snapshots WHERE snapshot_time < $1", detailCutoff, "bandwidth_snapshots");
        ExecuteDelete(conn, "DELETE FROM api_timing_snapshots WHERE snapshot_time < $1", detailCutoff, "api_timing_snapshots");
        ExecuteDelete(conn, "DELETE FROM endpoint_stats_snapshots WHERE snapshot_time < $1", detailCutoff, "endpoint_stats_snapshots");

        // 清理安全事件时间片（保留 AggregateRetentionDays）
        ExecuteDelete(conn, "DELETE FROM security_timeslots WHERE slot_time < $1", aggregateCutoff, "security_timeslots");

        // 清理拦截事件（保留 DetailRetentionDays * 3）
        var interceptCutoff = DateTime.UtcNow.AddDays(-_options.DetailRetentionDays * 3);
        ExecuteDelete(conn, "DELETE FROM intercept_events WHERE last_hit_time < $1", interceptCutoff, "intercept_events");

        // 清理攻击源（保留 AggregateRetentionDays）
        ExecuteDelete(conn, "DELETE FROM security_attack_sources WHERE last_attack_time < $1", aggregateCutoff, "security_attack_sources");

        _logger.Info("数据清理完成");
    }

    private static void ExecuteDelete(DuckDB.NET.Data.DuckDBConnection conn, string sql, DateTime cutoff, string tableName)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(cutoff));
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
                _logger.Info("清理 {}: 删除 {} 行", tableName, deleted);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "清理 {} 失败", tableName);
        }
    }
}
