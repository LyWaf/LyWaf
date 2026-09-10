using LyWaf.Services.ManagementTasks;
using Microsoft.AspNetCore.DataProtection;

namespace LyWaf.Services.Backup;

public sealed partial class ConfigurationBackupService
{
    private string DataDirectory => Path.GetDirectoryName(_directory)!;
    private string DatabasePath => Path.GetFullPath(configuration.GetValue<string>("DuckDb:DatabasePath") ?? "bin/lywaf_stats.duckdb");
    public bool StatisticsRestorePending => StatisticsRestoreCoordinator.IsPending(DataDirectory);

    public async Task CancelStatisticsRestoreAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { StatisticsRestoreCoordinator.Cancel(DataDirectory); }
        finally { _gate.Release(); }
    }

    public ManagementJobSnapshot CreateStatistics(string password, string actor)
    {
        if (!configuration.GetValue("DuckDb:Enabled", true)) throw new ArgumentException("统计数据库未启用");
        var secret = Protect(password);
        var id = "s-" + Guid.NewGuid().ToString("N");
        return jobs.Enqueue("statistics-backup", actor, async (log, token) =>
        {
            await _gate.WaitAsync(token);
            var snapshot = Path.Combine(Path.GetTempPath(), "lywaf-statistics-" + Guid.NewGuid().ToString("N") + ".duckdb");
            var path = GetPath(id);
            try
            {
                Directory.CreateDirectory(_directory);
                if (Directory.EnumerateFiles(_directory, "*.lybackup").Take(100).Count() >= 100)
                    throw new InvalidDataException("最多保留 100 份备份");
                log("开始统计数据库事务快照，仅包含已经提交的数据");
                await DuckDbBackupSnapshot.CreateAsync(DatabasePath, snapshot, token);
                await using (var output = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await _archive.CreateAsync(new Dictionary<string, string> { ["statistics"] = snapshot }, output, _protector.Unprotect(secret), token);
                if (new FileInfo(path + ".tmp").Length > 32 * 1024 * 1024) throw new InvalidDataException("压缩加密后的统计备份超过 32 MiB 管理端限制");
                File.Move(path + ".tmp", path);
                log($"统计数据备份完成: {id}");
            }
            finally
            {
                try { File.Delete(snapshot); File.Delete(snapshot + ".wal"); File.Delete(path + ".tmp"); }
                finally { _gate.Release(); }
            }
        }, TimeSpan.FromMinutes(15), [id]);
    }

    private ManagementJobSnapshot RestoreStatistics(string id, string password, bool dryRun, string actor)
    {
        var path = GetPath(id);
        if (!File.Exists(path)) throw new ArgumentException("备份不存在", nameof(id));
        var secret = Protect(password);
        return jobs.Enqueue(dryRun ? "statistics-preflight" : "statistics-restore-prepare", actor, async (log, token) =>
        {
            await _gate.WaitAsync(token);
            try
            {
                var clearPassword = _protector.Unprotect(secret);
                await using (var input = File.OpenRead(path))
                    await _archive.RestoreAsync(input, clearPassword, new Dictionary<string, string> { ["statistics"] = DatabasePath },
                        StatisticsRestoreCoordinator.ValidateAsync, _ => Task.CompletedTask, true, token);
                log("统计备份认证、摘要、版本和数据库结构预检通过");
                if (!dryRun)
                {
                    await StatisticsRestoreCoordinator.PrepareAsync(DataDirectory, DatabasePath, path, clearPassword, actor, token);
                    log("恢复已登记，重启后在加载统计与启动写入服务之前执行；当前数据未改变");
                }
            }
            catch (System.Security.Cryptography.CryptographicException) { throw new InvalidDataException("备份密码错误或备份损坏"); }
            finally { _gate.Release(); }
        }, TimeSpan.FromMinutes(15), [id]);
    }
}
