using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.Backup;

/// <summary>备份清单中的逻辑文件，不接受归档提供的磁盘目标路径。</summary>
public sealed record BackupFile(string Name, long Length, string Sha256, DateTimeOffset? LastModified = null);
public sealed record BackupManifest(int Schema, string Application, DateTimeOffset CreatedAt, BackupFile[] Files);

/// <summary>流式打包及白名单预检；恢复写入前验证所有条目，失败时回滚已经替换的文件。</summary>
public sealed class BackupArchive
{
    private const int MaximumFiles = 1000;
    private const long MaximumFileBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task CreateAsync(IReadOnlyDictionary<string, string> sources, Stream destination,
        string password, CancellationToken token)
    {
        ValidateNames(sources.Keys);
        var temporary = Path.Combine(Path.GetTempPath(), $"lywaf-backup-{Guid.NewGuid():N}.zip");
        try
        {
            var files = new List<BackupFile>();
            await using (var output = Open(temporary, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    long total = 0;
                    foreach (var (name, path) in sources)
                    {
                        token.ThrowIfCancellationRequested();
                        EnsureNoLinks(path);
                        await using var input = Open(path, FileMode.Open, FileAccess.Read);
                        await using var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var length = await CopyHashAsync(input, entry, hash, token);
                        if ((total += length) > EncryptedBackupStream.MaximumPlaintextBytes)
                            throw new InvalidDataException("备份数据总量超过限制");
                        files.Add(new(name, length, Convert.ToHexString(hash.GetHashAndReset()), File.GetLastWriteTimeUtc(path)));
                    }
                    await using var manifest = zip.CreateEntry("manifest.json").Open();
                    await JsonSerializer.SerializeAsync(manifest, new BackupManifest(1, "LyWaf", DateTimeOffset.UtcNow, files.ToArray()), Json, token);
                }
                output.Position = 0;
                if (password.Length == 0) await PasswordlessBackupStream.WriteAsync(output, destination, token);
                else await EncryptedBackupStream.EncryptAsync(output, destination, password, token);
            }
        }
        finally { File.Delete(temporary); }
    }

