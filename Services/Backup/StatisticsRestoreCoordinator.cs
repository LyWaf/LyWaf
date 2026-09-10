using System.Data.Common;
using System.Text.Json;
using DuckDB.NET.Data;
using LyWaf.Services.DuckDb;
using Microsoft.AspNetCore.DataProtection;

namespace LyWaf.Services.Backup;

/// <summary>在启动统计恢复和写入服务之前应用已授权的备份，避免内存旧值回写。</summary>
public static class StatisticsRestoreCoordinator
{
    public static IDisposable? AcquireLease(string database, bool enabled)
    {
        if (!enabled) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database))!);
        return new FileStream(database + ".lywaf.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed record PendingRestore(string Database, string Password, string Actor);

    private static IDataProtector Protector(string directory) => DataProtectionProvider.Create(
        new DirectoryInfo(Path.Combine(directory, ".lywaf.restore-keys")),
        builder => builder.SetApplicationName("LyWaf.StatisticsRestore.v1")).CreateProtector("pending");

    public static async Task PrepareAsync(string directory, string database, string backup, string password, string actor, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, ".lywaf.statistics-restore");
        if (File.Exists(pending)) throw new InvalidDataException("已有待重启执行的数据恢复，请先取消或执行");
        var archive = pending + ".lybackup";
        try
        {
            await using (var source = File.OpenRead(backup))
            await using (var target = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await source.CopyToAsync(target, 65536, token);
            var ticket = Protector(directory).Protect(JsonSerializer.Serialize(new PendingRestore(Path.GetFullPath(database), password, actor)));
            await File.WriteAllTextAsync(pending + ".tmp", ticket, token);
            File.Move(pending + ".tmp", pending);
        }
        catch { File.Delete(archive); throw; }
        finally { File.Delete(pending + ".tmp"); }
    }

    public static bool IsPending(string directory) => File.Exists(Path.Combine(directory, ".lywaf.statistics-restore"));

    public static void Cancel(string directory)
    {
        var pending = Path.Combine(directory, ".lywaf.statistics-restore");
        File.Delete(pending);
        File.Delete(pending + ".lybackup");
    }

    public static void ApplyBeforeStartup(string directory, string database)
    {
        var pending = Path.Combine(directory, ".lywaf.statistics-restore");
        if (!File.Exists(pending)) return;
        if (new FileInfo(pending).Length > 16384) throw new InvalidDataException("数据恢复凭据超过限制");
        var request = JsonSerializer.Deserialize<PendingRestore>(Protector(directory).Unprotect(File.ReadAllText(pending)))
            ?? throw new InvalidDataException("数据恢复凭据无效");
        if (!Path.GetFullPath(database).Equals(request.Database, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("数据恢复登记后数据库路径发生变化，请取消恢复并重新预检");
        Directory.CreateDirectory(Path.GetDirectoryName(request.Database)!);
        // 正常关闭旧库并合并 WAL；被另一进程占用时 Open 会失败，禁止覆盖活动数据库。
        if (File.Exists(request.Database))
        {
            using var current = Open(request.Database);
            using var checkpoint = current.CreateCommand();
            checkpoint.CommandText = "CHECKPOINT";
            checkpoint.ExecuteNonQuery();
        }
        if (File.Exists(request.Database + ".wal")) throw new InvalidDataException("旧数据库仍存在 WAL，不能执行恢复");
        using var input = File.OpenRead(pending + ".lybackup");
        new BackupArchive().RestoreAsync(input, request.Password, new Dictionary<string, string> { ["statistics"] = request.Database },
            ValidateAsync, _ => Task.CompletedTask, false, CancellationToken.None).GetAwaiter().GetResult();
        input.Close();
        NLog.LogManager.GetCurrentClassLogger().Info("统计备份恢复完成，登记操作者: {Actor}", request.Actor);
        Cancel(directory);
    }

    public static Task ValidateAsync(IReadOnlyDictionary<string, string> files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (files.Count != 1 || !files.TryGetValue("statistics", out var path))
            throw new InvalidDataException("备份必须且只能包含统计数据库");
        using var expected = Open(":memory:");
        foreach (var sql in DuckDbSchema.CreateTableStatements)
        {
            using var create = expected.CreateCommand(); create.CommandText = sql; create.ExecuteNonQuery();
        }
        using var actual = Open(path);
        var required = Columns(expected);
        var received = Columns(actual);
        if (!required.All(column => received.Contains(column))) throw new InvalidDataException("统计数据库表结构与当前版本不兼容");
        return Task.CompletedTask;

        static HashSet<string> Columns(DuckDBConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.table_name, c.column_name, c.data_type
                FROM information_schema.columns c
                JOIN information_schema.tables t
                  ON c.table_catalog = t.table_catalog AND c.table_schema = t.table_schema AND c.table_name = t.table_name
                WHERE c.table_schema = 'main' AND t.table_type = 'BASE TABLE'
                """;
            using var reader = command.ExecuteReader();
            var result = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (result.Count >= 10000) throw new InvalidDataException("统计数据库结构超过限制");
                result.Add(reader.GetString(0) + ":" + reader.GetString(1) + ":" + reader.GetString(2));
            }
            return result;
        }
    }

    private static DuckDBConnection Open(string path)
    {
        var settings = new DbConnectionStringBuilder { ["Data Source"] = path };
        var connection = new DuckDBConnection(settings.ConnectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
