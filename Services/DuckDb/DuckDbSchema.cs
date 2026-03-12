namespace LyWaf.Services.DuckDb;

/// <summary>
/// DuckDB 表结构定义
/// </summary>
public static class DuckDbSchema
{
    public static readonly string[] CreateTableStatements =
    [
        // 流量快照（每分钟一行）
        @"CREATE TABLE IF NOT EXISTS traffic_snapshots (
            snapshot_time TIMESTAMP NOT NULL,
            total_requests BIGINT NOT NULL DEFAULT 0,
            page_views BIGINT NOT NULL DEFAULT 0,
            unique_visitors INTEGER NOT NULL DEFAULT 0,
            unique_ips INTEGER NOT NULL DEFAULT 0,
            intercept_count BIGINT NOT NULL DEFAULT 0,
            attack_ips INTEGER NOT NULL DEFAULT 0,
            error_4xx BIGINT NOT NULL DEFAULT 0,
            intercept_4xx BIGINT NOT NULL DEFAULT 0,
            error_5xx BIGINT NOT NULL DEFAULT 0
        )",

        // 安全事件累计计数（单行）
        @"CREATE TABLE IF NOT EXISTS security_counters (
            id INTEGER PRIMARY KEY DEFAULT 1 CHECK (id = 1),
            start_time TIMESTAMP NOT NULL,
            waf_intercept_count BIGINT DEFAULT 0,
            blacklist_block_count BIGINT DEFAULT 0,
            cc_attack_count BIGINT DEFAULT 0,
            crawler_detect_count BIGINT DEFAULT 0,
            geo_block_count BIGINT DEFAULT 0,
            rate_limit_count BIGINT DEFAULT 0
        )",

        // 安全事件时间片（5分钟粒度）
        @"CREATE TABLE IF NOT EXISTS security_timeslots (
            slot_time TIMESTAMP PRIMARY KEY,
            waf_intercept BIGINT DEFAULT 0,
            blacklist_block BIGINT DEFAULT 0,
            cc_attack BIGINT DEFAULT 0,
            crawler_detect BIGINT DEFAULT 0,
            geo_block BIGINT DEFAULT 0,
            rate_limit BIGINT DEFAULT 0
        )",

        // 攻击源IP统计
        @"CREATE TABLE IF NOT EXISTS security_attack_sources (
            ip VARCHAR NOT NULL,
            event_type INTEGER NOT NULL,
            hit_count BIGINT DEFAULT 0,
            last_attack_time TIMESTAMP,
            PRIMARY KEY (ip, event_type)
        )",

        // 地理位置流量
        @"CREATE TABLE IF NOT EXISTS geo_traffic (
            region_type VARCHAR NOT NULL,
            region_name VARCHAR NOT NULL,
            visit_count BIGINT DEFAULT 0,
            intercept_count BIGINT DEFAULT 0,
            last_updated TIMESTAMP DEFAULT current_timestamp,
            PRIMARY KEY (region_type, region_name)
        )",

        // API 耗时快照
        @"CREATE TABLE IF NOT EXISTS api_timing_snapshots (
            snapshot_time TIMESTAMP NOT NULL,
            path VARCHAR NOT NULL,
            method VARCHAR NOT NULL,
            backend VARCHAR DEFAULT '',
            original_host VARCHAR DEFAULT '',
            request_count INTEGER DEFAULT 0,
            total_time BIGINT DEFAULT 0,
            backend_time BIGINT DEFAULT 0,
            min_total_time BIGINT DEFAULT 0,
            max_total_time BIGINT DEFAULT 0,
            min_backend_time BIGINT DEFAULT 0,
            max_backend_time BIGINT DEFAULT 0,
            status_codes VARCHAR DEFAULT '{}',
            PRIMARY KEY (snapshot_time, method, path, backend)
        )",

        // 后端/路径/客户端聚合快照
        @"CREATE TABLE IF NOT EXISTS endpoint_stats_snapshots (
            snapshot_time TIMESTAMP NOT NULL,
            stat_type VARCHAR NOT NULL,
            key VARCHAR NOT NULL,
            total_count INTEGER DEFAULT 0,
            total_time BIGINT DEFAULT 0,
            avg_time DOUBLE DEFAULT 0,
            last_access_time BIGINT DEFAULT 0,
            PRIMARY KEY (snapshot_time, stat_type, key)
        )",

        // 拦截事件
        @"CREATE TABLE IF NOT EXISTS intercept_events (
            source_ip VARCHAR NOT NULL,
            application VARCHAR NOT NULL,
            region VARCHAR DEFAULT '',
            city VARCHAR DEFAULT '',
            rule_name VARCHAR DEFAULT '',
            rule_type VARCHAR DEFAULT '',
            hit_count BIGINT DEFAULT 0,
            first_hit_time TIMESTAMP NOT NULL,
            last_hit_time TIMESTAMP NOT NULL,
            PRIMARY KEY (source_ip, application)
        )",

        // QPS 快照（每次 flush 写一行，记录该周期内的请求数）
        @"CREATE TABLE IF NOT EXISTS qps_snapshots (
            snapshot_time TIMESTAMP NOT NULL PRIMARY KEY,
            request_count BIGINT NOT NULL DEFAULT 0
        )",

        // 带宽快照（每次 flush 写一行，记录该周期内的入站/出站字节）
        @"CREATE TABLE IF NOT EXISTS bandwidth_snapshots (
            snapshot_time TIMESTAMP NOT NULL PRIMARY KEY,
            inbound_bytes BIGINT NOT NULL DEFAULT 0,
            outbound_bytes BIGINT NOT NULL DEFAULT 0
        )",

        // 审计日志
        @"CREATE SEQUENCE IF NOT EXISTS audit_log_seq START 1",
        @"CREATE TABLE IF NOT EXISTS audit_log (
            id BIGINT DEFAULT nextval('audit_log_seq') PRIMARY KEY,
            username VARCHAR NOT NULL,
            action VARCHAR NOT NULL,
            detail VARCHAR DEFAULT '',
            ip VARCHAR DEFAULT '',
            log_time TIMESTAMP NOT NULL
        )",

        // 运行日志（进程启动/停止记录）
        @"CREATE SEQUENCE IF NOT EXISTS runtime_log_seq START 1",
        @"CREATE TABLE IF NOT EXISTS runtime_logs (
            id BIGINT DEFAULT nextval('runtime_log_seq') PRIMARY KEY,
            start_time TIMESTAMP NOT NULL,
            stop_time TIMESTAMP,
            exit_reason VARCHAR DEFAULT '',
            duration_seconds BIGINT DEFAULT 0,
            peak_memory_mb DOUBLE DEFAULT 0,
            final_memory_mb DOUBLE DEFAULT 0,
            gc_gen0 BIGINT DEFAULT 0,
            gc_gen1 BIGINT DEFAULT 0,
            gc_gen2 BIGINT DEFAULT 0,
            total_requests BIGINT DEFAULT 0,
            total_intercepts BIGINT DEFAULT 0,
            dotnet_version VARCHAR DEFAULT '',
            os_description VARCHAR DEFAULT '',
            process_id INTEGER DEFAULT 0
        )",
    ];

    public static readonly string[] CreateIndexStatements =
    [
        "CREATE INDEX IF NOT EXISTS idx_traffic_time ON traffic_snapshots(snapshot_time)",
        "CREATE INDEX IF NOT EXISTS idx_security_ts_time ON security_timeslots(slot_time)",
        "CREATE INDEX IF NOT EXISTS idx_api_timing_time ON api_timing_snapshots(snapshot_time)",
        "CREATE INDEX IF NOT EXISTS idx_endpoint_time ON endpoint_stats_snapshots(snapshot_time)",
        "CREATE INDEX IF NOT EXISTS idx_intercept_time ON intercept_events(last_hit_time)",
        "CREATE INDEX IF NOT EXISTS idx_qps_time ON qps_snapshots(snapshot_time)",
        "CREATE INDEX IF NOT EXISTS idx_bandwidth_time ON bandwidth_snapshots(snapshot_time)",
        "CREATE INDEX IF NOT EXISTS idx_audit_time ON audit_log(log_time)",
        "CREATE INDEX IF NOT EXISTS idx_runtime_start ON runtime_logs(start_time)",
    ];
}
