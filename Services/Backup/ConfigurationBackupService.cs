using LyWaf.Config;
using LyWaf.Services.Configuration;
using LyWaf.Services.ManagementTasks;
using LyWaf.Shared;
using Microsoft.AspNetCore.DataProtection;

namespace LyWaf.Services.Backup;

public sealed record BackupInfo(string Id, long Size, DateTime CreatedAt, string Scope);

/// <summary>配置备份任务与恢复入口；密码仅在任务执行时解密，不写入日志或任务快照。</summary>
public sealed partial class ConfigurationBackupService(
    ManagementJobService jobs, IDataProtectionProvider protection,
    ConfigVersionService versions, ConfigurationLifecycleService lifecycle, IConfiguration configuration)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDataProtector _protector = protection.CreateProtector("LyWaf.BackupTaskPassword.v1");
    private readonly BackupArchive _archive = new();
    private readonly string _directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SharedData.ConfigFilePath))!, ".lywaf.backups");

    public BackupInfo[] List() => !Directory.Exists(_directory) ? [] : new DirectoryInfo(_directory)
        .EnumerateFiles("*.lybackup").OrderByDescending(x => x.CreationTimeUtc).Take(100)
        .Select(x => new BackupInfo(Path.GetFileNameWithoutExtension(x.Name), x.Length, x.CreationTimeUtc, x.Name.StartsWith("s-") ? "statistics" : x.Name.StartsWith("i-") ? "instance" : "configuration")).ToArray();

    public string GetPath(string id)
    {
        if (!Guid.TryParseExact(id.StartsWith("s-") || id.StartsWith("i-") ? id[2..] : id, "N", out _)) throw new ArgumentException("备份 ID 无效", nameof(id));
        return Path.Combine(_directory, id + ".lybackup");
    }

    public ManagementJobSnapshot Create(string password, string actor)
    {
        var secret = Protect(password);
        var id = Guid.NewGuid().ToString("N");
        return jobs.Enqueue("configuration-backup", actor, async (log, token) =>
        {
            await _gate.WaitAsync(token);
            var path = GetPath(id);
            var temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(_directory);
                if (Directory.EnumerateFiles(_directory, "*.lybackup").Take(100).Count() >= 100)
                    throw new InvalidDataException("最多保留 100 份备份，请先删除不再需要的备份");
                log("开始配置备份，范围为主配置、草稿和运行时补丁");
                var sources = Targets().Where(x => File.Exists(x.Value)).ToDictionary(x => x.Key, x => x.Value);
                if (!sources.ContainsKey("config")) throw new InvalidDataException("主配置文件不存在");
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    await _archive.CreateAsync(sources, output, _protector.Unprotect(secret), token);
                File.Move(temporary, path, true);
                log($"配置备份完成: {id}");
            }
            finally
            {
                try { File.Delete(temporary); }
                finally { _gate.Release(); }
            }
        }, TimeSpan.FromMinutes(10), [id]);
    }

    public ManagementJobSnapshot Restore(string id, string password, bool dryRun, string actor)
    {
        if (id.StartsWith("s-")) return RestoreStatistics(id, password, dryRun, actor);
        if (id.StartsWith("i-")) return QueueInstance(id, password, actor, true, dryRun);
        var path = GetPath(id);
        if (!File.Exists(path)) throw new ArgumentException("备份不存在", nameof(id));
        var secret = Protect(password);
        return jobs.Enqueue(dryRun ? "backup-preflight" : "configuration-restore", actor, async (log, token) =>
        {
            await _gate.WaitAsync(token);
            try
            {
                if (configuration is not IConfigurationRoot root) throw new InvalidDataException("当前配置不支持重载");
                var before = lifecycle.Capture(root);
                var targets = Targets();
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await _archive.RestoreAsync(input, _protector.Unprotect(secret), targets, async (files, ct) =>
                {
                    if (!files.ContainsKey("config")) throw new InvalidDataException("备份中缺少主配置");
                    foreach (var (name, current) in targets)
                        if (File.Exists(current) && !files.ContainsKey(name))
                            throw new InvalidDataException($"当前存在备份未包含的 {name}，请先处理该配置，避免恢复后被旧配置覆盖");
                    foreach (var (name, staged) in files)
                    {
                        if (new FileInfo(staged).Length > 8 * 1024 * 1024) throw new InvalidDataException("单份配置超过 8 MiB");
                        var text = await File.ReadAllTextAsync(staged, ct);
                        if (name is "config" or "draft") versions.ValidateMainCandidate(targets[name], text);
                        else using (System.Text.Json.JsonDocument.Parse(text)) { }
                    }
                    var active = files.GetValueOrDefault("draft") ?? files["config"];
                    var candidate = BackupConfigurationCandidate.Build(targets["config"], await File.ReadAllTextAsync(active, ct), files.GetValueOrDefault("patch"));
                    try { lifecycle.Validate(candidate); }
                    finally { (candidate as IDisposable)?.Dispose(); }
                    log("版本、摘要、文件白名单与配置语法预检通过");
                }, ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    var impact = lifecycle.ReloadAndValidate(root, before);
                    if (impact.RequiresRestart) log("配置已恢复，监听或进程级配置需重启后生效");
                    return Task.CompletedTask;
                }, dryRun, token);
                log(dryRun ? "预检完成，未修改运行配置" : "配置恢复并重载完成");
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                throw new InvalidDataException("备份密码错误或备份内容已损坏，未通过认证");
            }
            finally { _gate.Release(); }
        }, TimeSpan.FromMinutes(10), [id]);
    }

    public async Task DeleteAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { File.Delete(GetPath(id)); }
        finally { _gate.Release(); }
    }

    public async Task<string> UploadAsync(Stream input, CancellationToken token, bool statistics = false, bool instance = false)
    {
        await _gate.WaitAsync(token);
        var id = (instance ? "i-" : statistics ? "s-" : "") + Guid.NewGuid().ToString("N");
        var path = GetPath(id);
        try
        {
            Directory.CreateDirectory(_directory);
            if (Directory.EnumerateFiles(_directory, "*.lybackup").Take(100).Count() >= 100)
                throw new InvalidDataException("备份数量已达到 100 份上限");
            await using (var output = new FileStream(path + ".tmp", FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var header = new byte[8];
                await input.ReadExactlyAsync(header, token);
                if (!header.AsSpan().SequenceEqual("LYWAFBK1"u8) && !header.AsSpan().SequenceEqual(PasswordlessBackupStream.Magic))
                    throw new InvalidDataException("备份格式或版本无效");
                await output.WriteAsync(header, token);
                var buffer = new byte[65536];
                long total = 8;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    if ((total += count) > 32 * 1024 * 1024) throw new InvalidDataException("配置备份上传不能超过 32 MiB");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            File.Move(path + ".tmp", path);
            return id;
        }
        finally
        {
            try { File.Delete(path + ".tmp"); }
            finally { _gate.Release(); }
        }
    }

    private static Dictionary<string, string> Targets()
    {
        var config = Path.GetFullPath(SharedData.ConfigFilePath);
        var directory = Path.GetDirectoryName(config)!;
        return new()
        {
            ["config"] = config,
            ["draft"] = Path.Combine(directory, ".lywaf.draft" + Path.GetExtension(config)),
            ["patch"] = Path.GetFullPath(LbPatchConfig.GetPatchFilePath())
        };
    }

    private string Protect(string password)
    {
        if (password is not { Length: 0 } and not { Length: >= 12 and <= 1024 }) throw new ArgumentException("密码长度须为 12 至 1024 字符，或留空使用免密码备份", nameof(password));
        return _protector.Protect(password);
    }
}
