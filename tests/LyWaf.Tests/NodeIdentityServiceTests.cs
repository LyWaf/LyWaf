using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeIdentityServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lywaf-identity-" + Guid.NewGuid().ToString("N"));
    private NodeCommunicationOptions Options(string node = "edge") => new()
    { Enabled = true, ClusterId = "cluster", NodeId = node, StateDirectory = _directory };

    [Fact]
    public void GeneratedIdentityIsDurableAndDoesNotDependOnMachineName()
    {
        var first = NodeIdentityService.Resolve(Options(""));
        var second = NodeIdentityService.Resolve(Options(""));
        Assert.Equal(first, second);
        Assert.StartsWith("node-", first.NodeId);
        Assert.NotEqual(Environment.MachineName, first.NodeId);
    }

    [Fact]
    public async Task RestartKeepsIdentityButChangesBootIdAndReleasesOwnership()
    {
        NodeHandshake before;
        using (var first = new NodeIdentityService(new StaticOptionsMonitor<NodeCommunicationOptions>(Options())))
        {
            await first.StartAsync(default);
            before = first.Capture();
            using var duplicate = new NodeIdentityService(new StaticOptionsMonitor<NodeCommunicationOptions>(Options()));
            await Assert.ThrowsAsync<IOException>(() => duplicate.StartAsync(default));
            await first.StopAsync(default);
            Assert.Throws<InvalidOperationException>(() => first.Capture());
        }
        using var restarted = new NodeIdentityService(new StaticOptionsMonitor<NodeCommunicationOptions>(Options()));
        await restarted.StartAsync(default);
        var after = restarted.Capture();
        Assert.Equal(before.NodeId, after.NodeId);
        Assert.NotEqual(before.BootId, after.BootId);
        Assert.Contains(2, after.ProtocolVersions);
    }

    [Fact]
    public async Task ConflictingIdentityAndLiveIdentityChangesAreRejected()
    {
        var config = Options();
        using var service = new NodeIdentityService(new StaticOptionsMonitor<NodeCommunicationOptions>(config));
        await service.StartAsync(default);
        config.NodeId = "another";
        Assert.Throws<InvalidOperationException>(() => service.Capture());
        await service.StopAsync(default);
        Assert.Throws<InvalidDataException>(() => NodeIdentityService.Resolve(config));
        config.NodeId = "edge";
        config.ClusterId = "another-cluster";
        Assert.Throws<InvalidDataException>(() => NodeIdentityService.Resolve(config));
        Assert.Equal("edge", NodeIdentityService.Resolve(Options()).NodeId);
    }

    [Fact]
    public void MissingSubsystemIdentitiesInheritPersistedCommunicationIdentity()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NodeCommunication:Enabled"] = "true", ["NodeCommunication:ClusterId"] = "cluster",
            ["NodeCommunication:StateDirectory"] = _directory
        }).Build();
        using var services = new ServiceCollection().AddLyWafDistribution(config).BuildServiceProvider();
        var id = services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue.NodeId;
        Assert.Equal(id, services.GetRequiredService<IOptionsMonitor<DistributionOptions>>().CurrentValue.NodeId);
        Assert.Equal(id, services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue.NodeId);
        Assert.Equal(id, NodeIdentityService.Resolve(Options("")).NodeId);
    }

    [Fact]
    public void CorruptIdentityIsNotRegenerated()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "identity.json"), "invalid");
        Assert.Throws<System.Text.Json.JsonException>(() => NodeIdentityService.Resolve(Options("")));
        Assert.Equal("invalid", File.ReadAllText(Path.Combine(_directory, "identity.json")));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
