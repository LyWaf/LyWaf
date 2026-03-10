using LyWaf.Shared;
using Newtonsoft.Json;
using NLog;

namespace LyWaf.Services.DuckDb;

/// <summary>
/// 启动恢复服务：从 DuckDB 恢复上次运行的统计数据到内存
/// 在 StartAsync 中阻塞执行，确保数据在第一个请求前恢复
/// </summary>
public class StatisticRecoveryService : IHostedService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IDuckDbService _db;

    public StatisticRecoveryService(IDuckDbService db)
    {
        _db = db;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_db.IsEnabled) return;

        try
        {
            // 先初始化 Schema（确保表存在）
            await _db.InitializeSchemaAsync();

            RecoverTraffic();
            RecoverSecurityCounters();
            RecoverSecurityTimeSlots();
            RecoverSecurityAttackSources();
            RecoverGeoTraffic();
            RecoverApiTimings();
            RecoverEndpointStats();
            RecoverInterceptEvents();
            _logger.Info("DuckDB 数据恢复完成");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DuckDB 数据恢复失败，将以空数据启动");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RecoverTraffic()
    {
        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT snapshot_time, total_requests, page_views, unique_visitors, unique_ips,
                intercept_count, attack_ips, error_4xx, intercept_4xx, error_5xx
                FROM traffic_snapshots ORDER BY snapshot_time DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var startTime = reader.GetDateTime(0);
                var totalRequests = reader.GetInt64(1);
                var pageViews = reader.GetInt64(2);
                var uniqueVisitors = reader.GetInt32(3);
                var uniqueIps = reader.GetInt32(4);
                var interceptCount = reader.GetInt64(5);
                var attackIps = reader.GetInt32(6);
                var error4xx = reader.GetInt64(7);
                var intercept4xx = reader.GetInt64(8);
                var error5xx = reader.GetInt64(9);

                SharedData.Traffic.RestoreFrom(totalRequests, pageViews, uniqueVisitors, uniqueIps,
                    interceptCount, attackIps, error4xx, intercept4xx, error5xx, startTime);
                _logger.Info("流量统计已恢复: 总请求={}, PV={}, UV={}, IP={}", totalRequests, pageViews, uniqueVisitors, uniqueIps);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "恢复流量统计失败");
        }
    }

    private void RecoverSecurityCounters()
    {
        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT start_time, waf_intercept_count, blacklist_block_count, cc_attack_count,
                crawler_detect_count, geo_block_count, rate_limit_count
                FROM security_counters WHERE id = 1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                SharedData.Security.RestoreCounters(
                    reader.GetDateTime(0),
                    reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
                _logger.Info("安全事件计数已恢复");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "恢复安全事件计数失败");
        }
    }

    private void RecoverSecurityTimeSlots()
    {
        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            // 恢复最近24小时的时间片
            cmd.CommandText = @"SELECT slot_time, waf_intercept, blacklist_block, cc_attack,
                crawler_detect, geo_block, rate_limit
                FROM security_timeslots
                WHERE slot_time >= $1
                ORDER BY slot_time ASC";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(DateTime.UtcNow.AddHours(-24)));
            using var reader = cmd.ExecuteReader();

            var slots = new List<SecurityTimeSlot>();
            while (reader.Read())
            {
                slots.Add(new SecurityTimeSlot
                {
                    Time = reader.GetDateTime(0),
                    WafIntercept = reader.GetInt64(1),
                    BlacklistBlock = reader.GetInt64(2),
                    CcAttack = reader.GetInt64(3),
                    CrawlerDetect = reader.GetInt64(4),
                    GeoBlock = reader.GetInt64(5),
                    RateLimit = reader.GetInt64(6),
                });
            }

            if (slots.Count > 0)
            {
                SharedData.Security.RestoreTimeSlots(slots);
                _logger.Info("安全事件时间片已恢复: {} 个", slots.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "恢复安全事件时间片失败");
        }
    }

    private void RecoverSecurityAttackSources()
    {
        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ip, event_type, hit_count, last_attack_time
                FROM security_attack_sources ORDER BY hit_count DESC LIMIT 1000";
            using var reader = cmd.ExecuteReader();

            var sources = new List<AttackSourceStat>();
            while (reader.Read())
            {
                sources.Add(new AttackSourceStat
                {
                    Ip = reader.GetString(0),
                    LastEventType = (SecurityEventType)reader.GetInt32(1),
                    Count = reader.GetInt64(2),
                    LastAttackTime = reader.GetDateTime(3),
                });
            }

            if (sources.Count > 0)
            {
                SharedData.Security.RestoreAttackSources(sources);
                _logger.Info("攻击源统计已恢复: {} 个IP", sources.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "恢复攻击源统计失败");
        }
    }

    private void RecoverGeoTraffic()
    {
        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT region_type, region_name, visit_count, intercept_count FROM geo_traffic";
            using var reader = cmd.ExecuteReader();

            var snapshot = new GeoTrafficSnapshot
            {
                CountryVisits = new Dictionary<string, long>(),
                CountryIntercepts = new Dictionary<string, long>(),
                RegionVisits = new Dictionary<string, long>(),
                RegionIntercepts = new Dictionary<string, long>(),
            };

            while (reader.Read())
            {
                var regionType = reader.GetString(0);
                var regionName = reader.GetString(1);
                var visitCount = reader.GetInt64(2);
                var interceptCount = reader.GetInt64(3);

                if (regionType == "country")
                {
                    snapshot.CountryVisits[regionName] = visitCount;
                    if (interceptCount > 0)
                        snapshot.CountryIntercepts[regionName] = interceptCount;
                }
                else if (regionType == "region")
                {
                    snapshot.RegionVisits[regionName] = visitCount;
                    if (interceptCount > 0)
                        snapshot.RegionIntercepts[regionName] = interceptCount;
                }
            }

            var total = snapshot.CountryVisits.Count + snapshot.RegionVisits.Count;
            if (total > 0)
            {
                SharedData.GeoTraffic.RestoreFrom(snapshot);
                _logger.Info("地理位置流量已恢复: {} 个国家, {} 个地区",
                    snapshot.CountryVisits.Count, snapshot.RegionVisits.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "恢复地理位置流量失败");
        }
    }

    private void RecoverApiTimings()
    {
        try
        {
            var conn = _db.GetConnection();
            // 取最近一次刷写批次（同一个 snapshot_time）的所有记录
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT path, method, backend, original_host,
                request_count, total_time, backend_time, min_total_time, max_total_time,
                min_backend_time, max_backend_time, status_codes
                FROM api_timing_snapshots
                WHERE snapshot_time = (SELECT MAX(snapshot_time) FROM api_timing_snapshots)";
            using var reader = cmd.ExecuteReader();

            int count = 0;
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var method = reader.GetString(1);
                var backend = reader.GetString(2);
                var originalHost = reader.GetString(3);

                var timing = new ApiTimingStatistic
                {
                    Path = path,
                    Method = method,
                    Backend = backend,
                    OriginalHost = originalHost,
                    RequestCount = reader.GetInt32(4),
                    TotalTime = reader.GetInt64(5),
                    BackendTime = reader.GetInt64(6),
                    MinTotalTime = reader.GetInt64(7),
                    MaxTotalTime = reader.GetInt64(8),
                    MinBackendTime = reader.GetInt64(9),
                    MaxBackendTime = reader.GetInt64(10),
                };

                // MinTotalTime 为 0 时恢复为 long.MaxValue（初始值）
                if (timing.MinTotalTime == 0 && timing.RequestCount == 0)
                    timing.MinTotalTime = long.MaxValue;
                if (timing.MinBackendTime == 0 && timing.RequestCount == 0)
                    timing.MinBackendTime = long.MaxValue;

                var statusCodesJson = reader.GetString(11);
                if (!string.IsNullOrEmpty(statusCodesJson) && statusCodesJson != "{}")
                {
                    timing.StatusCodeCounts = JsonConvert.DeserializeObject<Dictionary<int, int>>(statusCodesJson) ?? [];
                }

                // 构建 key（与 StatisticUtil.RecordApiTiming 一致）
                var key = string.IsNullOrEmpty(backend)
                    ? $"{method}:{path}"
                    : $"{method}:{path}@{backend}";

                SharedData.ApiTimings.AddOrUpdate(key, timing);
                count++;
            }

            if (count > 0)
                _logger.Info("API 耗时统计已恢复: {} 个端点", count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "恢复 API 耗时统计失败");
        }
    }

    private void RecoverEndpointStats()
    {
        try
        {
            var conn = _db.GetConnection();
            // 对每个 stat_type 取最近一次批次
            foreach (var statType in new[] { "dest", "req", "client" })
            {
                var dict = statType switch
                {
                    "dest" => SharedData.DestStas,
                    "req" => SharedData.ReqStas,
                    "client" => SharedData.ClientStas,
                    _ => null
                };
                if (dict == null) continue;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT key, total_count, total_time, avg_time, last_access_time
                    FROM endpoint_stats_snapshots
                    WHERE stat_type = $1
                      AND snapshot_time = (
                          SELECT MAX(snapshot_time) FROM endpoint_stats_snapshots WHERE stat_type = $1
                      )";
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(statType));
                using var reader = cmd.ExecuteReader();

                int count = 0;
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var stat = new IpStatistic
                    {
                        CountTime = new StaCountTime
                        {
                            Count = reader.GetInt32(1),
                            UseTime = reader.GetInt64(2),
                        },
                        LastAccessTime = reader.GetInt64(4),
                    };
                    dict.AddOrUpdate(key, stat);
                    count++;
                }

                if (count > 0)
                    _logger.Info("{} 统计已恢复: {} 个条目", statType, count);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "恢复端点统计失败");
        }
    }

    private void RecoverInterceptEvents()
    {
        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT source_ip, application, region, city, rule_name, rule_type,
                hit_count, first_hit_time, last_hit_time
                FROM intercept_events ORDER BY last_hit_time DESC LIMIT 10000";
            using var reader = cmd.ExecuteReader();

            int count = 0;
            while (reader.Read())
            {
                SharedData.RecordInterceptEvent(
                    reader.GetString(0),  // sourceIp
                    reader.GetString(1),  // application
                    reader.GetString(2),  // region
                    reader.GetString(3),  // city
                    reader.GetString(4),  // ruleName
                    reader.GetString(5)   // ruleType
                );
                count++;
            }

            if (count > 0)
                _logger.Info("拦截事件已恢复: {} 个", count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "恢复拦截事件失败");
        }
    }
}
