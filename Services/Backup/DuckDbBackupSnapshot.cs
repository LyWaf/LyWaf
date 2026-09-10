using System.Data.Common;
using DuckDB.NET.Data;

namespace LyWaf.Services.Backup;

/// <summary>使用独立连接和数据库事务生成快照，不读取运行中的数据库文件或 WAL。</summary>
public static class DuckDbBackupSnapshot
{
    public static async Task CreateAsync(string databasePath, string destination, CancellationToken token)
    {
        if (!File.Exists(databasePath)) throw new InvalidDataException("统计数据库尚未创建");
        if (File.Exists(destination) || File.Exists(destination + ".wal"))
            throw new InvalidDataException("数据库快照目标已存在");
        var connectionString = new DbConnectionStringBuilder { ["Data Source"] = Path.GetFullPath(databasePath) };
        using var connection = new DuckDBConnection(connectionString.ConnectionString);
        await connection.OpenAsync(token);
        using var name = connection.CreateCommand();
        name.CommandText = "SELECT current_database()";
        var source = Convert.ToString(await name.ExecuteScalarAsync(token)) ?? throw new InvalidDataException("无法确定源数据库");
        var alias = "lywaf_backup_" + Guid.NewGuid().ToString("N");
        var attached = false;
        var completed = false;
        try
        {
            await Execute($"ATTACH '{Path.GetFullPath(destination).Replace("'", "''")}' AS {alias}", token);
            attached = true;
            using (var transaction = connection.BeginTransaction())
            {
                using var copy = connection.CreateCommand();
                copy.Transaction = transaction;
                copy.CommandText = $"COPY FROM DATABASE \"{source.Replace("\"", "\"\"")}\" TO {alias}";
                await copy.ExecuteNonQueryAsync(token);
                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            await Execute($"CHECKPOINT {alias}", token);
            await Execute($"DETACH {alias}", token);
            attached = false;
            completed = true;
        }
        finally
        {
            try
            {
                if (attached)
                {
                    try { await Execute($"DETACH {alias}", CancellationToken.None); }
                    finally { connection.Close(); }
                }
            }
            finally
            {
                if (!completed)
                {
                    File.Delete(destination);
                    File.Delete(destination + ".wal");
                }
            }
        }

        async Task Execute(string sql, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellation);
        }
    }
}
