using DuckDB.NET.Data;

namespace LyWaf.Services.DuckDb;

/// <summary>
/// DuckDB 连接管理服务接口
/// </summary>
public interface IDuckDbService : IDisposable
{
    /// <summary>DuckDB 是否已启用</summary>
    bool IsEnabled { get; }

    /// <summary>获取数据库连接（单例长连接）</summary>
    DuckDBConnection GetConnection();

    /// <summary>初始化表结构</summary>
    Task InitializeSchemaAsync();
}
