using LyWaf.Shared;
using Newtonsoft.Json;
using NLog;

namespace LyWaf.Services.DuckDb;

/// <summary>
/// DuckDB 历史查询服务
/// </summary>
public interface IDuckDbQueryService
{
    bool IsEnabled { get; }
    List<TrafficSnapshotRow> GetTrafficHistory(DateTime from, DateTime to, string granularity = "hour");
    List<SecurityTimeSlot> GetSecurityTimeSlots(DateTime from, DateTime to);
    List<ApiTimingRow> GetApiTimingHistory(DateTime from, DateTime to, string? pathFilter = null);
    GeoTrafficSnapshot GetGeoTrafficHistory();
    (List<InterceptEventRow> Items, int Total) GetInterceptEventsPaged(string? ipFilter, string? domainFilter, DateTime? start, DateTime? end, int offset, int limit);
    (List<AuditLogRow> Items, int Total) GetAuditLogsPaged(int offset, int limit);
    List<EndpointStatRow> GetEndpointStatsHistory(string statType, DateTime from, DateTime to, int topN = 50);
}

public class TrafficSnapshotRow
{
    public DateTime SnapshotTime { get; set; }
    public long TotalRequests { get; set; }
    public long PageViews { get; set; }
    public int UniqueVisitors { get; set; }
    public int UniqueIps { get; set; }
    public long InterceptCount { get; set; }
    public int AttackIps { get; set; }
    public long Error4xx { get; set; }
    public long Error5xx { get; set; }
}

public class ApiTimingRow
{
    public DateTime SnapshotTime { get; set; }
    public string Path { get; set; } = "";
    public string Method { get; set; } = "";
    public string Backend { get; set; } = "";
    public string OriginalHost { get; set; } = "";
    public int RequestCount { get; set; }
    public long TotalTime { get; set; }
    public long BackendTime { get; set; }
    public long MinTotalTime { get; set; }
    public long MaxTotalTime { get; set; }
    public long MinBackendTime { get; set; }
    public long MaxBackendTime { get; set; }
    public Dictionary<int, int> StatusCodeCounts { get; set; } = [];
}

public class InterceptEventRow
{
    public string SourceIp { get; set; } = "";
    public string Application { get; set; } = "";
    public string Region { get; set; } = "";
    public string City { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string RuleType { get; set; } = "";
    public long HitCount { get; set; }
    public DateTime FirstHitTime { get; set; }
    public DateTime LastHitTime { get; set; }
}

public class AuditLogRow
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Ip { get; set; } = "";
    public DateTime LogTime { get; set; }
}

public class EndpointStatRow
{
    public DateTime SnapshotTime { get; set; }
    public string StatType { get; set; } = "";
    public string Key { get; set; } = "";
    public int TotalCount { get; set; }
    public long TotalTime { get; set; }
    public double AvgTime { get; set; }
    public long LastAccessTime { get; set; }
}

