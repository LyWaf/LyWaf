using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeRollbackRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "node-rollback-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldPath = SharedData.ConfigFilePath;
    private readonly IConfigurationRoot _root;
    private readonly FailingSource _reload = new();
    private string StatePath => Path.Combine(_directory, "node-state.json");
    private string History => Path.Combine(_directory, ".lywaf-config-history");
    private string Pins => Path.Combine(History, "retention-pins.json");
    private string RollbackPath => Path.Combine(_directory, "prepared", "d.json.rollback");
    private static ConfigVersionService Versions() => new(new HttpContextAccessor());
    private DistributionNodeService Node() => new(new StaticOptionsMonitor<DistributionOptions>(new()
        { Enabled = true, Role = "Node", NodeId = "edge", StateDirectory = _directory }),
        Versions(), new ConfigurationLifecycleService(), _root);
    private NodeCommandInbox Inbox() => new(new StaticOptionsMonitor<NodeCommunicationOptions>(new() { StateDirectory = _directory }));
    private static string Content(string business, string secret = "local") =>
        $"NodeCommunication:\n  NodeId: edge\n  Secret: {secret}\nSpeedLimit:\n  Value: {business}\n";
    private static PrepareConfigurationRequest Request(string id = "d")
    {
        var content = Content("new", "remote-must-not-win");
        return new(id, "v", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), content);
    }
    private static NodeMessage Message() => new(2, "cluster", "controller", "edge", Guid.NewGuid().ToString("D"), "d",
        "configuration.rollback", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
        JsonSerializer.SerializeToElement(new DistributionCommandRequest("d")));

    public NodeRollbackRecoveryTests()
    {
        Directory.CreateDirectory(_directory);
        SharedData.ConfigFilePath = Path.Combine(_directory, "config.yaml");
        File.WriteAllText(SharedData.ConfigFilePath, Content("old"));
        _root = new ConfigurationBuilder().AddYamlFile(SharedData.ConfigFilePath, false, false).Add(_reload).Build();
    }

    private async Task<DistributionNodeService> Applied()
    {
        var node = Node();
        Assert.True((await node.PrepareAsync(Request(), default)).Ack);
        Assert.True((await node.CommitAsync("d", default)).Ack);
        return node;
    }

    [Theory]
    [InlineData("IntentPersisted", false)]
    [InlineData("ConfigurationWritten", true)]
    [InlineData("ConfigurationReloaded", true)]
    [InlineData("StatePersisted", true)]
    public async Task FaultAtRealRollbackBoundaryLeavesUnknownAndRestartOnlyReconciles(string boundary, bool canConfirm)
    {
        var node = await Applied();
        node.RollbackFaultPoint = stage => { if (stage == boundary) throw new IOException("injected rollback crash boundary"); };
        var message = Message();
        await Assert.ThrowsAsync<IOException>(() => Inbox().ExecuteAsync(message, () => node.RollbackAsync("d", default), default));
        var receipt = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "inbox"), "*.json"));
        Assert.Null(JsonNode.Parse(File.ReadAllText(receipt))!["Result"]);
        Assert.True(File.Exists(RollbackPath));
        var versionCount = (await Versions().GetVersionsAsync()).Count;
        var current = File.ReadAllText(SharedData.ConfigFilePath);
        var restarted = Node();
        var recovered = await Inbox().ExecuteAsync(message, () => throw new Exception("must not execute"), default,
            () => restarted.ReconcileCommandAsync(message.Type, "d", null, default), queryOnly: true);
        Assert.Equal(current, File.ReadAllText(SharedData.ConfigFilePath));
        Assert.Equal(versionCount, (await Versions().GetVersionsAsync()).Count);
        Assert.Equal(0, await restarted.CleanupExpiredCandidatesAsync(default));
        if (canConfirm)
        {
            Assert.True(recovered.Acknowledgement?.Ack);
            Assert.Equal("RolledBack", restarted.GetStatus().State);
            Assert.Equal("old", _root["SpeedLimit:Value"]);
            var replay = await Inbox().ExecuteAsync(message, () => throw new Exception("no replay"), default);
            Assert.True(replay.Acknowledgement?.Ack);
            Assert.Single(await Versions().GetVersionsAsync(), v => v.Action == "distribution-rollback:d");
        }
        else
        {
            Assert.Equal("node_outcome_unknown", recovered.Error?.Code);
            Assert.Equal("RollingBack", restarted.GetStatus().State);
            await Assert.ThrowsAsync<NodeConfigurationOutcomeUnknownException>(() => restarted.RollbackAsync("d", default));
            await Assert.ThrowsAsync<NodeConfigurationOutcomeUnknownException>(() => restarted.PrepareAsync(Request("another"), default));
            Assert.Equal("new", _root["SpeedLimit:Value"]);
        }
    }

    [Fact]
    public async Task ActualReloadFailureAfterWriteIsUnknownAndCanBeReconciledWhenReloadWorks()
    {
        var node = await Applied();
        node.RollbackFaultPoint = stage => { if (stage == "ConfigurationWritten") _reload.Provider.Fail = true; };
        var message = Message();
        await Assert.ThrowsAsync<IOException>(() => Inbox().ExecuteAsync(message, () => node.RollbackAsync("d", default), default));
        Assert.Equal("RollingBack", Node().GetStatus().State);
        Assert.Contains("old", File.ReadAllText(SharedData.ConfigFilePath));
        _reload.Provider.Fail = false;
        var recovered = await Inbox().ExecuteAsync(message, () => throw new Exception("must not execute"), default,
            () => Node().ReconcileCommandAsync(message.Type, "d", null, default), queryOnly: true);
        Assert.True(recovered.Acknowledgement?.Ack);
        Assert.Equal("old", _root["SpeedLimit:Value"]);
    }

    [Fact]
    public async Task CancellationAfterEffectKeepsUnknownAndReconciliationCreatesNoNewVersion()
    {
        var node = await Applied();
        using var stop = new CancellationTokenSource();
        node.RollbackFaultPoint = stage => { if (stage == "ConfigurationWritten") stop.Cancel(); };
        var message = Message();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Inbox().ExecuteAsync(message,
            () => node.RollbackAsync("d", stop.Token), stop.Token));
        Assert.Equal("RollingBack", Node().GetStatus().State);
        var recovered = await Inbox().ExecuteAsync(message, () => throw new Exception("no execute"), default,
            () => Node().ReconcileCommandAsync(message.Type, "d", null, default), queryOnly: true);
        Assert.True(recovered.Acknowledgement?.Ack);
        Assert.Single(await Versions().GetVersionsAsync(), v => v.Action == "distribution-rollback:d");
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("current")]
    [InlineData("archive")]
    [InlineData("baseline")]
    public async Task ChangedEvidenceAfterEffectCannotBeConvertedToAck(string changed)
    {
        var node = await Applied();
        node.RollbackFaultPoint = stage => { if (stage == "ConfigurationWritten") throw new IOException("after effect"); };
        await Assert.ThrowsAsync<IOException>(() => node.RollbackAsync("d", default));
        var archive = (await Versions().GetVersionsAsync()).First(v => v.Action == "distribution-rollback:d");
        var baseline = (await Versions().GetVersionsAsync()).First(v => v.Id == archive.PreviousVersionId);
        File.WriteAllText(changed switch
        {
            "candidate" => RollbackPath,
            "archive" => Path.Combine(History, archive.ContentFile),
            "baseline" => Path.Combine(History, baseline.ContentFile),
            _ => SharedData.ConfigFilePath
        }, Content("operator-change"));
        Assert.Null(await Node().ReconcileCommandAsync("configuration.rollback", "d", null, default));
        Assert.Equal("RollingBack", Node().GetStatus().State);
    }

    [Fact]
    public async Task LocalEditBeforePrepareBecomesActualCompensationBaseline()
    {
        await Versions().WriteMainAsync(SharedData.ConfigFilePath, Content("historical"), "old-admin-change");
        File.WriteAllText(SharedData.ConfigFilePath, Content("before-deployment", "rotated"));
        _root.Reload();
        await Applied();
        Assert.True((await Node().RollbackAsync("d", default)).Ack);
        Assert.Equal("before-deployment", _root["SpeedLimit:Value"]);
        Assert.Equal("rotated", _root["NodeCommunication:Secret"]);
    }

    [Fact]
    public async Task RotatedKeyAndBomBeforeRollbackRemainProvableAfterRestart()
    {
        var node = await Applied();
        File.WriteAllText(SharedData.ConfigFilePath, Content("new", "rotated"), new UTF8Encoding(true));
        node.RollbackFaultPoint = stage => { if (stage == "ConfigurationWritten") throw new IOException("after effect"); };
        await Assert.ThrowsAsync<IOException>(() => node.RollbackAsync("d", default));
        Assert.True((await Node().ReconcileCommandAsync("configuration.rollback", "d", null, default))?.Ack);
        Assert.Equal("old", _root["SpeedLimit:Value"]);
        Assert.Equal("rotated", _root["NodeCommunication:Secret"]);
    }

    [Fact]
    public async Task StateReplacementFailureAfterRollbackWritePreservesDurableIntent()
    {
        var node = await Applied();
        FileStream? blocked = null;
        node.RollbackFaultPoint = stage =>
        {
            if (stage != "ConfigurationReloaded") return;
            if (OperatingSystem.IsWindows()) blocked = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            else throw new IOException("state publication fault");
        };
        try
        {
            var error = await Record.ExceptionAsync(() => node.RollbackAsync("d", default));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        finally { blocked?.Dispose(); }
        Assert.True(JsonNode.Parse(File.ReadAllText(StatePath))!["RollbackStarted"]!.GetValue<bool>());
        Assert.Equal("RollingBack", Node().GetStatus().State);
        Assert.True((await Node().ReconcileCommandAsync("configuration.rollback", "d", null, default))?.Ack);
    }

    [Fact]
    public async Task LocalBusinessEditRejectsRollbackButLocalKeyRotationAndFormattingArePreserved()
    {
        await Applied();
        File.WriteAllText(SharedData.ConfigFilePath, Content("operator-change", "rotated"));
        Assert.False((await Node().RollbackAsync("d", default)).Ack);
        Assert.False(File.Exists(RollbackPath));
        Assert.Equal(Content("operator-change", "rotated"), File.ReadAllText(SharedData.ConfigFilePath));
        File.WriteAllText(SharedData.ConfigFilePath, "# local formatting\n" + Content("new", "rotated"));
        Assert.True((await Node().RollbackAsync("d", default)).Ack);
        Assert.Equal("rotated", _root["NodeCommunication:Secret"]);
        Assert.Equal("old", _root["SpeedLimit:Value"]);
    }

    [Fact]
    public async Task EditAfterIntentCannotBeOverwrittenAndRemainsUnknown()
    {
        var node = await Applied();
        node.RollbackFaultPoint = stage =>
        { if (stage == "IntentPersisted") File.WriteAllText(SharedData.ConfigFilePath, Content("operator-change")); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => node.RollbackAsync("d", default));
        Assert.Equal(Content("operator-change"), File.ReadAllText(SharedData.ConfigFilePath));
        Assert.Null(await Node().ReconcileCommandAsync("configuration.rollback", "d", null, default));
        Assert.Equal("RollingBack", Node().GetStatus().State);
    }

    [Fact]
    public async Task CandidateWriteFailureBeforeIntentHasNoEffectAndDoesNotPublishRollingBack()
    {
        var node = await Applied();
        var before = File.ReadAllText(SharedData.ConfigFilePath);
        Directory.CreateDirectory(RollbackPath);
        Assert.False((await node.RollbackAsync("d", default)).Ack);
        Assert.Equal("Applied", Node().GetStatus().State);
        Assert.Equal(before, File.ReadAllText(SharedData.ConfigFilePath));
        Directory.Delete(RollbackPath);
        Assert.True((await Node().RollbackAsync("d", default)).Ack);
    }

    [Fact]
    public async Task PinnedCompensationSurvivesRollingHistoryPruningAndServiceRestart()
    {
        await Applied();
        var original = (await Versions().GetVersionsAsync()).ToArray();
        var patches = Path.Combine(_directory, "patch.json");
        var versions = Versions();
        for (var i = 0; i < 105; i++) await versions.WritePatchAsync(patches, "{\"n\":" + i + "}", "unrelated");
        var retained = await Versions().GetVersionsAsync();
        foreach (var item in original)
        {
            Assert.Contains(retained, v => v.Id == item.Id);
            Assert.True(File.Exists(Path.Combine(History, item.ContentFile)));
        }
        Assert.True((await Node().RollbackAsync("d", default)).Ack);
        Assert.Equal("old", _root["SpeedLimit:Value"]);
        Assert.True(File.Exists(Pins));
    }

    [Fact]
    public async Task PinPersistenceFailurePreventsCommitEffect()
    {
        var node = Node();
        Assert.True((await node.PrepareAsync(Request(), default)).Ack);
        Directory.CreateDirectory(Pins);
        var error = await Record.ExceptionAsync(() => node.CommitAsync("d", default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("old", _root["SpeedLimit:Value"]);
        Assert.Contains("old", File.ReadAllText(SharedData.ConfigFilePath));
        Assert.Null(await Node().ReconcileCommandAsync("configuration.commit", "d", null, default));
    }

    [Fact]
    public async Task CorruptRetentionIndexFailsClosedWithoutLosingOriginalCompensation()
    {
        await Applied();
        var original = (await Versions().GetVersionsAsync()).ToArray();
        File.WriteAllText(Pins, "{");
        await Assert.ThrowsAsync<JsonException>(() => Versions().WritePatchAsync(Path.Combine(_directory, "patch.json"), "{}", "unrelated"));
        foreach (var version in original) Assert.True(File.Exists(Path.Combine(History, version.ContentFile)));
        Assert.False((await Node().RollbackAsync("d", default)).Ack);
        Assert.Equal("Applied", Node().GetStatus().State);
    }

    private sealed class FailingSource : IConfigurationSource
    {
        public FailingProvider Provider { get; } = new();
        public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
    }
    private sealed class FailingProvider : ConfigurationProvider
    {
        public bool Fail;
        public override void Load() { if (Fail) throw new IOException("injected configuration provider failure"); }
    }

    public void Dispose()
    {
        _reload.Provider.Fail = false;
        (_root as IDisposable)?.Dispose();
        SharedData.ConfigFilePath = _oldPath;
        Directory.Delete(_directory, true);
    }
}
