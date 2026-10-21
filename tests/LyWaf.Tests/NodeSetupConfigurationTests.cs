using LyWaf.Services.Captcha;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSetupConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lywaf-setup-projection-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ControllerSetupProjectsIdentityCaptchaAndPeersWithoutPreservingLegacyRoutes()
    {
        var seed = new NodeSetupStore(root);
        await seed.ConfigureAsync("Controller", null, null, default);
        var first = await seed.CreateCredentialAsync("edge", default);
        var configuration = Config();
        var services = new ServiceCollection();
        var store = configuration.AddNodeSetup(services, root);
        services.AddOptions<NodeCommunicationOptions>().Bind(configuration.GetSection("NodeCommunication"));
        services.AddOptions<DistributionOptions>().Bind(configuration.GetSection("Distribution"));
        services.AddLyWafCaptcha(configuration);
        using var provider = services.BuildServiceProvider();
        var communication = provider.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
        Assert.True(communication.Enabled);
        Assert.Equal("Controller", communication.Mode);
        Assert.Equal("Controller", communication.Role);
        Assert.Equal(seed.Status().ClusterId, communication.ClusterId);
        Assert.Equal(seed.Status().NodeId, communication.NodeId);
        Assert.Equal(first.Peer.NodeId, Assert.Single(communication.Peers).Key);
        Assert.False(communication.AllowLegacyProtocol);
        var distribution = provider.GetRequiredService<IOptionsMonitor<DistributionOptions>>().CurrentValue;
        Assert.Equal("https://node-socket.invalid/", Assert.Single(distribution.Nodes).Value.Endpoint);
        Assert.Equal(communication.NodeId, distribution.NodeId);
        var captcha = provider.GetRequiredService<IOptions<CaptchaStoreOptions>>().Value;
        Assert.Equal("Authority", captcha.Provider);
        Assert.Equal(communication.NodeId, captcha.AuthorityNodeId);
        Assert.Equal(communication.ClusterId, captcha.ClusterId);
        Assert.StartsWith(root, captcha.StateDirectory);
        Assert.False(store.Status().RestartRequired);
    }

    [Fact]
    public async Task SavedNodeSetupOnlyNeedsOnePeerAndControllerAddress()
    {
        var controller = new NodeSetupStore(Path.Combine(root, "controller"));
        await controller.ConfigureAsync("Controller", null, null, default);
        var issued = await controller.CreateCredentialAsync("edge", default);
        var nodeRoot = Path.Combine(root, "node");
        await new NodeSetupStore(nodeRoot).ConfigureAsync("Node", "https://controller.example:7030/", issued.Credential, default);
        var configuration = Config();
        var services = new ServiceCollection();
        configuration.AddNodeSetup(services, nodeRoot);
        services.AddOptions<NodeCommunicationOptions>().Bind(configuration.GetSection("NodeCommunication"));
        services.AddOptions<DistributionOptions>().Bind(configuration.GetSection("Distribution"));
        using var provider = services.BuildServiceProvider();
        var communication = provider.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
        Assert.Equal("Node", communication.Mode);
        Assert.Equal(issued.Peer.NodeId, communication.NodeId);
        Assert.Equal(controller.Status().NodeId, communication.ControllerNodeId);
        Assert.Equal("https://controller.example:7030/", communication.ControllerUrl);
        Assert.Equal("Controller", Assert.Single(communication.Peers).Value.Role);
        Assert.Equal("https://controller.example:7030/", Assert.Single(provider.GetRequiredService<IOptionsMonitor<DistributionOptions>>()
            .CurrentValue.Nodes).Value.Endpoint);
    }

    [Fact]
    public async Task AddingAndRevokingPeersRefreshesBindingsWithoutChangingIdentity()
    {
        await new NodeSetupStore(root).ConfigureAsync("Controller", null, null, default);
        var configuration = Config();
        var services = new ServiceCollection();
        var store = configuration.AddNodeSetup(services, root);
        services.AddOptions<NodeCommunicationOptions>().Bind(configuration.GetSection("NodeCommunication"));
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>();
        var identity = monitor.CurrentValue.NodeId;
        Assert.Empty(monitor.CurrentValue.Peers);
        var issued = await store.CreateCredentialAsync("new", default);
        provider.GetRequiredService<IOptionsMonitorCache<NodeCommunicationOptions>>().TryRemove(Options.DefaultName);
        Assert.Equal(issued.Peer.NodeId, Assert.Single(monitor.CurrentValue.Peers).Key);
        await store.SetPeerEnabledAsync(issued.Peer.NodeId, false, default);
        provider.GetRequiredService<IOptionsMonitorCache<NodeCommunicationOptions>>().TryRemove(Options.DefaultName);
        Assert.False(monitor.CurrentValue.Peers[issued.Peer.NodeId].Enabled);
        Assert.Equal("Revoked", monitor.CurrentValue.Peers[issued.Peer.NodeId].AdmissionState);
        Assert.Equal(identity, monitor.CurrentValue.NodeId);
    }

    [Fact]
    public async Task ExplicitStandaloneDisablesOldDistributedConfiguration()
    {
        await new NodeSetupStore(root).ConfigureAsync("Standalone", null, null, default);
        var configuration = Config();
        configuration["NodeCommunication:Enabled"] = "true";
        configuration["RuntimeState:Enabled"] = "true";
        configuration["Distribution:Enabled"] = "true";
        var services = new ServiceCollection();
        configuration.AddNodeSetup(services, root);
        Assert.False(configuration.GetValue<bool>("NodeCommunication:Enabled"));
        Assert.False(configuration.GetValue<bool>("RuntimeState:Enabled"));
        Assert.False(configuration.GetValue<bool>("Distribution:Enabled"));
        Assert.Equal("Local", configuration["CaptchaStore:Provider"]);
    }

    [Fact]
    public void UnconfiguredInstancesKeepExistingConfigurationAndDoNotCreateFiles()
    {
        var configuration = Config();
        var expected = configuration.AsEnumerable().ToDictionary(pair => pair.Key, pair => pair.Value);
        configuration.AddNodeSetup(new ServiceCollection(), root);
        Assert.Equal(expected.OrderBy(pair => pair.Key), configuration.AsEnumerable().OrderBy(pair => pair.Key));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("Agent", "Node", true)]
    [InlineData("Node", "Controller", true)]
    [InlineData("Controller", "Node", true)]
    [InlineData("Standalone", "Node", true)]
    public void InvalidManagedModeCannotSilentlySelectLegacyTransport(string mode, string role, bool enabled)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NodeCommunication:Mode"] = mode, ["NodeCommunication:Role"] = role,
            ["NodeCommunication:Enabled"] = enabled.ToString(), ["NodeCommunication:ClusterId"] = "cluster",
            ["NodeCommunication:NodeId"] = "edge"
        });
        var services = new ServiceCollection();
        services.AddLyWafDistribution(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue);
    }

    private static ConfigurationManager Config()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NodeCommunication:Peers:old:Role"] = "Controller", ["NodeCommunication:NodeId"] = "old-identity",
            ["Distribution:Nodes:old:Endpoint"] = "http://legacy.invalid/", ["CaptchaStore:Provider"] = "Local"
        });
        return configuration;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
