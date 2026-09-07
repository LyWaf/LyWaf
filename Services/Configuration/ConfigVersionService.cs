using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Config;
using LyWaf.Shared;

namespace LyWaf.Services.Configuration;

/// <summary>
/// 描述一次成功写入的配置版本。内容单独存放，索引只保存审计字段和内容摘要。
/// </summary>
public sealed record ConfigVersionInfo(
    string Id,
    DateTimeOffset CreatedAt,
    string Operator,
    string Kind,
    string TargetPath,
    string ContentFile,
    string Sha256,
    string? PreviousVersionId,
    string Action);

/// <summary>
/// 表示两个配置版本之间的逐行差异，供控制台展示和回滚前确认。
/// </summary>
public sealed record ConfigVersionDiff(
    ConfigVersionInfo Version,
    ConfigVersionInfo? PreviousVersion,
    IReadOnlyList<ConfigDiffLine> Lines,
    bool Truncated);

/// <summary>
/// 表示配置差异中的一行；Kind 为 equal、add 或 remove。
/// </summary>
public sealed record ConfigDiffLine(string Kind, int? OldLine, int? NewLine, string Text);

/// <summary>
/// 集中管理主配置草稿和运行时补丁的版本化写入。
/// 服务先构建完整候选配置，再通过同目录临时文件替换目标，失败时不改变当前有效文件。
/// </summary>
public sealed class ConfigVersionService(IHttpContextAccessor httpContextAccessor)
{
    private const int MaxVersions = 100;
    private const int MaxDiffLines = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 获取全部配置版本，按创建时间倒序返回。
    /// </summary>
    public async Task<IReadOnlyList<ConfigVersionInfo>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadIndexAsync(cancellationToken)).OrderByDescending(x => x.CreatedAt).ToArray(); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 校验并原子写入主配置草稿，同时创建可回滚版本。
    /// </summary>
    public Task<ConfigVersionInfo> WriteDraftAsync(string content, string action, CancellationToken cancellationToken = default)
    {
        var originalPath = Path.GetFullPath(SharedData.ConfigFilePath);
        var directory = Path.GetDirectoryName(originalPath) ?? Directory.GetCurrentDirectory();
        var draftPath = Path.Combine(directory, $".lywaf.draft{Path.GetExtension(originalPath)}");
        return WriteAsync(draftPath, content, "main", action, ValidateMainConfig, cancellationToken);
    }

    /// <summary>
    /// 校验并原子写入指定主配置文件，同时创建可回滚版本。
    /// </summary>
    public Task<ConfigVersionInfo> WriteMainAsync(string path, string content, string action, CancellationToken cancellationToken = default)
        => WriteAsync(Path.GetFullPath(path), content, "main", action, ValidateMainConfig, cancellationToken);

    /// <summary>仅解析并验证主配置候选，不创建版本或修改磁盘文件。</summary>
    public void ValidateMainCandidate(string path, string content) => ValidateMainConfig(Path.GetFullPath(path), content);

    /// <summary>读取归档版本的完整配置内容；找不到版本时返回 null。</summary>
    public async Task<string?> GetVersionContentAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var version = (await ReadIndexAsync(cancellationToken)).FirstOrDefault(x => x.Id == id);
            return version == null ? null : await ReadVersionContentAsync(version, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 校验并原子写入 JSON 运行时补丁，同时创建可回滚版本。
    /// </summary>
    public Task<ConfigVersionInfo> WritePatchAsync(string path, string content, string action, CancellationToken cancellationToken = default)
        => WriteAsync(Path.GetFullPath(path), content, "patch", action, ValidatePatch, cancellationToken);

    /// <summary>
    /// 读取指定版本与同一目标上一版本之间的差异。
    /// </summary>
    public async Task<ConfigVersionDiff?> GetDiffAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var versions = await ReadIndexAsync(cancellationToken);
            var version = versions.FirstOrDefault(x => x.Id == id);
            if (version == null) return null;
            var previous = version.PreviousVersionId == null
                ? null : versions.FirstOrDefault(x => x.Id == version.PreviousVersionId);
            var current = await ReadVersionContentAsync(version, cancellationToken);
            var before = previous == null ? "" : await ReadVersionContentAsync(previous, cancellationToken);
            var (lines, truncated) = BuildDiff(before, current);
            return new(version, previous, lines, truncated);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 将历史内容作为一个新的版本原子写回原目标；历史记录本身保持不可变。
    /// </summary>
    public async Task<ConfigVersionInfo?> RollbackAsync(string id, CancellationToken cancellationToken = default)
    {
        ConfigVersionInfo? source;
        string content;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            source = (await ReadIndexAsync(cancellationToken)).FirstOrDefault(x => x.Id == id);
            if (source == null) return null;
            content = await ReadVersionContentAsync(source, cancellationToken);
        }
        finally { _gate.Release(); }

        return source.Kind == "patch"
            ? await WritePatchAsync(source.TargetPath, content, $"rollback:{source.Id}", cancellationToken)
            : await WriteAsync(source.TargetPath, content, "main", $"rollback:{source.Id}", ValidateMainConfig, cancellationToken);
    }

    private async Task<ConfigVersionInfo> WriteAsync(
        string targetPath,
        string content,
        string kind,
        string action,
        Action<string, string> validator,
        CancellationToken cancellationToken)
    {
        validator(targetPath, content);
        await _gate.WaitAsync(cancellationToken);
        var targetExisted = false;
        string? originalContent = null;
        string? archivedContentPath = null;
        try
        {
            targetExisted = File.Exists(targetPath);
            originalContent = targetExisted
                ? await File.ReadAllTextAsync(targetPath, cancellationToken)
                : null;
            var versions = await ReadIndexAsync(cancellationToken);
            var previous = versions.Where(x => PathEquals(x.TargetPath, targetPath))
                .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (previous == null && File.Exists(targetPath))
            {
                var existing = await File.ReadAllTextAsync(targetPath, cancellationToken);
                previous = await CreateArchivedVersionAsync(
                    versions, targetPath, existing, kind, "baseline", null, cancellationToken);
            }
            var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var info = new ConfigVersionInfo(id, DateTimeOffset.UtcNow, CurrentOperator(), kind,
                Path.GetFullPath(targetPath), id + ".config", Hash(content), previous?.Id, action);

            var historyDirectory = HistoryDirectory();
            Directory.CreateDirectory(historyDirectory);
            archivedContentPath = Path.Combine(historyDirectory, info.ContentFile);
            await AtomicWriteAsync(archivedContentPath, content, cancellationToken);
            versions.Add(info);
            await AtomicWriteAsync(targetPath, content, cancellationToken);
            try
            {
                await PruneAndWriteIndexAsync(versions, cancellationToken);
            }
            catch
            {
                if (targetExisted) await AtomicWriteAsync(targetPath, originalContent!, CancellationToken.None);
                else if (File.Exists(targetPath)) File.Delete(targetPath);
                throw;
            }
            return info;
        }
        catch
        {
            if (archivedContentPath is not null && File.Exists(archivedContentPath))
                File.Delete(archivedContentPath);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task<ConfigVersionInfo> CreateArchivedVersionAsync(
        List<ConfigVersionInfo> versions,
        string targetPath,
        string content,
        string kind,
        string action,
        string? previousVersionId,
        CancellationToken cancellationToken)
    {
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var info = new ConfigVersionInfo(id, DateTimeOffset.UtcNow, CurrentOperator(), kind,
            Path.GetFullPath(targetPath), id + ".config", Hash(content), previousVersionId, action);
        Directory.CreateDirectory(HistoryDirectory());
        await AtomicWriteAsync(Path.Combine(HistoryDirectory(), info.ContentFile), content, cancellationToken);
        versions.Add(info);
        return info;
    }

    private static void ValidateMainConfig(string path, string content)
    {
        if (path.EndsWith(".ly", StringComparison.OrdinalIgnoreCase))
        {
            var variables = Environment.GetEnvironmentVariables().Keys.Cast<object>()
                .ToDictionary(key => key.ToString()!, key => Environment.GetEnvironmentVariable(key.ToString()!) ?? "");
            var parsed = LyConfigParser.Parse(content, variables);
            _ = LyToAppSettingsConverter.TransformToAppSettings(parsed);
            return;
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        _ = YamlConfigurationFileParser.Parse(stream);
    }

    private static void ValidatePatch(string _, string content)
    {
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("配置补丁根节点必须是 JSON 对象");
    }

    private async Task PruneAndWriteIndexAsync(List<ConfigVersionInfo> versions, CancellationToken cancellationToken)
    {
        var ordered = versions.OrderByDescending(x => x.CreatedAt).ToList();
        var removedVersions = ordered.Skip(MaxVersions).ToArray();
        var retained = ordered.Take(MaxVersions).OrderBy(x => x.CreatedAt).ToList();
        await AtomicWriteAsync(IndexPath(), JsonSerializer.Serialize(retained, JsonOptions), cancellationToken);
        foreach (var removed in removedVersions)
        {
            var path = Path.Combine(HistoryDirectory(), removed.ContentFile);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            if (File.Exists(fullPath)) File.Move(temporary, fullPath, true);
            else File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<List<ConfigVersionInfo>> ReadIndexAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(IndexPath())) return [];
        await using var stream = File.OpenRead(IndexPath());
        return await JsonSerializer.DeserializeAsync<List<ConfigVersionInfo>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task<string> ReadVersionContentAsync(ConfigVersionInfo version, CancellationToken cancellationToken)
        => await File.ReadAllTextAsync(Path.Combine(HistoryDirectory(), version.ContentFile), cancellationToken);

    private static (IReadOnlyList<ConfigDiffLine>, bool) BuildDiff(string before, string after)
    {
        var oldLines = before.Replace("\r\n", "\n").Split('\n');
        var newLines = after.Replace("\r\n", "\n").Split('\n');
        var result = new List<ConfigDiffLine>(Math.Min(MaxDiffLines, oldLines.Length + newLines.Length));
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
            Add("equal", prefix + 1, prefix + 1, oldLines[prefix++]);
        var oldSuffix = oldLines.Length - 1;
        var newSuffix = newLines.Length - 1;
        while (oldSuffix >= prefix && newSuffix >= prefix && oldLines[oldSuffix] == newLines[newSuffix])
        { oldSuffix--; newSuffix--; }
        for (var index = prefix; index <= oldSuffix; index++) Add("remove", index + 1, null, oldLines[index]);
        for (var index = prefix; index <= newSuffix; index++) Add("add", null, index + 1, newLines[index]);
        for (var offset = 1; oldSuffix + offset < oldLines.Length; offset++)
            Add("equal", oldSuffix + offset + 1, newSuffix + offset + 1, oldLines[oldSuffix + offset]);
        return (result, result.Count >= MaxDiffLines);

        void Add(string kind, int? oldLine, int? newLine, string text)
        {
            if (result.Count < MaxDiffLines) result.Add(new(kind, oldLine, newLine, text));
        }
    }

    private string CurrentOperator()
        => httpContextAccessor.HttpContext?.Items["Username"]?.ToString() ?? "system";

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string HistoryDirectory()
    {
        var config = Path.GetFullPath(SharedData.ConfigFilePath);
        return Path.Combine(Path.GetDirectoryName(config) ?? Directory.GetCurrentDirectory(), ".lywaf-config-history");
    }

    private static string IndexPath() => Path.Combine(HistoryDirectory(), "index.json");
}
