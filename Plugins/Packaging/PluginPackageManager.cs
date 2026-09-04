using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Plugins.Core;
using Microsoft.Extensions.Options;

namespace LyWaf.Plugins.Packaging;

/// <summary>Defines the signed manifest stored as <c>plugin.json</c> in a .lyplugin archive.</summary>
public sealed class PluginPackageManifest
{
    /// <summary>Gets the stable plugin identifier.</summary>
    public required string Id { get; init; }
    /// <summary>Gets the semantic plugin version.</summary>
    public required string Version { get; init; }
    /// <summary>Gets the relative backend entry assembly path.</summary>
    public required string EntryAssembly { get; init; }
    /// <summary>Gets the SDK major version required by this package.</summary>
    public string SdkVersion { get; init; } = "1.0.0";
    /// <summary>Gets the persistent data schema version.</summary>
    public int DataVersion { get; init; }
    /// <summary>Gets the frontend asset version paired with the backend.</summary>
    public string? FrontendVersion { get; init; }
    /// <summary>Gets SHA-256 hashes keyed by normalized relative file path.</summary>
    public required Dictionary<string, string> Files { get; init; }
    /// <summary>Gets the trusted public-key identifier used for the signature.</summary>
    public string? SigningKeyId { get; init; }
    /// <summary>Gets the Base64 RSA-SHA256 signature over the canonical file-hash payload.</summary>
    public string? Signature { get; init; }
}

/// <summary>Describes the active package pointer used during startup and rollback.</summary>
/// <param name="Id">Stable plugin identifier.</param>
/// <param name="Version">Active backend package version.</param>
/// <param name="EntryAssembly">Relative entry assembly inside the active version directory.</param>
/// <param name="PreviousVersion">Version available for a one-step rollback.</param>
/// <param name="DataVersion">Persistent data schema expected by this version.</param>
/// <param name="FrontendVersion">Frontend assets paired with this backend.</param>
public sealed record PluginPackagePointer(string Id, string Version, string EntryAssembly,
    string? PreviousVersion, int DataVersion, string? FrontendVersion);

/// <summary>Reports an install or rollback result and whether host restart is required.</summary>
/// <param name="Id">Affected plugin identifier.</param>
/// <param name="Version">Version selected by the operation.</param>
/// <param name="RequiresRestart">Whether the host must restart before the selection is active.</param>
/// <param name="Message">Human-readable operation summary.</param>
public sealed record PluginPackageResult(string Id, string Version, bool RequiresRestart, string Message);