public class DuckDbQueryService : IDuckDbQueryService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IDuckDbService _db;

    public bool IsEnabled => _db.IsEnabled;

    public DuckDbQueryService(IDuckDbService db)
    {
        _db = db;
    }

    public List<TrafficSnapshotRow> GetTrafficHistory(DateTime from, DateTime to, string granularity = "hour")
    {
        var result = new List<TrafficSnapshotRow>();
        if (!_db.IsEnabled) return result;

        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            // 按 granularity 聚合
            var truncUnit = granularity == "day" ? "day" : "hour";
            cmd.CommandText = $@"SELECT date_trunc('{truncUnit}', snapshot_time) as t,
                MAX(total_requests) as total_requests, MAX(page_views) as page_views,
                MAX(unique_visitors) as unique_visitors, MAX(unique_ips) as unique_ips,
                MAX(intercept_count) as intercept_count, MAX(attack_ips) as attack_ips,
                MAX(error_4xx) as error_4xx, MAX(error_5xx) as error_5xx
                FROM traffic_snapshots
                WHERE snapshot_time >= $1 AND snapshot_time <= $2
                GROUP BY t ORDER BY t ASC";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(from));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(to));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new TrafficSnapshotRow
                {
                    SnapshotTime = reader.GetDateTime(0),
                    TotalRequests = reader.GetInt64(1),
                    PageViews = reader.GetInt64(2),
                    UniqueVisitors = reader.GetInt32(3),
                    UniqueIps = reader.GetInt32(4),
                    InterceptCount = reader.GetInt64(5),
                    AttackIps = reader.GetInt32(6),
                    Error4xx = reader.GetInt64(7),
                    Error5xx = reader.GetInt64(8),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查询流量历史失败");
        }
        return result;
    }

    public List<SecurityTimeSlot> GetSecurityTimeSlots(DateTime from, DateTime to)
    {
        var result = new List<SecurityTimeSlot>();
        if (!_db.IsEnabled) return result;

        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT slot_time, waf_intercept, blacklist_block, cc_attack,
                crawler_detect, geo_block, rate_limit
                FROM security_timeslots
                WHERE slot_time >= $1 AND slot_time <= $2
                ORDER BY slot_time ASC";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(from));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(to));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new SecurityTimeSlot
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
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查询安全事件时间片失败");
        }
        return result;
    }

    public List<ApiTimingRow> GetApiTimingHistory(DateTime from, DateTime to, string? pathFilter = null)
    {
        var result = new List<ApiTimingRow>();
        if (!_db.IsEnabled) return result;

        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            var whereClause = "WHERE snapshot_time >= $1 AND snapshot_time <= $2";
            if (!string.IsNullOrEmpty(pathFilter))
                whereClause += " AND path LIKE $3";

            cmd.CommandText = $@"SELECT snapshot_time, path, method, backend, original_host,
                request_count, total_time, backend_time, min_total_time, max_total_time,
                min_backend_time, max_backend_time, status_codes
                FROM api_timing_snapshots
                {whereClause}
                ORDER BY snapshot_time DESC LIMIT 1000";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(from));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(to));
            if (!string.IsNullOrEmpty(pathFilter))
                cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter($"%{pathFilter}%"));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var statusCodesJson = reader.GetString(12);
                var statusCodes = string.IsNullOrEmpty(statusCodesJson) || statusCodesJson == "{}"
                    ? new Dictionary<int, int>()
                    : JsonConvert.DeserializeObject<Dictionary<int, int>>(statusCodesJson) ?? [];

                result.Add(new ApiTimingRow
                {
                    SnapshotTime = reader.GetDateTime(0),
                    Path = reader.GetString(1),
                    Method = reader.GetString(2),
                    Backend = reader.GetString(3),
                    OriginalHost = reader.GetString(4),
                    RequestCount = reader.GetInt32(5),
                    TotalTime = reader.GetInt64(6),
                    BackendTime = reader.GetInt64(7),
                    MinTotalTime = reader.GetInt64(8),
                    MaxTotalTime = reader.GetInt64(9),
                    MinBackendTime = reader.GetInt64(10),
                    MaxBackendTime = reader.GetInt64(11),
                    StatusCodeCounts = statusCodes,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查询API耗时历史失败");
        }
        return result;
    }

    public GeoTrafficSnapshot GetGeoTrafficHistory()
    {
        var snapshot = new GeoTrafficSnapshot
        {
            CountryVisits = new Dictionary<string, long>(),
            CountryIntercepts = new Dictionary<string, long>(),
            RegionVisits = new Dictionary<string, long>(),
            RegionIntercepts = new Dictionary<string, long>(),
        };
        if (!_db.IsEnabled) return snapshot;

        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT region_type, region_name, visit_count, intercept_count FROM geo_traffic";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var regionType = reader.GetString(0);
                var regionName = reader.GetString(1);
                var visits = reader.GetInt64(2);
                var intercepts = reader.GetInt64(3);

                if (regionType == "country")
                {
                    snapshot.CountryVisits[regionName] = visits;
                    if (intercepts > 0) snapshot.CountryIntercepts[regionName] = intercepts;
                }
                else
                {
                    snapshot.RegionVisits[regionName] = visits;
                    if (intercepts > 0) snapshot.RegionIntercepts[regionName] = intercepts;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查询地理流量历史失败");
        }
        return snapshot;
    }

    public (List<InterceptEventRow> Items, int Total) GetInterceptEventsPaged(
        string? ipFilter, string? domainFilter, DateTime? start, DateTime? end, int offset, int limit)
    {
        var items = new List<InterceptEventRow>();
        int total = 0;
        if (!_db.IsEnabled) return (items, total);

        try
        {
            var conn = _db.GetConnection();
            var conditions = new List<string>();
            var parameters = new List<DuckDB.NET.Data.DuckDBParameter>();
            int paramIdx = 1;

            if (!string.IsNullOrEmpty(ipFilter))
            {
                conditions.Add($"source_ip LIKE ${paramIdx++}");
                parameters.Add(new DuckDB.NET.Data.DuckDBParameter($"%{ipFilter}%"));
            }
            if (!string.IsNullOrEmpty(domainFilter))
            {
                conditions.Add($"application LIKE ${paramIdx++}");
                parameters.Add(new DuckDB.NET.Data.DuckDBParameter($"%{domainFilter}%"));
            }
            if (start.HasValue)
            {
                conditions.Add($"last_hit_time >= ${paramIdx++}");
                parameters.Add(new DuckDB.NET.Data.DuckDBParameter(start.Value));
            }
            if (end.HasValue)
            {
                conditions.Add($"last_hit_time <= ${paramIdx++}");
                parameters.Add(new DuckDB.NET.Data.DuckDBParameter(end.Value));
            }

            var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

            // Count
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM intercept_events {whereClause}";
                foreach (var p in parameters) countCmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(p.Value));
                total = Convert.ToInt32(countCmd.ExecuteScalar());
            }

            // Data
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT source_ip, application, region, city, rule_name, rule_type,
                hit_count, first_hit_time, last_hit_time
                FROM intercept_events {whereClause}
                ORDER BY last_hit_time DESC
                LIMIT {limit} OFFSET {offset}";
            foreach (var p in parameters) cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(p.Value));

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new InterceptEventRow
                {
                    SourceIp = reader.GetString(0),
                    Application = reader.GetString(1),
                    Region = reader.GetString(2),
                    City = reader.GetString(3),
                    RuleName = reader.GetString(4),
                    RuleType = reader.GetString(5),
                    HitCount = reader.GetInt64(6),
                    FirstHitTime = reader.GetDateTime(7),
                    LastHitTime = reader.GetDateTime(8),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查询拦截事件失败");
        }
        return (items, total);
    }

    public (List<AuditLogRow> Items, int Total) GetAuditLogsPaged(int offset, int limit)
    {
        var items = new List<AuditLogRow>();
        int total = 0;
        if (!_db.IsEnabled) return (items, total);

        try
        {
            var conn = _db.GetConnection();

            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM audit_log";
                total = Convert.ToInt32(countCmd.ExecuteScalar());
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT id, username, action, detail, ip, log_time
                FROM audit_log ORDER BY log_time DESC LIMIT {limit} OFFSET {offset}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new AuditLogRow
                {
                    Id = reader.GetInt64(0),
                    Username = reader.GetString(1),
                    Action = reader.GetString(2),
                    Detail = reader.GetString(3),
                    Ip = reader.GetString(4),
                    LogTime = reader.GetDateTime(5),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查询审计日志失败");
        }
        return (items, total);
    }

    public List<EndpointStatRow> GetEndpointStatsHistory(string statType, DateTime from, DateTime to, int topN = 50)
    {
        var result = new List<EndpointStatRow>();
        if (!_db.IsEnabled) return result;

        try
        {
            var conn = _db.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT snapshot_time, stat_type, key, total_count, total_time, avg_time, last_access_time
                FROM endpoint_stats_snapshots
                WHERE stat_type = $1 AND snapshot_time >= $2 AND snapshot_time <= $3
                ORDER BY total_count DESC LIMIT {topN}";
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(statType));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(from));
            cmd.Parameters.Add(new DuckDB.NET.Data.DuckDBParameter(to));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new EndpointStatRow
                {
                    SnapshotTime = reader.GetDateTime(0),
                    StatType = reader.GetString(1),
                    Key = reader.GetString(2),
                    TotalCount = reader.GetInt32(3),
                    TotalTime = reader.GetInt64(4),
                    AvgTime = reader.GetDouble(5),
                    LastAccessTime = reader.GetInt64(6),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "查询端点统计历史失败");
        }
        return result;
    }
}
