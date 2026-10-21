using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSetupSynchronizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "node-sync-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NewControllerDefaultsToSynchronizationWithoutEditingRuntimeConfiguration()
    {
        await new NodeSetupStore(root).ConfigureAsync("Controller", null, null, default);
        using var provider = Project(root, false);
        var store = provider.GetRequiredService<NodeSetupStore>();
        var runtime = provider.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
        Assert.True(runtime.Enabled);
        Assert.True(store.Status().SynchronizeRuntimeState);
        Assert.True(store.Status().RuntimeSynchronizationConfigured);
        Assert.True(store.Status().RuntimeSynchronizationActive);
        Assert.EndsWith("runtime-shared", runtime.StateDirectory);
        Assert.NotEmpty(runtime.CcReleasePrivateKeyPem);
    }

    [Fact]
    public async Task ChangesStayPendingAndLocalHistoriesNeverMixIntoSharedHistories()
    {
        await new NodeSetupStore(root).ConfigureAsync("Controller", null, null, default);
        string shared;
        using (var provider = Project(root, false))
        {
            var store = provider.GetRequiredService<NodeSetupStore>();
            var monitor = provider.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>();
            shared = monitor.CurrentValue.StateDirectory;
            Directory.CreateDirectory(shared);
            File.WriteAllText(Path.Combine(shared, "sentinel"), "retained-shared-history");
            var changed = await store.ConfigureAsync("Controller", null, null, default, false);
            Assert.True(changed.RestartRequired);
            Assert.False(changed.SynchronizeRuntimeState);
            Assert.True(changed.RuntimeSynchronizationActive);
            Assert.True(changed.RuntimeHistoryChangePending);
            provider.GetRequiredService<IOptionsMonitorCache<RuntimeStateOptions>>().TryRemove(Options.DefaultName);
            Assert.True(monitor.CurrentValue.Enabled);
            Assert.Equal(shared, monitor.CurrentValue.StateDirectory);
        }
        using (var provider = Project(root, true))
        {
            var store = provider.GetRequiredService<NodeSetupStore>();
            var runtime = provider.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
            Assert.False(runtime.Enabled);
            Assert.False(store.Status().RuntimeSynchronizationActive);
            Assert.EndsWith("runtime-local", runtime.StateDirectory);
            Assert.NotEqual(shared, runtime.StateDirectory);
            Directory.CreateDirectory(runtime.StateDirectory);
            File.WriteAllText(Path.Combine(runtime.StateDirectory, "sentinel"), "isolated-local-history");
            await store.ConfigureAsync("Controller", null, null, default, true);
        }
        using var enabled = Project(root, false);
        var active = enabled.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
        Assert.True(active.Enabled);
        Assert.Equal(shared, active.StateDirectory);
        Assert.Equal("retained-shared-history", File.ReadAllText(Path.Combine(shared, "sentinel")));
        Assert.Equal("isolated-local-history", File.ReadAllText(Path.Combine(Path.GetDirectoryName(shared)!, "runtime-local", "sentinel")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyProfilesKeepTheirExistingFlagAndDirectoryUntilExplicitlyAdopted(bool oldFlag)
    {
        await new NodeSetupStore(root).ConfigureAsync("Controller", null, null, default);
        var seed = new NodeSetupStore(root).Active! with { SynchronizeRuntimeState = null };
        File.WriteAllText(Path.Combine(root, "setup.json"), JsonSerializer.Serialize(seed, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var provider = Project(root, oldFlag);
        var store = provider.GetRequiredService<NodeSetupStore>();
        var runtime = provider.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
        Assert.Equal(oldFlag, runtime.Enabled);
        Assert.EndsWith(Path.DirectorySeparatorChar + "runtime", runtime.StateDirectory);
        Assert.Equal(oldFlag, store.Status().SynchronizeRuntimeState);
        Assert.False(store.Status().RuntimeSynchronizationConfigured);
        var revision = store.Status().Revision;
        await store.ConfigureAsync("Controller", null, null, default);
        Assert.Equal(revision, store.Status().Revision);
        Assert.Null(new NodeSetupStore(root).Active!.SynchronizeRuntimeState);
    }

    [Fact]
    public async Task NewNodeWaitsForAuthorityMaterialBeforeEnablingSynchronization()
    {
        var controlRoot = Path.Combine(root, "controller");
        var control = new NodeSetupStore(controlRoot);
        await control.ConfigureAsync("Controller", null, null, default);
        var issued = await control.CreateCredentialAsync("node", default);
        var credential = NodeSetupStore.DecodeCredential(issued.Credential);
        var legacy = credential with { ReleasePublicKeyPem = "", ChallengeSecret = "", ReleaseTrustDomain = "" };
        var encoded = "lywaf_node_" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(legacy,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var nodeRoot = Path.Combine(root, "node");
        await new NodeSetupStore(nodeRoot).ConfigureAsync("Node", "https://controller.example/", encoded, default);
        using (var provider = Project(nodeRoot, true))
        {
            var store = provider.GetRequiredService<NodeSetupStore>();
            Assert.True(store.Status().SynchronizeRuntimeState);
            Assert.False(store.Status().RuntimeSynchronizationActive);
            var runtime = provider.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
            Assert.False(runtime.Enabled);
            Assert.EndsWith("runtime-local", runtime.StateDirectory);
            await store.ConfigureAsync("Node", "https://controller.example/", issued.Credential, default);
            Assert.False(store.Status().RuntimeSynchronizationActive);
        }
        using var upgraded = Project(nodeRoot, false);
        var active = upgraded.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
        Assert.True(active.Enabled);
        Assert.EndsWith("runtime-shared", active.StateDirectory);
        Assert.Empty(active.CcReleasePrivateKeyPem);
    }

    private static ServiceProvider Project(string directory, bool rawEnabled)
    {
        var configuration = new ConfigurationManager();
        configuration["RuntimeState:Enabled"] = rawEnabled.ToString();
        var services = new ServiceCollection();
        configuration.AddNodeSetup(services, directory);
        services.AddOptions<RuntimeStateOptions>().Bind(configuration.GetSection("RuntimeState"));
        return services.BuildServiceProvider();
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
