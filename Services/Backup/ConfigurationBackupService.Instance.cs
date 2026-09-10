using LyWaf.Services.ManagementTasks;
using Microsoft.AspNetCore.DataProtection;

namespace LyWaf.Services.Backup;

public sealed partial class ConfigurationBackupService
{
    public bool InstanceStatePending => InstanceStateCoordinator.IsPending(DataDirectory);

    public async Task CancelInstanceStateAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { InstanceStateCoordinator.Cancel(DataDirectory); }
        finally { _gate.Release(); }
    }

    public ManagementJobSnapshot CreateInstance(string password, string actor)
        => QueueInstance("i-" + Guid.NewGuid().ToString("N"), password, actor, false, false);

    private ManagementJobSnapshot QueueInstance(string id, string password, string actor, bool restore, bool dryRun)
    {
        var secret = Protect(password);
        var path = GetPath(id);
        if (restore && !File.Exists(path)) throw new ArgumentException("备份不存在", nameof(id));
        return jobs.Enqueue(dryRun ? "instance-preflight" : restore ? "instance-restore-prepare" : "instance-backup-prepare", actor, async (log, token) =>
        {
            await _gate.WaitAsync(token);
            try
            {
                var paths = InstanceStatePaths.FromConfiguration(configuration);
                var clear = _protector.Unprotect(secret);
                if (restore) await InstanceStateCoordinator.PreflightAsync(path, clear, paths, token);
                if (dryRun) { log("实例状态备份认证、目录边界和账号预检通过，未修改当前数据"); return; }
                await InstanceStateCoordinator.PrepareAsync(DataDirectory, paths, id, clear, actor, restore, token);
                log("已登记，需重启后在状态服务加载前执行；当前账号、证书与插件数据尚未改变");
            }
            finally { _gate.Release(); }
        }, TimeSpan.FromMinutes(15), [id]);
    }
}
