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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeConfigurationRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "node-retention-" + Guid.NewGuid().ToString("N"));
    private readonly string _oldPath = SharedData.ConfigFilePath;
    private readonly IConfigurationRoot _root;
    private readonly ConfigVersionService _versions = new(new HttpContextAccessor());
    private string StatePath => Path.Combine(_directory, "node-state.json");
    private string CandidatePath => Path.Combine(_directory, "prepared", "d.json");
    private StaticOptionsMonitor<DistributionOptions> Options => new(new() { Enabled = true, Role = "Node", NodeId = "edge", StateDirectory = _directory });
    private StaticOptionsMonitor<NodeCommunicationOptions> Protocol => new(new() { Enabled = true, Role = "Node", StateDirectory = _directory });
    private DistributionNodeService Node() => new(Options, _versions, new ConfigurationLifecycleService(), _root);
    private NodeCommandInbox Inbox() => new(Protocol);
    private static PrepareConfigurationRequest Request(string id = "d")
    {
        const string content = "SpeedLimit:\n  Value: new\n";
        return new(id, "v", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), content);
    }
    private static NodeMessage Message() => new(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "d",
        "configuration.commit", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), JsonSerializer.SerializeToElement(new { deploymentId = "d" }));
    private static Task<NodeAcknowledgement> Ack() => Task.FromResult(new NodeAcknowledgement(true, "edge", "d", "commit"));
    private static void Edit(string path, Action<JsonObject> update)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        update(json);
        File.WriteAllText(path, json.ToJsonString());
    }
    private void Expire(bool started = false) => Edit(StatePath, json =>
    {
        json["CandidateExpiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        json["CommitStarted"] = started;
    });
    private string InboxPath() => Assert.Single(Directory.GetFiles(Path.Combine(_directory, "inbox"), "*.json"));
    private void AgeInbox() => Edit(InboxPath(), json => json["CompletedAt"] = DateTimeOffset.UtcNow.AddDays(-8));

    public NodeConfigurationRetentionTests()
    {
        Directory.CreateDirectory(_directory);
        SharedData.ConfigFilePath = Path.Combine(_directory, "config.yaml");
        File.WriteAllText(SharedData.ConfigFilePath, "SpeedLimit:\n  Value: old\n");
        _root = new ConfigurationBuilder().AddYamlFile(SharedData.ConfigFilePath, false, false).Build();
    }

    [Fact]
    public async Task DeadlinePersistsAcrossPrepareAndRestartAndExpiredCommitCannotWrite()
    {
        Assert.True((await Node().PrepareAsync(Request(), default)).Ack);
        var before = File.ReadAllText(StatePath);
        Assert.True((await Node().PrepareAsync(Request(), default)).Ack);
        Assert.Equal(before, File.ReadAllText(StatePath));
        Expire();
        Assert.False((await Node().CommitAsync("d", default)).Ack);
        Assert.False((await Node().PrepareAsync(Request(), default)).Ack);
        Assert.Equal("old", _root["SpeedLimit:Value"]);
        Assert.Equal(1, await Node().CleanupExpiredCandidatesAsync(default));
        Assert.False(File.Exists(CandidatePath));
        Assert.False(File.Exists(CandidatePath + ".overlay"));
        Assert.True(File.Exists(CandidatePath + ".expired"));
        Assert.False((await Node().PrepareAsync(Request(), default)).Ack);
        Assert.True((await Node().PrepareAsync(Request("new-task"), default)).Ack);
        Assert.False((await Node().CommitAsync("d", default)).Ack);
    }

    [Fact]
    public async Task LegacyDeadlineMissingAndUnknownCommitRemainIntact()
    {
        Assert.True((await Node().PrepareAsync(Request(), default)).Ack);
        Edit(StatePath, json => json.Remove("CandidateExpiresAt"));
        Assert.False((await Node().CommitAsync("d", default)).Ack);
        Assert.Equal(0, await Node().CleanupExpiredCandidatesAsync(default));
        Expire(started: true);
        var node = Node();
        Assert.Equal(0, await node.CleanupExpiredCandidatesAsync(default));
        await Assert.ThrowsAsync<NodeConfigurationOutcomeUnknownException>(() => node.CommitAsync("d", default));
        await Assert.ThrowsAsync<NodeConfigurationOutcomeUnknownException>(() => node.RollbackAsync("d", default));
        Assert.True(File.Exists(CandidatePath));
        Assert.True(File.Exists(CandidatePath + ".overlay"));
    }

    [Fact]
    public async Task AppliedCompensationAndUnknownReconciliationSurviveCleanup()
    {
        Assert.True((await Node().PrepareAsync(Request(), default)).Ack);
        var prepared = File.ReadAllText(StatePath);
        Assert.True((await Node().CommitAsync("d", default)).Ack);
        Assert.Equal(0, await Node().CleanupExpiredCandidatesAsync(default));
        Assert.True(File.Exists(CandidatePath));
        // Simulate the crash window after the configuration write but before the Applied state write.
        File.WriteAllText(StatePath, prepared);
        Expire(started: true);
        var restarted = Node();
        Assert.Equal(0, await restarted.CleanupExpiredCandidatesAsync(default));
        Assert.NotNull(await restarted.ReconcileCommandAsync("configuration.commit", "d", null, default));
        Assert.True((await restarted.RollbackAsync("d", default)).Ack);
        Assert.Equal("old", _root["SpeedLimit:Value"]);
    }

    [Fact]
    public async Task FenceWriteFailureNeverDeletesCandidateAndCanRetry()
    {
        Assert.True((await Node().PrepareAsync(Request(), default)).Ack);
        Expire();
        Directory.CreateDirectory(CandidatePath + ".expired");
        var error = await Record.ExceptionAsync(() => Node().CleanupExpiredCandidatesAsync(default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(File.Exists(CandidatePath));
        Assert.True(File.Exists(CandidatePath + ".overlay"));
        Directory.Delete(CandidatePath + ".expired");
        Assert.Equal(1, await Node().CleanupExpiredCandidatesAsync(default));
    }

    [Fact]
    public async Task DeleteFailureLeavesFenceAndRestartFinishesCleanup()
    {
        Assert.True((await Node().PrepareAsync(Request(), default)).Ack);
        Expire();
        using (var held = new FileStream(CandidatePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => Node().CleanupExpiredCandidatesAsync(default));
            Assert.True(File.Exists(CandidatePath + ".expired"));
            Assert.False((await Node().CommitAsync("d", default)).Ack);
        }
        Assert.Equal(1, await Node().CleanupExpiredCandidatesAsync(default));
        Assert.False((await Node().PrepareAsync(Request(), default)).Ack);
    }

    [Fact]
    public async Task StateWriteFailureBeforeCommitCannotApplyOrPublishInMemory()
    {
        var node = Node();
        Assert.True((await node.PrepareAsync(Request(), default)).Ack);
        using (var held = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False((await node.CommitAsync("d", default)).Ack);
            Assert.Equal("Prepared", node.GetStatus().State);
            Assert.Equal("old", _root["SpeedLimit:Value"]);
        }
        Assert.False(JsonNode.Parse(File.ReadAllText(StatePath))!["CommitStarted"]!.GetValue<bool>());
        Assert.True((await node.CommitAsync("d", default)).Ack);
    }

    [Fact]
    public async Task StateWriteFailureDuringExpirationKeepsBodyAndFenceThenRecovers()
    {
        Assert.True((await Node().PrepareAsync(Request(), default)).Ack);
        Expire();
        var node = Node();
        node.GetStatus();
        using (var held = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => node.CleanupExpiredCandidatesAsync(default));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("Prepared", node.GetStatus().State);
            Assert.True(File.Exists(CandidatePath));
            Assert.True(File.Exists(CandidatePath + ".expired"));
        }
        Assert.Equal(1, await Node().CleanupExpiredCandidatesAsync(default));
        Assert.False((await Node().PrepareAsync(Request(), default)).Ack);
    }

    [Fact]
    public async Task CommitWriteFailureAfterMarkerLeavesInboxUnknownAndCandidateProtected()
    {
        var node = Node();
        Assert.True((await node.PrepareAsync(Request(), default)).Ack);
        var message = Message();
        using (var held = new FileStream(SharedData.ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => Inbox().ExecuteAsync(message, () => node.CommitAsync("d", default), default));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.True(JsonNode.Parse(File.ReadAllText(StatePath))!["CommitStarted"]!.GetValue<bool>());
        Assert.Null(JsonNode.Parse(File.ReadAllText(InboxPath()))!["Result"]);
        Expire(started: true);
        Assert.Equal(0, await Node().CleanupExpiredCandidatesAsync(default));
        Assert.True(File.Exists(CandidatePath));
        Assert.True(File.Exists(CandidatePath + ".overlay"));
        var replay = await Inbox().ExecuteAsync(message, () => throw new Exception("no replay"), default,
            () => Node().ReconcileCommandAsync("configuration.commit", "d", null, default));
        Assert.Equal("node_outcome_unknown", replay.Error?.Code);
    }

    [Fact]
    public async Task SevenDayInboxRetiresToPermanentFenceWithoutExecuteOrReconcile()
    {
        var message = Message();
        await Inbox().ExecuteAsync(message, Ack, default);
        Assert.Equal(0, await Inbox().RetireCompletedAsync(default));
        AgeInbox();
        Assert.Equal(1, await Inbox().RetireCompletedAsync(default));
        Assert.Null(JsonNode.Parse(File.ReadAllText(InboxPath()))!["Result"]);
        foreach (var query in new[] { false, true })
        {
            var result = await Inbox().ExecuteAsync(message, () => throw new Exception("no execute"), default,
                () => throw new Exception("no reconcile"), query);
            Assert.Equal("node_result_retired", result.Error?.Code);
        }
        var conflict = await Inbox().ExecuteAsync(message with { Payload = JsonSerializer.SerializeToElement(new { changed = true }) },
            () => throw new Exception("no execute"), default);
        Assert.Equal("node_message_conflict", conflict.Error?.Code);
        Assert.Equal(0, await Inbox().RetireCompletedAsync(default));
    }

    [Fact]
    public async Task UnknownAndLegacyInboxAreNeverRetiredAndExpiredMissingCommandNeverExecutes()
    {
        var message = Message();
        await Assert.ThrowsAsync<IOException>(() => Inbox().ExecuteAsync(message, () => throw new IOException("unknown"), default));
        AgeInbox();
        Assert.Equal(0, await Inbox().RetireCompletedAsync(default));
        Assert.Equal("node_outcome_unknown", (await Inbox().ExecuteAsync(message, Ack, default)).Error?.Code);
        await Inbox().ExecuteAsync(message, Ack, default, async () => await Ack());
        Edit(InboxPath(), json => json.Remove("CompletedAt"));
        Assert.Equal(0, await Inbox().RetireCompletedAsync(default));
        Assert.NotNull((await Inbox().ExecuteAsync(message, () => throw new Exception("no replay"), default)).Acknowledgement);
        Assert.Equal("node_message_expired", (await Inbox().ExecuteAsync(Message() with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) },
            () => throw new Exception("expired must not execute"), default)).Error?.Code);
    }

    [Fact]
    public async Task InboxRewriteFailureKeepsResultAndReleasesLock()
    {
        var message = Message();
        await Inbox().ExecuteAsync(message, Ack, default);
        AgeInbox();
        var before = File.ReadAllText(InboxPath());
        Directory.CreateDirectory(InboxPath() + ".tmp");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Inbox().RetireCompletedAsync(default));
        Assert.Equal(before, File.ReadAllText(InboxPath()));
        Directory.Delete(InboxPath() + ".tmp");
        Assert.NotNull((await Inbox().ExecuteAsync(message, () => throw new Exception("no replay"), default)).Acknowledgement);
        Assert.Equal(1, await Inbox().RetireCompletedAsync(default));
    }

    [Fact]
    public async Task InboxBatchIsBoundedAndCorruptionFailsClosed()
    {
        var message = Message();
        await Inbox().ExecuteAsync(message, Ack, default);
        AgeInbox();
        var original = InboxPath();
        for (var i = 0; i < 100; i++) File.Copy(original, Path.Combine(_directory, "inbox", $"extra-{i}.json"));
        Assert.Equal(100, await Inbox().RetireCompletedAsync(default));
        Assert.Equal(1, await Inbox().RetireCompletedAsync(default));
        Assert.Equal(101, Directory.GetFiles(Path.Combine(_directory, "inbox"), "*.json").Length);
        File.WriteAllText(original, "{");
        await Assert.ThrowsAsync<JsonException>(() => Inbox().RetireCompletedAsync(default));
        Assert.Equal("node_inbox_corrupt", (await Inbox().ExecuteAsync(message, () => throw new Exception("no replay"), default)).Error?.Code);
        Assert.Equal("{", File.ReadAllText(original));
    }

    [Fact]
    public async Task MaintenanceDoesNotRaceInFlightInboxOrIgnoreCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = Inbox().ExecuteAsync(Message(), async () => { entered.SetResult(); await release.Task; return await Ack(); }, default);
        await entered.Task;
        try { await Assert.ThrowsAnyAsync<IOException>(() => Inbox().RetireCompletedAsync(default)); }
        finally { release.SetResult(); await executing; }
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Inbox().RetireCompletedAsync(stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Node().CleanupExpiredCandidatesAsync(stop.Token));
    }

    [Fact]
    public async Task HostedMaintenanceIsRegisteredAndRunsWithoutManualCall()
    {
        var services = new ServiceCollection();
        services.AddLyWafDistribution(new ConfigurationBuilder().Build());
        Assert.Contains(services, item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(NodeConfigurationMaintenanceService));
        Assert.True((await Node().PrepareAsync(Request(), default)).Ack);
        Expire();
        await Inbox().ExecuteAsync(Message(), Ack, default);
        AgeInbox();
        using var service = new NodeConfigurationMaintenanceService(Node(), Inbox(), Options, Protocol, NullLogger<NodeConfigurationMaintenanceService>.Instance);
        await service.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (File.Exists(CandidatePath) || JsonNode.Parse(File.ReadAllText(InboxPath()))!["Retired"]?.GetValue<bool>() != true)
                await Task.Delay(20, timeout.Token);
        }
        finally { await service.StopAsync(default); }
    }

    public void Dispose()
    {
        (_root as IDisposable)?.Dispose();
        SharedData.ConfigFilePath = _oldPath;
        Directory.Delete(_directory, true);
    }
}
