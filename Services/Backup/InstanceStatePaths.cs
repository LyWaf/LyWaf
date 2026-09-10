using System.Security.Cryptography;
using System.Text;
using LyWaf.Shared;

namespace LyWaf.Services.Backup;

/// <summary>实例状态的受信目录白名单；归档只携带逻辑根标识和编码后的相对文件名。</summary>
public sealed class InstanceStatePaths
{
    public sealed record Root(string Path, bool Directory);
    private readonly IReadOnlyDictionary<string, Root> roots;
    public InstanceStatePaths(IReadOnlyDictionary<string, Root> roots)
    {
        this.roots = roots;
        foreach (var root in roots.Values.Where(root => root.Directory))
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));
            var cwd = Path.TrimEndingDirectorySeparator(Directory.GetCurrentDirectory());
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (path.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), comparison) || cwd.Equals(path, comparison) ||
                cwd.StartsWith(path + Path.DirectorySeparatorChar, comparison))
                throw new InvalidDataException("实例状态目录不能覆盖工作目录或磁盘根目录");
        }
    }

    public static InstanceStatePaths FromConfiguration(IConfiguration configuration)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(SharedData.ConfigFilePath))!;
        var roots = new Dictionary<string, Root>
        {
            ["auth"] = new(Path.GetFullPath(".lywaf.auth.json"), false),
            ["tokens"] = new(Path.GetFullPath(".lywaf.management-tokens.json"), false),
            ["notifications"] = new(Path.Combine(directory, ".lywaf.notifications.json"), false),
            ["sites"] = new(Path.Combine(directory, ".lywaf.sites.json"), false),
            ["incidents"] = new(Path.Combine(directory, ".lywaf.incidents.json"), false),
            ["site-statistics"] = new(Path.Combine(directory, ".lywaf.site-statistics.json"), false),
            ["keys"] = new(Path.Combine(directory, ".lywaf.keys"), true),
            ["requests"] = new(Path.Combine(directory, ".lywaf.incident-requests"), true),
            ["plugins"] = new(Path.GetFullPath(configuration["Plugins:DataDirectory"] ?? "plugin_data"), true),
            ["acme-account"] = new(Path.GetFullPath(configuration["Acme:AccountKeyPath"] ?? "certs/account.pem"), false),
            ["certificates"] = new(Path.GetFullPath(configuration["Acme:CertificatePath"] ?? "certs"), true),
            ["internal-ca"] = new(Path.GetFullPath(configuration["Acme:InternalCaPath"] ?? "certs/internal-ca"), true)
        };
        foreach (var cert in configuration.GetSection("WafInfos:Certs").GetChildren())
        {
            foreach (var field in new[] { "PemFile", "KeyFile" })
            {
                var file = cert[field];
                if (string.IsNullOrWhiteSpace(file)) continue;
                var identity = (cert["Host"] ?? "*") + "|" + field + "|" + Path.GetFileName(file);
                var name = "tls-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                roots[name] = new(Path.GetFullPath(file), false);
            }
        }
        return new(roots);
    }

    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        roots.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{Path.GetFullPath(pair.Value.Path)}:{pair.Value.Directory}")))));

    public Dictionary<string, string> Enumerate()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (name, root) in roots)
        {
            BackupArchive.EnsureNoLinks(root.Path);
            if (!root.Directory)
            {
                if (File.Exists(root.Path)) Add(name, root.Path);
                continue;
            }
            if (!Directory.Exists(root.Path)) continue;
            var pending = new Stack<string>(); pending.Push(root.Path);
            var directories = 0;
            while (pending.TryPop(out var directory))
            {
                if (++directories > 1000) throw new InvalidDataException("实例状态目录超过 1000 个");
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    BackupArchive.EnsureNoLinks(entry);
                    if (Directory.Exists(entry)) pending.Push(entry);
                    else Add(name + "." + Convert.ToHexString(Encoding.UTF8.GetBytes(Path.GetRelativePath(root.Path, entry).Replace('\\', '/'))), entry);
                }
            }
        }
        return result;

        void Add(string name, string path)
        {
            path = Path.GetFullPath(path);
            if (!seen.Add(path)) return;
            if (result.Count >= 1000 || name.Length > 512) throw new InvalidDataException("实例状态文件数量或相对路径长度超过限制");
            result.Add(name, path);
        }
    }

    public IReadOnlyDictionary<string, string> Resolve(BackupManifest manifest)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            var parts = file.Name.Split('.', 2);
            if (!roots.TryGetValue(parts[0], out var root)) throw new InvalidDataException("实例状态包含未知的逻辑根");
            var path = Path.GetFullPath(root.Path);
            if (root.Directory)
            {
                if (parts.Length != 2) throw new InvalidDataException("目录条目缺少相对文件名");
                string relative;
                try { relative = new UTF8Encoding(false, true).GetString(Convert.FromHexString(parts[1])); }
                catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { throw new InvalidDataException("相对文件名编码无效"); }
                if (relative.Length == 0 || relative.Contains('\\') || relative.Contains(':') || relative.Contains('\0') ||
                    relative.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')))
                    throw new InvalidDataException("实例状态相对文件名不安全");
                var resolved = Path.GetFullPath(Path.Combine(path, relative));
                if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("实例状态目标越过目录边界");
                path = resolved;
            }
            else if (parts.Length != 1) throw new InvalidDataException("文件根不接受子路径");
            BackupArchive.EnsureNoLinks(path);
            result.Add(file.Name, path);
        }
        // Existing files absent from the snapshot are removed by the archive transaction.
        // Reuse the snapshot's name for paths covered by overlapping configured roots.
        var paths = result.Values.ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (name, path) in Enumerate())
            if (paths.Add(path)) result.Add(name, path);
        return result;
    }
}
