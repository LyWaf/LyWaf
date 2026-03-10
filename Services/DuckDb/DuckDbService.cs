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
    private DuckDBConnection? _connection;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsEnabled => _options.Enabled;

    public DuckDbService(IOptions<DuckDbOptions> options)
    {
        _options = options.Value;

        if (!_options.Enabled)
        {
            _logger.Info("DuckDB 持久化已禁用");
            return;
        }

        try
        {
            // 确保目录存在
            var dir = Path.GetDirectoryName(Path.GetFullPath(_options.DatabasePath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connection = new DuckDBConnection($"Data Source={_options.DatabasePath}");
            _connection.Open();
            _logger.Info("DuckDB 连接已建立: {}", _options.DatabasePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "DuckDB 初始化失败，将以纯内存模式运行");
            _options.Enabled = false;
            _connection = null;
        }
    }

    public DuckDBConnection GetConnection()
    {
        if (!IsEnabled || _connection == null)
            throw new InvalidOperationException("DuckDB is not enabled");
        return _connection;
    }

    public async Task InitializeSchemaAsync()
    {
        if (!IsEnabled || _connection == null) return;

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
            _disposed = true;
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
            _logger.Info("DuckDB 连接已关闭");
        }
    }
}
