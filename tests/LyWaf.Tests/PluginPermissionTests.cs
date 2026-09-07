using LyWaf.Plugins.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class PluginPermissionTests
{
    [Fact]
    public void ExternalPluginRequiresEveryRequestedPermission()
    {
        var options = new PluginOptions
        {
            Permissions = { ["sample"] = PluginPermission.Network | PluginPermission.DataDirectory }
        };
        var manager = new PluginManager(new ConfigurationBuilder().Build(), new PluginEventBus(),
            new StaticOptionsMonitor<PluginOptions>(options));

        Assert.True(manager.IsPermissionGranted("sample", PluginPermission.Network));
        Assert.True(manager.IsPermissionGranted("sample", PluginPermission.Network | PluginPermission.DataDirectory));
        Assert.False(manager.IsPermissionGranted("sample", PluginPermission.ConsoleApi));
        Assert.False(manager.IsPermissionGranted("sample", PluginPermission.Network | PluginPermission.ConsoleApi));
    }

    [Fact]
    public void StorageRejectsAbsoluteAndEscapingPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-plugin-storage-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new PluginStorage(root, true);
            Assert.Throws<ArgumentException>(() => storage.OpenRead(Path.GetFullPath("outside.txt")));
            Assert.Throws<UnauthorizedAccessException>(() => storage.OpenRead("../outside.txt"));
            using (var stream = storage.OpenWrite("nested/value.bin")) stream.WriteByte(42);
            using var read = storage.OpenRead("nested/value.bin");
            Assert.Equal(42, read.ReadByte());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void StorageRequiresExplicitGrant()
    {
        var storage = new PluginStorage(Path.GetTempPath(), false);
        Assert.Throws<UnauthorizedAccessException>(() => storage.Exists("value.bin"));
    }

    [Fact]
    public void SystemPluginReceivesHostCapabilities()
    {
        var manager = new PluginManager(new ConfigurationBuilder().Build(), new PluginEventBus(),
            new StaticOptionsMonitor<PluginOptions>(new()));

        Assert.True(manager.IsPermissionGranted("web-tamper", PluginPermission.All));
    }
}
