using DuckDB.NET.Data;

namespace LyWaf.Services.DuckDb;

/// <summary>
/// DuckDB 连接管理服务接口
/// </summary>
public interface IDuckDbService : IDisposable
{
    /// <summary>DuckDB 是否已启用</summary>
    bool IsEnabled { get; set; }
    /// <summary>只读连接状态，不会为健康检查触发数据库打开或 WAL 恢复。</summary>
    string HealthStatus => IsEnabled ? "unknown" : "disabled";

    /// <summary>获取数据库连接（单例长连接）</summary>
    DuckDBConnection GetConnection();

    /// <summary>初始化表结构</summary>
    Task InitializeSchemaAsync();

    /// <summary>执行 CHECKPOINT，将 WAL 合并进主库文件（控制 WAL 体积、加速下次启动）</summary>
    void Checkpoint();
}
