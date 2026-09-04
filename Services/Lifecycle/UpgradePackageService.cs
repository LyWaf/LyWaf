using System.Diagnostics;
using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Lifecycle;

/// <summary>控制升级包收件箱、版本仓库和候选校验的安全限制。</summary>
public sealed class UpgradeOptions
{
    public string InboxDirectory { get; set; } = "upgrades/inbox";
    public string ReleaseDirectory { get; set; } = "upgrades/releases";
    public long MaxPackageBytes { get; set; } = 512L * 1024 * 1024;
    public long MaxExtractedBytes { get; set; } = 1024L * 1024 * 1024;
    public int MaxEntries { get; set; } = 20_000;
    public int ValidationTimeoutSeconds { get; set; } = 30;
    public int ReadinessTimeoutSeconds { get; set; } = 30;
}

/// <summary>描述一个已校验并可供激活的 LyWaf 程序版本。</summary>
public sealed record UpgradeReleaseInfo(string Version, string Directory, string EntryPoint, string Sha256,
    DateTimeOffset StagedAt, string Status, string ValidationOutput);

/// <summary>持久化当前、上一和待激活版本指针，供进程交接和故障回退使用。</summary>
public sealed record UpgradePointers(string? Current, string? Previous, string? Pending,
    string? CurrentEntryPoint, string? PreviousEntryPoint, string? PendingEntryPoint, bool PendingIsRollback = false);

/// <summary>表示已在隔离端口达到就绪状态并保持运行的候选探针进程。</summary>
public sealed record UpgradeProbeResult(int ProcessId, int ControlPort);

