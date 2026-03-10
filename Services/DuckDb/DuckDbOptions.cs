namespace LyWaf.Services.DuckDb;

/// <summary>
/// DuckDB 持久化配置
/// </summary>
public class DuckDbOptions
{
    /// <summary>是否启用 DuckDB 持久化</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>数据库文件路径（相对于工作目录）</summary>
    public string DatabasePath { get; set; } = "datas/lywaf_stats.duckdb";

    /// <summary>刷写间隔（秒）</summary>
    public int FlushIntervalSeconds { get; set; } = 60;

    /// <summary>详细数据保留天数</summary>
    public int DetailRetentionDays { get; set; } = 30;

    /// <summary>聚合数据保留天数</summary>
    public int AggregateRetentionDays { get; set; } = 365;
}
