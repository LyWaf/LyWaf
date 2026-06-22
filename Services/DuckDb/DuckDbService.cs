using DuckDB.NET.Data;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.DuckDb;

/// <summary>
/// DuckDB 连接管理单例服务
/// 负责连接生命周期管理和表结构初始化
/// </summary>
public class DuckDbService : IDuckDbService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly DuckDbOptions _options;
    private volatile DuckDBConnection? _connection;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsEnabled { get; set; }

    public DuckDbService(IOptions<DuckDbOptions> options)
    {
        _options = options.Value;
        IsEnabled = _options.Enabled;

        if (!_options.Enabled)
        {
            _logger.Info("DuckDB 持久化已禁用");
        }
        // 注意：连接延迟到首次 GetConnection() 时才打开。
        // DuckDB 打开时会重放 WAL，体积大时可能耗时数十秒；若放在构造函数里，
        // 会在 DI 解析 HostedService（host 启动主线程）时同步阻塞，拖慢启动就绪。
        // 延迟打开后，首次访问发生在后台恢复/刷写任务里，不阻塞启动。
    }

    /// <summary>
    /// 确保连接已打开（幂等，线程安全）。打开失败则降级为纯内存模式。
    /// </summary>
    private void EnsureConnection()
    {
        if (_connection != null || !IsEnabled || _disposed) return;
        lock (_lock)
        {
            if (_connection != null || !IsEnabled || _disposed) return;
            try
            {
                // 确保目录存在
                var dir = Path.GetDirectoryName(Path.GetFullPath(_options.DatabasePath));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var conn = new DuckDBConnection($"Data Source={_options.DatabasePath}");
                conn.Open();
                _connection = conn;
                _logger.Info("DuckDB 连接已建立: {} (Open 耗时 {}ms)", _options.DatabasePath, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "DuckDB 初始化失败，将以纯内存模式运行");
                IsEnabled = false;
                _options.Enabled = false;
                _connection = null;
            }
        }
    }

    public DuckDBConnection GetConnection()
    {
        EnsureConnection();
        if (!IsEnabled || _connection == null)
            throw new InvalidOperationException("DuckDB is not enabled");
        return _connection;
    }

    public void Checkpoint()
    {
        if (!IsEnabled || _connection == null || _disposed) return;
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "CHECKPOINT";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // CHECKPOINT 可能因存在未完成事务而失败，下个周期会重试，记 Warn 即可
            _logger.Warn(ex, "DuckDB CHECKPOINT 失败");
        }
    }

    public async Task InitializeSchemaAsync()
    {
        if (!IsEnabled) return;
        EnsureConnection();
        if (_connection == null) return;

        try
        {
            foreach (var sql in DuckDbSchema.CreateTableStatements)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }

            foreach (var sql in DuckDbSchema.CreateIndexStatements)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }

            _logger.Info("DuckDB 表结构初始化完成");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DuckDB Schema 初始化失败");
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            // 关闭前合并 WAL，避免下次启动重放大量 WAL
            if (_connection != null)
            {
                try
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "CHECKPOINT";
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "关闭前 CHECKPOINT 失败");
                }
            }
            _disposed = true;
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
            _logger.Info("DuckDB 连接已关闭");
        }
    }
}
