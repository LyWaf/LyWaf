using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeIdentityConfigurationTests
{
    [Theory]
    [InlineData("active")]
    [InlineData("Approved")]
    [InlineData("")]
    public void InvalidAdmissionStateIsRejected(string state)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NodeCommunication:Enabled"] = "true",
            ["NodeCommunication:ClusterId"] = "cluster",
            ["NodeCommunication:NodeId"] = "node",
            ["NodeCommunication:Peers:peer:Keys:key"] = new string('k', 32),
            ["NodeCommunication:Peers:peer:AdmissionState"] = state
        }).Build();
        using var services = new ServiceCollection().AddLyWafDistribution(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue);
    }

    [Theory]
    [InlineData("HeartbeatIntervalSeconds", "0")]
    [InlineData("HeartbeatIntervalSeconds", "301")]
    [InlineData("SuspectFailureThreshold", "0")]
    [InlineData("OfflineTimeoutSeconds", "9")]
    [InlineData("OfflineTimeoutSeconds", "3601")]
    public void InvalidLifecycleThresholdIsRejected(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Distribution:Enabled"] = "true",
            [$"Distribution:{key}"] = value
        }).Build();
        using var services = new ServiceCollection().AddLyWafDistribution(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptionsMonitor<DistributionOptions>>().CurrentValue);
    }

    [Theory]
    [InlineData("configuration.commmit")]
    [InlineData("*")]
    [InlineData("Node.Status")]
    [InlineData("shell.execute")]
    public void UnknownPermissionIsRejectedAtStartup(string operation)
    {
        var values = new Dictionary<string, string?>
        {
            ["NodeCommunication:Enabled"] = "true",
            ["NodeCommunication:ClusterId"] = "cluster",
            ["NodeCommunication:NodeId"] = "node",
            ["NodeCommunication:Peers:peer:Keys:key"] = new string('k', 32),
            ["NodeCommunication:Peers:peer:AllowedOperations:0"] = operation
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var services = new ServiceCollection().AddLyWafDistribution(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue);
    }

    [Fact]
    public void SupportedOperationRegistryIsUniqueAndCaseSensitive()
    {
        Assert.Equal(NodeProtocolOperations.All.Count, NodeProtocolOperations.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(NodeProtocolOperations.All, operation => Assert.True(NodeProtocolOperations.IsSupported(operation)));
        Assert.False(NodeProtocolOperations.IsSupported("NODE.STATUS"));
    }

    [Theory]
    [InlineData("cluster-1", "edge_01", "peer.node", "key-2026")]
    [InlineData("c", "n", "p", "k")]
    public void ValidIdentitiesRemainUsable(string cluster, string node, string peer, string key)
    {
        var values = new Dictionary<string, string?>
        {
            ["NodeCommunication:Enabled"] = "true",
            ["NodeCommunication:ClusterId"] = cluster,
            ["NodeCommunication:NodeId"] = node,
            [$"NodeCommunication:Peers:{peer}:Keys:{key}"] = new string('k', 32)
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var services = new ServiceCollection().AddLyWafDistribution(configuration).BuildServiceProvider();
        var actual = services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
        Assert.Equal(node, actual.NodeId);
        Assert.Equal(cluster, actual.ClusterId);
        Assert.True(actual.Peers[peer].Keys.ContainsKey(key));
    }

    [Fact]
    public void IdentifierLengthBoundaryMatchesWireValidation()
    {
        Assert.True(NodeMessageAuthentication.ValidId(new string('a', 128)));
        Assert.False(NodeMessageAuthentication.ValidId(new string('a', 129)));
        Assert.False(NodeMessageAuthentication.ValidId(null));
        Assert.False(NodeMessageAuthentication.ValidId("node\nother"));
    }

    [Theory]
    [InlineData("ClusterId")]
    [InlineData("NodeId")]
    [InlineData("peer")]
    [InlineData("key")]
    public void InvalidIdentityIsRejectedByConfigurationValidation(string field)
    {
        var values = new Dictionary<string, string?>
        {
            ["NodeCommunication:Enabled"] = "true",
            ["NodeCommunication:ClusterId"] = field == "ClusterId" ? "bad cluster" : "cluster",
            ["NodeCommunication:NodeId"] = field == "NodeId" ? new string('n', 129) : "node",
            [$"NodeCommunication:Peers:{(field == "peer" ? "bad peer" : "peer")}:Keys:{(field == "key" ? "bad key" : "key")}"] = new string('k', 32)
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var services = new ServiceCollection().AddLyWafDistribution(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue);
    }
}