    public async Task<BackupManifest> RestoreAsync(Stream encrypted, string password,
        IReadOnlyDictionary<string, string> targets,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task> validate,
        Func<CancellationToken, Task> activate, bool dryRun, CancellationToken token,
        Func<BackupManifest, IReadOnlyDictionary<string, string>>? resolveTargets = null, bool removeMissing = false)
    {
        ValidateNames(targets.Keys);
        var root = Path.Combine(Path.GetTempPath(), $"lywaf-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var replacements = new List<(string Target, string? Rollback)>();
        try
        {
            var archivePath = Path.Combine(root, "archive.zip");
            await using (var clear = Open(archivePath, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                if (password.Length == 0) await PasswordlessBackupStream.ReadAsync(encrypted, clear, token);
                else await EncryptedBackupStream.DecryptAsync(encrypted, clear, password, token);
            }
            await using var archive = Open(archivePath, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaximumFiles + 1 || zip.Entries.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
                throw new InvalidDataException("归档包含过多或重复条目");
            var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("缺少备份清单");
            if (manifestEntry.Length > 1024 * 1024) throw new InvalidDataException("备份清单过大");
            BackupManifest manifest;
            await using (var input = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(input, Json, token) ?? throw new InvalidDataException("备份清单为空");
            if (manifest.Schema != 1 || manifest.Application != "LyWaf" || manifest.Files == null)
                throw new InvalidDataException("备份版本或应用不兼容");
            ValidateNames(manifest.Files.Select(x => x.Name));
            if (resolveTargets != null)
            {
                targets = resolveTargets(manifest);
                ValidateNames(targets.Keys);
            }
            var targetPaths = targets.Values.Select(Path.GetFullPath).ToArray();
            if (targetPaths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != targetPaths.Length)
                throw new InvalidDataException("多个归档条目不能写入同一目标");
            foreach (var target in targetPaths) EnsureNoLinks(target);
            if (manifest.Files.Length + 1 != zip.Entries.Count) throw new InvalidDataException("归档包含未声明的条目");
            var staged = new Dictionary<string, string>(StringComparer.Ordinal);
            long total = 0;
            foreach (var file in manifest.Files)
            {
                if (!targets.ContainsKey(file.Name)) throw new InvalidDataException($"不支持恢复的文件: {file.Name}");
                var entry = zip.GetEntry(file.Name) ?? throw new InvalidDataException("备份条目缺失");
                if (file.Length < 0 || entry.Length != file.Length || file.Length > MaximumFileBytes ||
                    (total += file.Length) > EncryptedBackupStream.MaximumPlaintextBytes)
                    throw new InvalidDataException("备份文件大小不符合限制");
                var stagedPath = Path.Combine(root, $"data-{staged.Count}");
                await using var input = entry.Open();
                await using var output = Open(stagedPath, FileMode.CreateNew, FileAccess.Write);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var actual = await CopyHashAsync(input, output, hash, token);
                if (actual != file.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"文件校验失败: {file.Name}");
                staged.Add(file.Name, stagedPath);
            }
            await validate(staged, token);
            if (dryRun) return manifest;
            try
            {
                foreach (var (name, source) in staged)
                {
                    token.ThrowIfCancellationRequested();
                    var target = Path.GetFullPath(targets[name]);
                    EnsureNoLinks(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var rollback = File.Exists(target) ? Path.Combine(root, $"rollback-{replacements.Count}") : null;
                    if (rollback != null) File.Copy(target, rollback);
                    // 同目录临时文件和原子替换，避免暴露写入一半的配置。
                    await ReplaceAsync(source, target, token, manifest.Files.Single(f => f.Name == name).LastModified?.UtcDateTime);
                    replacements.Add((target, rollback));
                }
                if (removeMissing)
                {
                    foreach (var (name, path) in targets.Where(pair => !staged.ContainsKey(pair.Key)))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!File.Exists(path)) continue;
                        EnsureNoLinks(path);
                        var rollback = Path.Combine(root, $"rollback-{replacements.Count}");
                        File.Copy(path, rollback);
                        File.Delete(path);
                        replacements.Add((path, rollback));
                    }
                }
                await activate(token);
            }
            catch (Exception failure)
            {
                var rollbackErrors = new List<Exception>();
                foreach (var (target, rollback) in replacements.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (rollback == null) File.Delete(target);
                        else await ReplaceAsync(rollback, target, CancellationToken.None);
                    }
                    catch (Exception ex) { rollbackErrors.Add(ex); }
                }
                try { await activate(CancellationToken.None); }
                catch (Exception ex) { rollbackErrors.Add(ex); }
                if (rollbackErrors.Count != 0)
                {
                    // 回滚失败必须保留副本供人工恢复，不能清理唯一的旧数据。
                    var retained = root;
                    root = "";
                    throw new AggregateException($"恢复和回滚未完全成功，副本保留于 {retained}", new[] { failure }.Concat(rollbackErrors));
                }
                throw;
            }
            return manifest;
        }
        finally { if (root.Length > 0) Directory.Delete(root, true); }
    }

    private static async Task ReplaceAsync(string source, string target, CancellationToken token, DateTime? lastModified = null)
    {
        var temporary = target + $".{Guid.NewGuid():N}.restore";
        try
        {
            await using (var input = Open(source, FileMode.Open, FileAccess.Read))
            await using (var output = Open(temporary, FileMode.CreateNew, FileAccess.Write))
                await input.CopyToAsync(output, 65536, token);
            File.SetLastWriteTimeUtc(temporary, lastModified ?? File.GetLastWriteTimeUtc(source));
            File.Move(temporary, target, true);
        }
        finally { File.Delete(temporary); }
    }

    private static void ValidateNames(IEnumerable<string> names)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (string.IsNullOrEmpty(name) || name.Length > 512 || name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_') ||
                name is "." or ".." || !unique.Add(name) || unique.Count > MaximumFiles)
                throw new InvalidDataException("无效或重复的备份逻辑文件名");
    }

    /// <summary>逐级拒绝目录链接；只检查叶文件不足以阻止经父目录逃逸。</summary>
    internal static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("备份路径不能包含符号链接或目录联接");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static FileStream Open(string path, FileMode mode, FileAccess access) =>
        new(path, mode, access, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<long> CopyHashAsync(Stream input, Stream output, IncrementalHash hash, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                if ((total += count) > MaximumFileBytes) throw new InvalidDataException("单个备份文件超过 512 MiB 限制");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
            return total;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
