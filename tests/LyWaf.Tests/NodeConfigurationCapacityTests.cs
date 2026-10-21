using System.Security.Cryptography;
using System.Text;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeConfigurationCapacityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "node-capacity-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldPath = SharedData.ConfigFilePath;
    private readonly IConfigurationRoot _root;
    private readonly ConfigVersionService _versions = new(new HttpContextAccessor());
    private const string Original = "Plugins:\n  Enabled: true\n  Load: [local-plugin]\n  Permissions:\n    local-plugin: [Storage]\n  PluginConfigs:\n    local-plugin:\n      Value: local\nPluginNodeMessages:\n  Secret: local\nSpeedLimit:\n  Value: old\n";
    private string Prepared => Path.Combine(_directory, "prepared");
    private string History => Path.Combine(_directory, ".lywaf-config-history");
    private DistributionNodeService Node() => new(new StaticOptionsMonitor<DistributionOptions>(new()
        { NodeId = "edge", StateDirectory = _directory }), _versions, new ConfigurationLifecycleService(), _root);
    private static PrepareConfigurationRequest Request(string content, string id = "d") =>
        new(id, "v", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), content);

    public NodeConfigurationCapacityTests()
    {
        Directory.CreateDirectory(_directory);
        SharedData.ConfigFilePath = Path.Combine(_directory, "config.yaml");
        File.WriteAllText(SharedData.ConfigFilePath, Original);
        _root = new ConfigurationBuilder().AddYamlFile(SharedData.ConfigFilePath, false, false).Build();
    }

    private static void Fill(string directory, long length)
    {
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, "orphan.tmp"));
        stream.SetLength(length);
    }

    [Fact]
    public async Task PreparedCountsOrphanTemporaryBytesAndRestartCannotBypassCapacity()
    {
        Fill(Prepared, ConfigurationStorageBudget.PreparedBytes);
        Assert.False((await Node().PrepareAsync(Request("SpeedLimit:\n  Value: new\n"), default)).Ack);
        Assert.False((await Node().PrepareAsync(Request("SpeedLimit:\n  Value: new\n", "another"), default)).Ack);
        Assert.Equal(Original, File.ReadAllText(SharedData.ConfigFilePath));
        Assert.Single(Directory.GetFiles(Prepared));
    }

    [Fact]
    public async Task OversizedContentRejectedBeforeCandidateFiles()
    {
        Assert.False((await Node().PrepareAsync(Request("#" + new string('x', (int)ConfigurationStorageBudget.ContentBytes)), default)).Ack);
        Assert.False(Directory.Exists(Prepared));
    }

    [Fact]
    public async Task EmptyFilesConsumePreparedEntryBudgetWithoutDeletingFences()
    {
        Directory.CreateDirectory(Prepared);
        for (var i = 0; i < 4096; i++) File.WriteAllText(Path.Combine(Prepared, i + ".expired"), "");
        Assert.False((await Node().PrepareAsync(Request("SpeedLimit:\n  Value: new\n"), default)).Ack);
        Assert.Equal(4096, Directory.GetFiles(Prepared).Length);
    }

    [Fact]
    public async Task ConcurrentPreparedWritersKeepAtomicDirectoryQuota()
    {
        Fill(Prepared, ConfigurationStorageBudget.PreparedBytes - 2048);
        var nodes = Enumerable.Range(0, 8).Select(_ => Node()).ToArray();
        var results = await Task.WhenAll(nodes.Select((node, i) => node.PrepareAsync(
            Request("SpeedLimit:\n  Value: " + new string('x', 400) + "\n", "d" + i), default)));
        Assert.Contains(results, result => !result.Ack);
        Assert.True(Directory.GetFiles(Prepared).Sum(path => new FileInfo(path).Length) <= ConfigurationStorageBudget.PreparedBytes);
        Assert.Equal(Original, File.ReadAllText(SharedData.ConfigFilePath));
    }

    [Fact]
    public async Task HistoryFullRefusesCommitAndPreservesUnknownCandidateAndExistingPins()
    {
        var node = Node();
        Assert.True((await node.PrepareAsync(Request("SpeedLimit:\n  Value: new\n"), default)).Ack);
        Fill(History, ConfigurationStorageBudget.HistoryBytes);
        await Assert.ThrowsAsync<IOException>(() => node.CommitAsync("d", default));
        Assert.Equal(Original, File.ReadAllText(SharedData.ConfigFilePath));
        Assert.True(File.Exists(Path.Combine(Prepared, "d.json.overlay")));
        Assert.Null(await Node().ReconcileCommandAsync("configuration.commit", "d", null, default));
        Assert.Equal(0, await Node().CleanupExpiredCandidatesAsync(default));
        Assert.Equal(ConfigurationStorageBudget.HistoryBytes, new FileInfo(Path.Combine(History, "orphan.tmp")).Length);
    }

    [Fact]
    public async Task FullPreparedRefusesRollbackWithoutDeletingCompensation()
    {
        var node = Node();
        Assert.True((await node.PrepareAsync(Request("SpeedLimit:\n  Value: new\n"), default)).Ack);
        Assert.True((await node.CommitAsync("d", default)).Ack);
        var pins = File.ReadAllText(Path.Combine(History, "retention-pins.json"));
        var current = File.ReadAllText(SharedData.ConfigFilePath);
        Fill(Prepared, ConfigurationStorageBudget.PreparedBytes - Directory.GetFiles(Prepared).Sum(p => new FileInfo(p).Length));
        Assert.False((await node.RollbackAsync("d", default)).Ack);
        Assert.Equal("Applied", node.GetStatus().State);
        Assert.Equal(current, File.ReadAllText(SharedData.ConfigFilePath));
        Assert.Equal(pins, File.ReadAllText(Path.Combine(History, "retention-pins.json")));
    }

    [Fact]
    public async Task ConcurrentVersionWritersCannotOverrunHeadroomAndDoNotDeletePinnedEvidence()
    {
        var node = Node();
        Assert.True((await node.PrepareAsync(Request("SpeedLimit:\n  Value: new\n"), default)).Ack);
        Assert.True((await node.CommitAsync("d", default)).Ack);
        var pins = File.ReadAllText(Path.Combine(History, "retention-pins.json"));
        var used = Directory.GetFiles(History).Sum(p => new FileInfo(p).Length);
        Fill(History, ConfigurationStorageBudget.HistoryBytes - used - ConfigurationStorageBudget.IndexBytes - 1024 * 1024 - 100);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try { await new ConfigVersionService(new HttpContextAccessor()).WritePatchAsync(Path.Combine(_directory, "patch.json"), "{\"x\":1}", "test"); return true; }
            catch (IOException) { return false; }
        }));
        Assert.Contains(false, results);
        Assert.True(Directory.GetFiles(History).Sum(p => new FileInfo(p).Length) <= ConfigurationStorageBudget.HistoryBytes);
        Assert.Equal(pins, File.ReadAllText(Path.Combine(History, "retention-pins.json")));
        foreach (var version in await _versions.GetVersionsAsync()) Assert.True(File.Exists(Path.Combine(History, version.ContentFile)));
    }

    [Theory]
    [InlineData("SchemaVersion: 2")]
    [InlineData("SchemaVersion: 1\n  MinimumAppVersion: 9999.0")]
    [InlineData("SchemaVersion: 1\n  MaximumAppVersion: 0.0")]
    [InlineData("SchemaVersion: 1\n  MinimumAppVersion: not-a-version")]
    [InlineData("SchemaVersion: 1\n  UnknownRequirement: true")]
    public async Task UnsupportedCompatibilityIsRejectedBeforePersistence(string metadata)
    {
        var request = Request("DistributionCompatibility:\n  " + metadata + "\nSpeedLimit:\n  Value: new\n");
        Assert.False((await Node().PrepareAsync(request, default)).Ack);
        Assert.False(Directory.Exists(Prepared));
        Assert.Equal(Original, File.ReadAllText(SharedData.ConfigFilePath));
    }

    [Fact]
    public async Task CompatibleCandidateCannotChangePluginConfigurationThroughRealPrepareCommit()
    {
        var app = typeof(DistributionNodeService).Assembly.GetName().Version;
        var content = $"DistributionCompatibility:\n  SchemaVersion: 1\n  MinimumAppVersion: {app}\n  MaximumAppVersion: {app}\nPlugins:\n  Enabled: false\n  Load: [missing-plugin]\n  Permissions:\n    missing-plugin: [NodeMessaging]\n  PluginConfigs:\n    local-plugin:\n      Value: remote\nPluginNodeMessages:\n  Secret: remote\nSpeedLimit:\n  Value: new\n";
        var node = Node();
        Assert.True((await node.PrepareAsync(Request(content), default)).Ack);
        Assert.True((await node.CommitAsync("d", default)).Ack);
        using var result = NodeLocalConfigurationOverlay.ReadConfiguration(SharedData.ConfigFilePath, File.ReadAllText(SharedData.ConfigFilePath));
        Assert.Equal("true", result["Plugins:Enabled"], ignoreCase: true);
        Assert.Equal("local", result["PluginNodeMessages:Secret"]);
        Assert.Equal("local-plugin", result["Plugins:Load:0"]);
        Assert.Equal("Storage", result["Plugins:Permissions:local-plugin:0"]);
        Assert.Null(result["Plugins:Permissions:missing-plugin:0"]);
        Assert.Equal("local", result["Plugins:PluginConfigs:local-plugin:Value"]);
        Assert.Equal("new", result["SpeedLimit:Value"]);
        Assert.Null(result["DistributionCompatibility:SchemaVersion"]);
    }

    [Fact]
    public void LyCompatibilityAndPluginOverlayUseSameBoundary()
    {
        const string local = "Plugins { Enabled = true }\nSpeedLimit { Value = old }";
        const string remote = "DistributionCompatibility { SchemaVersion = 1 }\nPlugins { Enabled = false }\nSpeedLimit { Value = new }";
        NodeLocalConfigurationOverlay.ValidateCompatibility("config.ly", remote);
        using var result = NodeLocalConfigurationOverlay.ReadConfiguration("config.ly", NodeLocalConfigurationOverlay.Compose("config.ly", local, remote));
        Assert.Equal("true", result["Plugins:Enabled"], ignoreCase: true);
        Assert.Equal("new", result["SpeedLimit:Value"]);
        Assert.Throws<InvalidOperationException>(() => NodeLocalConfigurationOverlay.ValidateCompatibility("config.ly", "DistributionCompatibility { SchemaVersion = 2 }"));
    }

    public void Dispose()
    {
        (_root as IDisposable)?.Dispose();
        SharedData.ConfigFilePath = _oldPath;
        Directory.Delete(_directory, true);
    }
}