/// <summary>从受限收件箱流式校验、解压和验证升级包，并维护不可变版本目录与版本指针。</summary>
public sealed class UpgradePackageService(IOptions<UpgradeOptions> options)
{
    private static readonly Regex VersionPattern = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly UpgradeOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>列出所有已完成候选校验的版本。</summary>
    public async Task<IReadOnlyList<UpgradeReleaseInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadReleasesAsync(cancellationToken)).OrderByDescending(x => x.StagedAt).ToArray(); }
        finally { _gate.Release(); }
    }

    /// <summary>读取持久化版本指针。</summary>
    public async Task<UpgradePointers> GetPointersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadPointersAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    /// <summary>在隔离回环端口启动候选版本，直到其就绪或失败；成功后探针保持运行等待交接。</summary>
    public async Task<UpgradeProbeResult> StartReadinessProbeAsync(UpgradeReleaseInfo release,
        IConfiguration configuration, string configPath, CancellationToken cancellationToken = default)
    {
        var listenCount = configuration.GetSection("WafInfos:Listens").GetChildren().Count();
        var ports = Enumerable.Range(0, listenCount + 1).Select(_ => ReserveFreePort()).ToArray();
        var controlPort = ports[^1];
        var start = CreateRunProcess(release.EntryPoint, configPath);
        for (var index = 0; index < listenCount; index++)
        {
            start.Environment[$"WafInfos__Listens__{index}__Host"] = "127.0.0.1";
            start.Environment[$"WafInfos__Listens__{index}__Port"] = ports[index].ToString();
            start.Environment[$"WafInfos__Listens__{index}__IsHttps"] = "false";
        }
        start.Environment["WafInfos__ControlListen__Host"] = "127.0.0.1";
        start.Environment["WafInfos__ControlListen__Port"] = controlPort.ToString();
        start.Environment["WafInfos__ControlListen__IsHttps"] = "false";
        start.Environment["Plugins__Enabled"] = "false";
        start.Environment["DuckDb__Enabled"] = "false";
        start.Environment["Acme__Enabled"] = "false";
        start.Environment["ProxyServer__Enabled"] = "false";
        start.Environment["StreamServer__Enabled"] = "false";
        start.Environment["OpenTelemetry__Enabled"] = "false";
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add(Path.Combine(Path.GetTempPath(), $"lywaf-upgrade-probe-{Guid.NewGuid():N}.pid"));
        var process = Process.Start(start) ?? throw new InvalidOperationException("候选探针进程未启动");
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(_options.ReadinessTimeoutSeconds, 1, 300));
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"候选探针提前退出，退出码 {process.ExitCode}");
                try
                {
                    using var response = await client.GetAsync($"http://127.0.0.1:{controlPort}/api/health/ready", cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        var result = new UpgradeProbeResult(process.Id, controlPort);
                        process.Dispose();
                        return result;
                    }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                await Task.Delay(250, cancellationToken);
            }
            throw new TimeoutException("候选探针未在限制时间内就绪");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            process.Dispose();
            throw;
        }
    }

    /// <summary>校验收件箱 ZIP 的摘要并流式解压，然后调用候选程序验证当前配置。</summary>
    public async Task<UpgradeReleaseInfo> StageAsync(string packageName, string version, string expectedSha256,
        string configPath, CancellationToken cancellationToken = default)
    {
        ValidateVersion(version);
        if (!Regex.IsMatch(expectedSha256, "^[A-Fa-f0-9]{64}$"))
            throw new InvalidDataException("SHA-256 必须是 64 位十六进制字符串");
        var inbox = EnsureDirectory(_options.InboxDirectory);
        var packagePath = ResolveChild(inbox, packageName);
        if (!File.Exists(packagePath) || !packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("升级包不存在或不是 ZIP 文件", packageName);
        var packageLength = new FileInfo(packagePath).Length;
        if (packageLength <= 0 || packageLength > _options.MaxPackageBytes)
            throw new InvalidDataException($"升级包大小超出限制: {packageLength}");
        var actualSha256 = await ComputeSha256Async(packagePath, cancellationToken);
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("升级包 SHA-256 不匹配");

        await _gate.WaitAsync(cancellationToken);
        string? stagingDirectory = null;
        try
        {
            var releaseRoot = EnsureDirectory(_options.ReleaseDirectory);
            var finalDirectory = ResolveChild(releaseRoot, version);
            if (Directory.Exists(finalDirectory)) throw new InvalidOperationException($"版本已存在: {version}");
            stagingDirectory = ResolveChild(releaseRoot, ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            await ExtractSecurelyAsync(packagePath, stagingDirectory, cancellationToken);
            var validation = await ValidateCandidateAsync(FindEntryPoint(stagingDirectory), Path.GetFullPath(configPath), cancellationToken);
            Directory.Move(stagingDirectory, finalDirectory);
            stagingDirectory = null;
            var info = new UpgradeReleaseInfo(version, finalDirectory, FindEntryPoint(finalDirectory), actualSha256,
                DateTimeOffset.UtcNow, "validated", validation);
            var releases = await ReadReleasesAsync(cancellationToken);
            releases.Add(info);
            await AtomicWriteAsync(ReleasesIndexPath(), JsonSerializer.Serialize(releases, JsonOptions), cancellationToken);
            return info;
        }
        finally
        {
            _gate.Release();
            if (stagingDirectory != null && Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        }
    }

    /// <summary>将已验证版本标记为待激活，同时保留当前版本作为回退目标。</summary>
    public async Task<UpgradePointers> MarkPendingAsync(string version, string runningVersion, string runningEntryPoint,
        CancellationToken cancellationToken = default)
    {
        ValidateVersion(version);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var release = (await ReadReleasesAsync(cancellationToken)).SingleOrDefault(x => x.Version == version)
                ?? throw new KeyNotFoundException($"升级版本不存在: {version}");
            if (!Directory.Exists(release.Directory) || !File.Exists(release.EntryPoint))
                throw new InvalidDataException("升级版本文件不完整");
            var current = await ReadPointersAsync(cancellationToken);
            var next = current with
            {
                Previous = current.Current ?? runningVersion,
                PreviousEntryPoint = current.CurrentEntryPoint ?? Path.GetFullPath(runningEntryPoint),
                Pending = version,
                PendingEntryPoint = release.EntryPoint,
                PendingIsRollback = false
            };
            await AtomicWriteAsync(PointersPath(), JsonSerializer.Serialize(next, JsonOptions), cancellationToken);
            return next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>候选就绪后提交待激活指针为当前版本。</summary>
    public async Task<UpgradePointers> CommitAsync(string version, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadPointersAsync(cancellationToken);
            if (current.Pending != version) throw new InvalidOperationException("该版本不是当前待激活版本");
            var next = current with
            {
                Current = version,
                CurrentEntryPoint = current.PendingEntryPoint,
                Pending = null,
                PendingEntryPoint = null,
                PendingIsRollback = false
            };
            await AtomicWriteAsync(PointersPath(), JsonSerializer.Serialize(next, JsonOptions), cancellationToken);
            return next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>候选启动失败时清除待激活版本并保留上一有效指针。</summary>
    public async Task<UpgradePointers> AbortPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadPointersAsync(cancellationToken);
            var next = current.PendingIsRollback
                ? current with
                {
                    Previous = current.Pending,
                    PreviousEntryPoint = current.PendingEntryPoint,
                    Pending = null,
                    PendingEntryPoint = null,
                    PendingIsRollback = false
                }
                : current with { Pending = null, PendingEntryPoint = null, PendingIsRollback = false };
            await AtomicWriteAsync(PointersPath(), JsonSerializer.Serialize(next, JsonOptions), cancellationToken);
            return next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>把上一有效版本设置为待激活版本，并返回其入口点。</summary>
    public async Task<(UpgradePointers Pointers, string EntryPoint)> MarkPreviousPendingAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadPointersAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(current.Previous) || string.IsNullOrWhiteSpace(current.PreviousEntryPoint) ||
                !File.Exists(current.PreviousEntryPoint))
                throw new InvalidOperationException("没有可用的上一程序版本");
            var next = current with
            {
                Pending = current.Previous,
                PendingEntryPoint = current.PreviousEntryPoint,
                Previous = current.Current,
                PreviousEntryPoint = current.CurrentEntryPoint,
                PendingIsRollback = true
            };
            await AtomicWriteAsync(PointersPath(), JsonSerializer.Serialize(next, JsonOptions), cancellationToken);
            return (next, current.PreviousEntryPoint);
        }
        finally { _gate.Release(); }
    }

    private async Task ExtractSecurelyAsync(string packagePath, string destination, CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > _options.MaxEntries) throw new InvalidDataException("升级包文件数量超出限制");
        long total = 0;
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"升级包不允许符号链接: {entry.FullName}");
            var target = ResolveChild(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (!paths.Add(target)) throw new InvalidDataException($"升级包包含重复路径: {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    total = checked(total + read);
                    if (total > _options.MaxExtractedBytes) throw new InvalidDataException("升级包解压总量超出限制");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }

    private async Task<string> ValidateCandidateAsync(string entryPoint, string configPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { WorkingDirectory = Path.GetDirectoryName(entryPoint)!, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (entryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { start.FileName = "dotnet"; start.ArgumentList.Add(entryPoint); }
        else start.FileName = entryPoint;
        start.ArgumentList.Add("validate"); start.ArgumentList.Add("--config"); start.ArgumentList.Add(configPath);
        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendBounded(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendBounded(output, e.Data);
        if (!process.Start()) throw new InvalidOperationException("无法启动候选程序验证进程");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.ValidationTimeoutSeconds, 1, 300)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("候选程序配置验证超时");
        }
        if (process.ExitCode != 0) throw new InvalidDataException($"候选程序配置验证失败: {output}");
        return output.ToString();
    }

    private static ProcessStartInfo CreateRunProcess(string entryPoint, string configPath)
    {
        var start = new ProcessStartInfo { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false, CreateNoWindow = true };
        if (entryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { start.FileName = "dotnet"; start.ArgumentList.Add(entryPoint); }
        else start.FileName = entryPoint;
        start.ArgumentList.Add("run"); start.ArgumentList.Add("--config"); start.ArgumentList.Add(Path.GetFullPath(configPath));
        return start;
    }

    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void AppendBounded(StringBuilder output, string? line)
    {
        if (line == null || output.Length >= 64 * 1024) return;
        var remaining = 64 * 1024 - output.Length;
        output.AppendLine(line.Length <= remaining ? line : line[..remaining]);
    }

    private static string FindEntryPoint(string directory)
    {
        var executables = Directory.EnumerateFiles(directory, "LyWaf.exe", SearchOption.AllDirectories).ToArray();
        if (executables.Length == 1) return executables[0];
        if (executables.Length > 1) throw new InvalidDataException("升级包包含多个 LyWaf.exe");
        var assemblies = Directory.EnumerateFiles(directory, "LyWaf.dll", SearchOption.AllDirectories).ToArray();
        return assemblies.Length == 1 ? assemblies[0]
            : throw new InvalidDataException($"升级包必须包含一个 LyWaf 入口点，实际为 {assemblies.Length} 个");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private async Task<List<UpgradeReleaseInfo>> ReadReleasesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ReleasesIndexPath())) return [];
        await using var stream = File.OpenRead(ReleasesIndexPath());
        return await JsonSerializer.DeserializeAsync<List<UpgradeReleaseInfo>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task<UpgradePointers> ReadPointersAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PointersPath())) return new(null, null, null, null, null, null);
        await using var stream = File.OpenRead(PointersPath());
        return await JsonSerializer.DeserializeAsync<UpgradePointers>(stream, JsonOptions, cancellationToken) ?? new(null, null, null, null, null, null);
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
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
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string ReleasesIndexPath() => Path.Combine(EnsureDirectory(_options.ReleaseDirectory), "releases.json");
    private string PointersPath() => Path.Combine(EnsureDirectory(_options.ReleaseDirectory), "pointers.json");
    private static string EnsureDirectory(string path) { var full = Path.GetFullPath(path); Directory.CreateDirectory(full); return full; }
    private static void ValidateVersion(string version)
    {
        if (!VersionPattern.IsMatch(version)) throw new InvalidDataException("版本号只能包含字母、数字、点、下划线和短横线");
    }
    private static string ResolveChild(string root, string child)
    {
        if (Path.IsPathRooted(child)) throw new InvalidDataException("不允许绝对路径");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, child));
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("路径超出允许目录");
        return full;
    }
}
