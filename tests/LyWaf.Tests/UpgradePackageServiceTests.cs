using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.Lifecycle;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

/// <summary>验证升级包边界检查和 current/previous/pending 版本指针转换。</summary>
public sealed class UpgradePackageServiceTests
{
    [Fact]
    public async Task CandidateProbeStartsOnIsolatedPortsAndBecomesReady()
    {
        var root = CreateTemporaryDirectory();
        Process? process = null;
        try
        {
            var configPath = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "lifecycle.yaml");
            var configuration = new ConfigurationBuilder().AddYamlFile(configPath, optional: false).Build();
            var service = CreateService(root, readinessTimeoutSeconds: 15);
            var entryPoint = typeof(Program).Assembly.Location;
            var release = new UpgradeReleaseInfo("probe", Path.GetDirectoryName(entryPoint)!, entryPoint,
                "test", DateTimeOffset.UtcNow, "validated", "test");

            var probe = await service.StartReadinessProbeAsync(release, configuration, configPath);
            process = Process.GetProcessById(probe.ProcessId);
            Assert.False(process.HasExited);
            Assert.InRange(probe.ControlPort, 1, 65535);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StageRejectsZipEntryOutsideReleaseDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var inbox = Path.Combine(root, "inbox");
            Directory.CreateDirectory(inbox);
            var package = Path.Combine(inbox, "evil.zip");
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../outside.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("escape");
            }
            await using var packageStream = File.OpenRead(package);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
            var service = CreateService(root);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.StageAsync("evil.zip", "v2", hash, Path.Combine(root, "config.yaml")));
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CommitAndRollbackKeepVersionAndEntryPointTogether()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var releasesDirectory = Path.Combine(root, "releases");
            var candidateDirectory = Path.Combine(releasesDirectory, "v2");
            Directory.CreateDirectory(candidateDirectory);
            var candidateEntry = Path.Combine(candidateDirectory, "LyWaf.dll");
            var runningEntry = Path.Combine(root, "LyWaf-v1.dll");
            await File.WriteAllTextAsync(candidateEntry, "candidate");
            await File.WriteAllTextAsync(runningEntry, "running");
            var release = new UpgradeReleaseInfo("v2", candidateDirectory, candidateEntry, new string('A', 64),
                DateTimeOffset.UtcNow, "validated", "ok");
            await File.WriteAllTextAsync(Path.Combine(releasesDirectory, "releases.json"), JsonSerializer.Serialize(new[] { release }));
            var service = CreateService(root);

            var pending = await service.MarkPendingAsync("v2", "v1", runningEntry);
            Assert.Equal("v1", pending.Previous);
            Assert.Equal(runningEntry, pending.PreviousEntryPoint);
            var committed = await service.CommitAsync("v2");
            Assert.Equal(candidateEntry, committed.CurrentEntryPoint);

            var (rollbackPending, rollbackEntry) = await service.MarkPreviousPendingAsync();
            Assert.Equal("v1", rollbackPending.Pending);
            Assert.Equal(runningEntry, rollbackEntry);
            var aborted = await service.AbortPendingAsync();
            Assert.Equal("v1", aborted.Previous);
            Assert.Equal(runningEntry, aborted.PreviousEntryPoint);

            (rollbackPending, rollbackEntry) = await service.MarkPreviousPendingAsync();
            var rolledBack = await service.CommitAsync("v1");
            Assert.Equal("v1", rolledBack.Current);
            Assert.Equal(runningEntry, rolledBack.CurrentEntryPoint);
            Assert.Equal("v2", rolledBack.Previous);
        }
        finally { Directory.Delete(root, true); }
    }

    private static UpgradePackageService CreateService(string root, int readinessTimeoutSeconds = 5) => new(Options.Create(new UpgradeOptions
    {
        InboxDirectory = Path.Combine(root, "inbox"),
        ReleaseDirectory = Path.Combine(root, "releases"),
        MaxPackageBytes = 1024 * 1024,
        MaxExtractedBytes = 1024 * 1024,
        MaxEntries = 10,
        ReadinessTimeoutSeconds = readinessTimeoutSeconds
    }));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "lywaf-upgrade-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LyWaf.csproj"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位 LyWaf 仓库根目录");
    }
}
