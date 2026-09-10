using System.Text.Json;
using LyWaf.Services.Auth;
using Microsoft.AspNetCore.DataProtection;

namespace LyWaf.Services.Backup;

/// <summary>冷备份/恢复登记：启动任何状态写入服务前执行，不复制正在变化的插件数据库。</summary>
public static class InstanceStateCoordinator
{
    private sealed record Pending(string Operation, string Id, string Password, string Actor, string Paths);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Ticket(string directory) => Path.Combine(directory, ".lywaf.instance-pending");
    private static IDataProtector Protector(string directory) => DataProtectionProvider.Create(
        new DirectoryInfo(Path.Combine(directory, ".lywaf.restore-keys")),
        builder => builder.SetApplicationName("LyWaf.InstanceState.v1")).CreateProtector("pending");

    public static bool IsPending(string directory) => File.Exists(Ticket(directory));

    public static void Cancel(string directory)
    {
        File.Delete(Ticket(directory));
        File.Delete(Ticket(directory) + ".lybackup");
    }

    public static async Task PrepareAsync(string directory, InstanceStatePaths paths, string id, string password,
        string actor, bool restore, CancellationToken token)
    {
        if (IsPending(directory)) throw new InvalidDataException("已有待启动执行的实例状态任务，请先执行或取消");
        var pending = Ticket(directory);
        try
        {
            if (restore)
            {
                await using var input = File.OpenRead(Path.Combine(directory, ".lywaf.backups", id + ".lybackup"));
                await using var output = new FileStream(pending + ".lybackup", FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                await input.CopyToAsync(output, 65536, token);
            }
            var request = new Pending(restore ? "restore" : "backup", id, password, actor, paths.Fingerprint);
            await File.WriteAllTextAsync(pending + ".tmp", Protector(directory).Protect(JsonSerializer.Serialize(request)), token);
            File.Move(pending + ".tmp", pending);
        }
        catch { File.Delete(pending + ".lybackup"); throw; }
        finally { File.Delete(pending + ".tmp"); }
    }

    public static async Task PreflightAsync(string backup, string password, InstanceStatePaths paths, CancellationToken token)
    {
        await using var input = File.OpenRead(backup);
        await new BackupArchive().RestoreAsync(input, password, new Dictionary<string, string>(), ValidateAsync,
            _ => Task.CompletedTask, true, token, paths.Resolve, true);
    }

    public static void ApplyBeforeStartup(string directory, InstanceStatePaths paths)
    {
        var pending = Ticket(directory);
        if (!File.Exists(pending)) return;
        if (new FileInfo(pending).Length > 16384) throw new InvalidDataException("实例状态任务凭据超过限制");
        var request = JsonSerializer.Deserialize<Pending>(Protector(directory).Unprotect(File.ReadAllText(pending)))
            ?? throw new InvalidDataException("实例状态任务凭据无效");
        if (request.Paths != paths.Fingerprint) throw new InvalidDataException("状态目录配置已改变，请取消任务后重新登记");
        if (!request.Id.StartsWith("i-") || !Guid.TryParseExact(request.Id[2..], "N", out _)) throw new InvalidDataException("实例备份 ID 无效");
        var archive = new BackupArchive();
        if (request.Operation == "backup")
        {
            var backups = Path.Combine(directory, ".lywaf.backups");
            Directory.CreateDirectory(backups);
            if (Directory.EnumerateFiles(backups, "*.lybackup").Take(100).Count() >= 100) throw new InvalidDataException("最多保留 100 份备份");
            var path = Path.Combine(backups, request.Id + ".lybackup");
            try
            {
                var sources = paths.Enumerate();
                ValidateAsync(sources, default).GetAwaiter().GetResult();
                using (var output = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                    archive.CreateAsync(sources, output, request.Password, default).GetAwaiter().GetResult();
                if (new FileInfo(path + ".tmp").Length > 32 * 1024 * 1024) throw new InvalidDataException("加密后的实例状态备份超过 32 MiB 管理端限制");
                File.Move(path + ".tmp", path, true);
            }
            finally { File.Delete(path + ".tmp"); }
        }
        else if (request.Operation == "restore")
        {
            using var input = File.OpenRead(pending + ".lybackup");
            archive.RestoreAsync(input, request.Password, new Dictionary<string, string>(), ValidateAsync,
                _ => Task.CompletedTask, false, default, paths.Resolve, true).GetAwaiter().GetResult();
        }
        else throw new InvalidDataException("实例状态任务类型无效");
        NLog.LogManager.GetCurrentClassLogger().Info("实例状态 {Operation} 完成，备份 {Id}，登记操作者 {Actor}", request.Operation, request.Id, request.Actor);
        Cancel(directory);
    }

    private static async Task ValidateAsync(IReadOnlyDictionary<string, string> files, CancellationToken token)
    {
        if (!files.TryGetValue("auth", out var authPath)) throw new InvalidDataException("实例状态备份缺少管理账号");
        if (new FileInfo(authPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("管理账号文件超过限制");
        await using var input = File.OpenRead(authPath);
        var auth = await JsonSerializer.DeserializeAsync<AuthConfig>(input, Json, token)
            ?? throw new InvalidDataException("管理账号文件无效");
        var key = new byte[1024];
        if (auth.JwtSecret == null || !Convert.TryFromBase64String(auth.JwtSecret, key, out var keyLength) || keyLength < 32 ||
            auth.PasswordHash is not { Length: 64 } || !auth.PasswordHash.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(auth.Username) ||
            auth.Users == null || auth.Roles == null ||
            !(!auth.Disabled && auth.Role == ManagementRoles.Admin || auth.Users.Any(user => !user.Disabled && user.Role == ManagementRoles.Admin)))
            throw new InvalidDataException("管理账号缺少有效密钥或可用管理员");
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        foreach (var (name, path) in files)
        {
            if (name is not ("tokens" or "notifications" or "sites" or "incidents" or "site-statistics")) continue;
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("状态 JSON 文件超过限制");
            await using var source = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(source, cancellationToken: token);
            if (json.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new InvalidDataException("状态 JSON 结构无效");
        }
    }
}
