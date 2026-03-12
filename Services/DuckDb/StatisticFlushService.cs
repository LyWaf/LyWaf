using LyWaf.Shared;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NLog;

namespace LyWaf.Services.DuckDb;

/// <summary>
/// 后台刷写服务：定期将内存统计数据持久化到 DuckDB
/// </summary>
public class StatisticFlushService : BackgroundService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IDuckDbService _db;
    private readonly DuckDbOptions _options;

    // 跟踪上次刷写的安全事件时间片时间，避免重复写入
    private DateTime _lastFlushedSlotTime = DateTime.MinValue;

    // FinalFlush 完成后置为 true，阻止后续循环再次刷写
    private volatile bool _finalFlushed;

    public StatisticFlushService(IDuckDbService db, IOptions<DuckDbOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_db.IsEnabled) return;

        // 初始化 Schema
        await _db.InitializeSchemaAsync();

        // 等待应用完全启动
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_finalFlushed) break;
                FlushAll();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "DuckDB 刷写周期失败");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.FlushIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 关机时的最终刷写（由 Program.cs 调用）
    /// </summary>
    public void FinalFlush()
    {
        if (!_db.IsEnabled) return;
        try
        {
            _logger.Info("执行 DuckDB 最终刷写...");
            FlushAll();
            _logger.Info("DuckDB 最终刷写完成");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DuckDB 最终刷写失败");
        }
        finally
        {
            _finalFlushed = true;
        }
    }

    private void FlushAll()
    {
        FlushTrafficSnapshot();
        FlushQpsSnapshot();
        FlushBandwidthSnapshot();
        FlushSecurityCounters();
        FlushSecurityTimeSlots();
        FlushSecurityAttackSources();
        FlushGeoTraffic();
        FlushApiTimings();
        FlushEndpointStats();
        FlushInterceptEvents();
    }

    // ===================== Traffic =====================

    private void FlushTrafficSnapshot()
    {
        try
        {
            var snapshot = SharedData.Traffic.GetSnapshot();
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO traffic_snapshots
                (snapshot_time, total_requests, page_views, unique_visitors, unique_ips,
                 intercept_count, attack_ips, error_4xx, intercept_4xx, error_5xx)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(DateTime.UtcNow));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.TotalRequests));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.PageViews));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.UniqueVisitors));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.UniqueIps));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.InterceptCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.AttackIps));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.Error4xxCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.Intercept4xxCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.Error5xxCount));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 traffic_snapshots 失败");
        }
    }

    // ===================== QPS =====================

    private void FlushQpsSnapshot()
    {
        try
        {
            var count = SharedData.Qps.FlushCount();
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO qps_snapshots (snapshot_time, request_count) VALUES ($1, $2)";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(DateTime.UtcNow));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(count));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 qps_snapshots 失败");
        }
    }

    // ===================== Bandwidth =====================

    private void FlushBandwidthSnapshot()
    {
        try
        {
            var (inbound, outbound) = SharedData.Bandwidth.FlushCount();
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO bandwidth_snapshots (snapshot_time, inbound_bytes, outbound_bytes) VALUES ($1, $2, $3)";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(DateTime.UtcNow));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(inbound));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(outbound));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 bandwidth_snapshots 失败");
        }
    }

    // ===================== Security =====================

    private void FlushSecurityCounters()
    {
        try
        {
            var snapshot = SharedData.Security.GetSnapshot();
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            // DuckDB INSERT OR REPLACE 语法
            cmd.CommandText = @"INSERT OR REPLACE INTO security_counters
                (id, start_time, waf_intercept_count, blacklist_block_count, cc_attack_count,
                 crawler_detect_count, geo_block_count, rate_limit_count)
                VALUES (1, $1, $2, $3, $4, $5, $6, $7)";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.StartTime));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.WafInterceptCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.BlacklistBlockCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.CcAttackCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.CrawlerDetectCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.GeoBlockCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(snapshot.RateLimitCount));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 security_counters 失败");
        }
    }

    private void FlushSecurityTimeSlots()
    {
        try
        {
            var slots = SharedData.Security.GetTimeSlots(24);
            if (slots.Count == 0) return;

            var conn = _db.GetConnection();
            foreach (var slot in slots)
            {
                // 只写入比上次更新的时间片
                if (slot.Time <= _lastFlushedSlotTime) continue;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR REPLACE INTO security_timeslots
                    (slot_time, waf_intercept, blacklist_block, cc_attack, crawler_detect, geo_block, rate_limit)
                    VALUES ($1, $2, $3, $4, $5, $6, $7)";
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(slot.Time));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(slot.WafIntercept));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(slot.BlacklistBlock));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(slot.CcAttack));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(slot.CrawlerDetect));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(slot.GeoBlock));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(slot.RateLimit));
                cmd.ExecuteNonQuery();
            }

            // 记录最后刷写的时间片（当前时间片可能还在更新中，用倒数第二个）
            if (slots.Count >= 2)
                _lastFlushedSlotTime = slots[^2].Time;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 security_timeslots 失败");
        }
    }

    private void FlushSecurityAttackSources()
    {
        try
        {
            var typedSources = SharedData.Security.GetAllTypedAttackSources(200);
            if (typedSources.Count == 0) return;

            var conn = _db.GetConnection();

            // 清空旧数据，重新写入
            using (var delCmd = conn.CreateCommand())
            {
                delCmd.CommandText = "DELETE FROM security_attack_sources";
                delCmd.ExecuteNonQuery();
            }

            foreach (var (eventType, ip, count) in typedSources)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO security_attack_sources
                    (ip, event_type, hit_count, last_attack_time)
                    VALUES ($1, $2, $3, $4)";
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(ip));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter((int)eventType));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(count));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(DateTime.UtcNow));
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 security_attack_sources 失败");
        }
    }

    // ===================== GeoTraffic =====================

    private void FlushGeoTraffic()
    {
        try
        {
            var snapshot = SharedData.GeoTraffic.GetSnapshot();
            var conn = _db.GetConnection();

            UpsertGeoEntries(conn, "country", snapshot.CountryVisits, snapshot.CountryIntercepts);
            UpsertGeoEntries(conn, "region", snapshot.RegionVisits, snapshot.RegionIntercepts);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 geo_traffic 失败");
        }
    }

    private static void UpsertGeoEntries(DuckDB.NET.Data.DuckDBConnection conn, string regionType,
        Dictionary<string, long> visits, Dictionary<string, long> intercepts)
    {
        // 合并所有 region name
        var allNames = new HashSet<string>(visits.Keys);
        foreach (var k in intercepts.Keys) allNames.Add(k);

        foreach (var name in allNames)
        {
            visits.TryGetValue(name, out var visitCount);
            intercepts.TryGetValue(name, out var interceptCount);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO geo_traffic
                (region_type, region_name, visit_count, intercept_count, last_updated)
                VALUES ($1, $2, $3, $4, $5)";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(regionType));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(name));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(visitCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(interceptCount));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        }
    }

    // ===================== API Timing =====================

    private void FlushApiTimings()
    {
        try
        {
            var snapshot = SharedData.ApiTimings.GetSnapshot();
            if (snapshot.Count == 0) return;

            var conn = _db.GetConnection();
            var now = DateTime.UtcNow;

            foreach (var (key, timing) in snapshot)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR REPLACE INTO api_timing_snapshots
                    (snapshot_time, path, method, backend, original_host, request_count,
                     total_time, backend_time, min_total_time, max_total_time,
                     min_backend_time, max_backend_time, status_codes)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)";
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(now));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.Path));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.Method));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.Backend));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.OriginalHost));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.RequestCount));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.TotalTime));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.BackendTime));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.MinTotalTime == long.MaxValue ? 0 : timing.MinTotalTime));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.MaxTotalTime));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.MinBackendTime == long.MaxValue ? 0 : timing.MinBackendTime));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(timing.MaxBackendTime));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(JsonConvert.SerializeObject(timing.StatusCodeCounts)));
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 api_timing_snapshots 失败");
        }
    }

    // ===================== Endpoint Stats =====================

    private void FlushEndpointStats()
    {
        try
        {
            var now = DateTime.UtcNow;
            var conn = _db.GetConnection();

            FlushEndpointStatsDict(conn, now, "dest", SharedData.DestStas.GetSnapshot());
            FlushEndpointStatsDict(conn, now, "req", SharedData.ReqStas.GetSnapshot());
            FlushEndpointStatsDict(conn, now, "client", SharedData.ClientStas.GetSnapshot());
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 endpoint_stats_snapshots 失败");
        }
    }

    private static void FlushEndpointStatsDict(DuckDB.NET.Data.DuckDBConnection conn, DateTime now,
        string statType, Dictionary<string, IpStatistic> snapshot)
    {
        foreach (var (key, stat) in snapshot)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO endpoint_stats_snapshots
                (snapshot_time, stat_type, key, total_count, total_time, avg_time, last_access_time)
                VALUES ($1, $2, $3, $4, $5, $6, $7)";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(now));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(statType));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(key));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(stat.CountTime.Count));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(stat.CountTime.UseTime));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(stat.CountTime.Average));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(stat.LastAccessTime));
            cmd.ExecuteNonQuery();
        }
    }

    // ===================== 拦截事件 =====================

    private void FlushInterceptEvents()
    {
        try
        {
            var events = SharedData.GetInterceptEvents();
            if (events.Count == 0) return;

            var conn = _db.GetConnection();
            foreach (var evt in events)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR REPLACE INTO intercept_events
                    (source_ip, application, region, city, rule_name, rule_type,
                     hit_count, first_hit_time, last_hit_time)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)";
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(evt.SourceIp));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(evt.Application));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(evt.Region));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(evt.City));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(evt.RuleName));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(evt.RuleType));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(evt.HitCount));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(evt.FirstHitTime));
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(evt.LastHitTime));
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "刷写 intercept_events 失败");
        }
    }
}