/// <summary>Installs verified plugin archives into immutable version directories and atomically switches the active pointer.</summary>
public sealed class PluginPackageManager(IOptionsMonitor<PluginOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    /// <summary>Streams, verifies and stages a package before atomically selecting its version.</summary>
    public async Task<PluginPackageResult> InstallAsync(Stream package, long? length, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (length > settings.MaxPackageBytes) throw new InvalidDataException("插件包超过大小限制。");
        var root = Path.GetFullPath(settings.PluginDirectory);
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archivePath = Path.Combine(staging, "package.lyplugin");
            await using (var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await CopyBoundedAsync(package, target, settings.MaxPackageBytes, cancellationToken);
            using var archive = ZipFile.OpenRead(archivePath);
            var manifestEntry = archive.GetEntry("plugin.json") ?? throw new InvalidDataException("插件包缺少 plugin.json。");
            if (manifestEntry.Length > 1024 * 1024) throw new InvalidDataException("插件清单超过 1 MiB 限制。");
            PluginPackageManifest manifest;
            await using (var stream = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<PluginPackageManifest>(stream, JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException("插件清单无效。");
            ValidateManifest(manifest);
            VerifySignature(manifest, settings);
            if (manifest.Files.Count > Math.Clamp(settings.MaxPackageFiles, 1, 10_000))
                throw new InvalidDataException("插件包文件数量超过限制。");
            if (manifest.Files.Keys.Any(path => Path.GetFileName(path).Equals("LyWaf.PluginSdk.dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("插件包不能重复携带宿主契约程序集 LyWaf.PluginSdk.dll。");
            var payload = Path.Combine(staging, "payload");
            Directory.CreateDirectory(payload);
            long extractedBytes = 0;
            foreach (var (relative, expectedHash) in manifest.Files)
            {
                var normalized = NormalizeRelative(relative);
                var entry = archive.GetEntry(normalized) ?? throw new InvalidDataException($"插件包缺少文件 {normalized}。");
                extractedBytes = checked(extractedBytes + entry.Length);
                if (extractedBytes > settings.MaxExtractedPackageBytes)
                    throw new InvalidDataException("插件包解压后大小超过限制。");
                var destination = SafeCombine(payload, normalized);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                using var sha = SHA256.Create();
                await using var hashing = new CryptoStream(output, sha, CryptoStreamMode.Write);
                await source.CopyToAsync(hashing, cancellationToken);
                await hashing.FlushFinalBlockAsync(cancellationToken);
                var actual = Convert.ToHexString(sha.Hash!);
                if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"文件 {normalized} 的 SHA-256 不匹配。");
            }
            if (!File.Exists(SafeCombine(payload, manifest.EntryAssembly)))
                throw new InvalidDataException("入口程序集不存在。");
            await File.WriteAllTextAsync(Path.Combine(payload, "plugin.json"),
                JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
            var pluginRoot = Path.Combine(root, manifest.Id);
            await InstallLock.WaitAsync(cancellationToken);
            try
            {
                var versionsRoot = Path.Combine(pluginRoot, "versions");
                Directory.CreateDirectory(versionsRoot);
                var versionPath = Path.Combine(versionsRoot, manifest.Version);
                if (Directory.Exists(versionPath)) throw new InvalidOperationException("该插件版本已经安装。");
                Directory.Move(payload, versionPath);
                var current = ReadPointer(pluginRoot);
                WritePointer(pluginRoot, new PluginPackagePointer(manifest.Id, manifest.Version, manifest.EntryAssembly,
                    current?.Version, manifest.DataVersion, manifest.FrontendVersion));
            }
            finally { InstallLock.Release(); }
            return new(manifest.Id, manifest.Version, true, "插件包已验证并安装，重启后加载新版本。");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    /// <summary>Atomically selects the previous installed version of a plugin.</summary>
    public PluginPackageResult Rollback(string pluginId)
    {
        InstallLock.Wait();
        try
        {
        var pluginRoot = Path.Combine(Path.GetFullPath(options.CurrentValue.PluginDirectory), ValidateSegment(pluginId));
        var current = ReadPointer(pluginRoot) ?? throw new InvalidOperationException("插件尚未通过包管理器安装。");
        if (string.IsNullOrWhiteSpace(current.PreviousVersion)) throw new InvalidOperationException("没有可回滚版本。");
        var previousRoot = Path.Combine(pluginRoot, "versions", ValidateSegment(current.PreviousVersion));
        var manifestPath = Path.Combine(previousRoot, "plugin.json");
        var previousManifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<PluginPackageManifest>(File.ReadAllText(manifestPath), JsonOptions)
            : null;
        var entry = previousManifest?.EntryAssembly ?? current.EntryAssembly;
        WritePointer(pluginRoot, new PluginPackagePointer(pluginId, current.PreviousVersion, entry,
            current.Version, previousManifest?.DataVersion ?? current.DataVersion, previousManifest?.FrontendVersion));
        return new(pluginId, current.PreviousVersion, true, "已切换到上一版本，重启后生效。");
        }
        finally { InstallLock.Release(); }
    }

    /// <summary>Reads the active package pointer from one plugin root directory.</summary>
    public static PluginPackagePointer? ReadPointer(string pluginRoot)
    {
        var path = Path.Combine(pluginRoot, "current.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<PluginPackagePointer>(File.ReadAllText(path), JsonOptions) : null;
    }

    /// <summary>Rejects a loaded assembly whose runtime metadata differs from its verified package manifest.</summary>
    public static void ValidateLoadedMetadata(string assemblyPath, PluginMetadata metadata)
    {
        var manifestPath = Path.Combine(Path.GetDirectoryName(assemblyPath)!, "plugin.json");
        if (!File.Exists(manifestPath)) return;
        var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("已安装插件清单无效。");
        if (!manifest.Id.Equals(metadata.Id, StringComparison.Ordinal)
            || !manifest.Version.Equals(metadata.Version, StringComparison.Ordinal)
            || manifest.DataVersion != metadata.DataVersion
            || !string.Equals(manifest.FrontendVersion, metadata.FrontendVersion, StringComparison.Ordinal))
            throw new InvalidDataException("插件 DLL 元数据与已签名包清单不一致。");
    }

    private static void WritePointer(string pluginRoot, PluginPackagePointer pointer)
    {
        Directory.CreateDirectory(pluginRoot);
        var path = Path.Combine(pluginRoot, "current.json");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(pointer, JsonOptions));
        File.Move(temporary, path, true);
    }

    private static void ValidateManifest(PluginPackageManifest manifest)
    {
        ValidateSegment(manifest.Id); ValidateSegment(manifest.Version);
        if (!Version.TryParse(manifest.SdkVersion, out var sdk) || sdk.Major != 1)
            throw new InvalidDataException("插件包 SDK 主版本与宿主不兼容。");
        NormalizeRelative(manifest.EntryAssembly);
        if (!manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("入口程序集必须是 DLL。");
        if (manifest.Files.Count == 0) throw new InvalidDataException("插件清单没有文件校验和。");
    }

    private static void VerifySignature(PluginPackageManifest manifest, PluginOptions settings)
    {
        if (string.IsNullOrWhiteSpace(manifest.Signature) || string.IsNullOrWhiteSpace(manifest.SigningKeyId))
        {
            if (settings.RequireSignedPackages) throw new CryptographicException("插件包缺少签名。");
            return;
        }
        if (!settings.TrustedSigningKeys.TryGetValue(manifest.SigningKeyId, out var pem))
            throw new CryptographicException("插件包签名密钥不受信任。");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var signedLines = new List<string>
        {
            $"id:{manifest.Id}", $"version:{manifest.Version}", $"entry:{NormalizeRelative(manifest.EntryAssembly)}",
            $"sdk:{manifest.SdkVersion}", $"data:{manifest.DataVersion}", $"frontend:{manifest.FrontendVersion ?? ""}"
        };
        signedLines.AddRange(manifest.Files.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"file:{NormalizeRelative(x.Key)}:{x.Value.ToUpperInvariant()}"));
        var payload = Encoding.UTF8.GetBytes(string.Join('\n', signedLines));
        if (!rsa.VerifyData(payload, Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("插件包签名无效。");
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken token)
    {
        var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token); if (read == 0) break;
            total += read; if (total > limit) throw new InvalidDataException("插件包超过大小限制。");
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static string SafeCombine(string root, string relative)
    {
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(prefix, NormalizeRelative(relative)));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件包路径越界。");
        return path;
    }

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').TrimStart('/') is var value
        && !string.IsNullOrWhiteSpace(value) && !value.Split('/').Any(part => part is ".." or "")
            ? value : throw new InvalidDataException("插件包包含非法路径。");

    private static string ValidateSegment(string value) => !string.IsNullOrWhiteSpace(value)
        && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
            ? value : throw new InvalidDataException("插件标识或版本格式无效。");
}
