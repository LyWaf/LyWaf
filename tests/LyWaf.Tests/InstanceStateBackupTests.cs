using System.Text;
using System.Text.Json;
using LyWaf.Services.Auth;
using LyWaf.Services.Backup;
using Xunit;

namespace LyWaf.Tests;

public sealed class InstanceStateBackupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "lywaf-instance-test-" + Guid.NewGuid().ToString("N"));
    private const string Password = "instance-backup-test-password";
    public InstanceStateBackupTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public async Task ColdBackupAndRestoreRoundTripRemovesOnlyExtraStateFiles()
    {
        var auth = Path.Combine(directory, "auth.json");
        var plugins = Path.Combine(directory, "plugins");
        Directory.CreateDirectory(Path.Combine(plugins, "nested"));
        var data = Path.Combine(plugins, "nested", "data.db");
        var extra = Path.Combine(plugins, "extra.db-wal");
        var outside = Path.Combine(directory, "unrelated.txt");
        await File.WriteAllTextAsync(outside, "leave-alone");
        await File.WriteAllTextAsync(auth, JsonSerializer.Serialize(new AuthConfig
        {
            Username = "admin", PasswordHash = new string('A', 64), JwtSecret = new string('S', 48)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await File.WriteAllBytesAsync(data, [0, 1, 2, 255]);
        var paths = new InstanceStatePaths(new Dictionary<string, InstanceStatePaths.Root>
        {
            ["auth"] = new(auth, false), ["plugins"] = new(plugins, true)
        });
        var id = "i-" + Guid.NewGuid().ToString("N");
        await InstanceStateCoordinator.PrepareAsync(directory, paths, id, Password, "operator", false, default);
        Assert.True(InstanceStateCoordinator.IsPending(directory));
        Assert.DoesNotContain(Password, await File.ReadAllTextAsync(Path.Combine(directory, ".lywaf.instance-pending")));
        InstanceStateCoordinator.ApplyBeforeStartup(directory, paths);
        Assert.False(InstanceStateCoordinator.IsPending(directory));
        var archive = Path.Combine(directory, ".lywaf.backups", id + ".lybackup");
        Assert.True(File.Exists(archive));
        await File.WriteAllBytesAsync(data, [9, 8, 7]);
        await File.WriteAllTextAsync(extra, "new-file");
        await InstanceStateCoordinator.PreflightAsync(archive, Password, paths, default);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(data));
        await InstanceStateCoordinator.PrepareAsync(directory, paths, id, Password, "operator", true, default);
        InstanceStateCoordinator.ApplyBeforeStartup(directory, paths);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, await File.ReadAllBytesAsync(data));
        Assert.False(File.Exists(extra));
        Assert.Equal("leave-alone", await File.ReadAllTextAsync(outside));
        Assert.False(InstanceStateCoordinator.IsPending(directory));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/outside")]
    [InlineData("sub/../../escape")]
    [InlineData("sub\\escape")]
    [InlineData("sub//escape")]
    public void RejectsEncodedTraversalBeforeCreatingTargets(string relative)
    {
        var paths = new InstanceStatePaths(new Dictionary<string, InstanceStatePaths.Root> { ["plugins"] = new(directory, true) });
        var name = "plugins." + Convert.ToHexString(Encoding.UTF8.GetBytes(relative));
        Assert.Throws<InvalidDataException>(() => paths.Resolve(new(1, "LyWaf", DateTimeOffset.UtcNow, [new(name, 0, "")] )));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task ChangedPathsKeepPendingRequestWithoutWritingData()
    {
        var paths = new InstanceStatePaths(new Dictionary<string, InstanceStatePaths.Root> { ["plugins"] = new(Path.Combine(directory, "first"), true) });
        await InstanceStateCoordinator.PrepareAsync(directory, paths, "i-" + Guid.NewGuid().ToString("N"), Password, "operator", false, default);
        var changed = new InstanceStatePaths(new Dictionary<string, InstanceStatePaths.Root> { ["plugins"] = new(Path.Combine(directory, "other"), true) });
        Assert.Throws<InvalidDataException>(() => InstanceStateCoordinator.ApplyBeforeStartup(directory, changed));
        Assert.True(InstanceStateCoordinator.IsPending(directory));
        Assert.False(Directory.Exists(Path.Combine(directory, "other")));
        InstanceStateCoordinator.Cancel(directory);
        Assert.False(InstanceStateCoordinator.IsPending(directory));
    }

    [Fact]
    public void RefusesWorkingDirectoryAsStateRoot()
        => Assert.Throws<InvalidDataException>(() => new InstanceStatePaths(new Dictionary<string, InstanceStatePaths.Root>
        { ["plugins"] = new(Directory.GetCurrentDirectory(), true) }));
}
