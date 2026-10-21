using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeCommandInboxTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lywaf-inbox-" + Guid.NewGuid().ToString("N"));
    private NodeCommandInbox Inbox() => new(new StaticOptionsMonitor<NodeCommunicationOptions>(new() { StateDirectory = _directory }));
    private static NodeMessage Message() => new(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "deployment",
        "configuration.commit", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), JsonSerializer.SerializeToElement(new { deploymentId = "deployment" }));
    private static Task<NodeAcknowledgement> Ack() => Task.FromResult(new NodeAcknowledgement(true, "edge", "deployment", "Applied"));

    [Fact]
    public async Task AdmissionReservesUnknownRecordAndMaximumOutcomeTemporaryCopy()
    {
        var directory = Path.Combine(_directory, "inbox");
        Directory.CreateDirectory(directory);
        using (var filler = File.Create(Path.Combine(directory, "retained.tmp")))
            filler.SetLength(63L * 1024 * 1024);
        var result = await Inbox().ExecuteAsync(Message(),
            () => throw new InvalidOperationException("effect cannot start without outcome replacement headroom"), default);
        Assert.Equal("node_inbox_full", result.Error?.Code);
        Assert.Empty(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public async Task OrphanTemporaryFilesCountTowardsCapacityBeforeExecution()
    {
        var directory = Path.Combine(_directory, "inbox");
        Directory.CreateDirectory(directory);
        using (var orphan = File.Create(Path.Combine(directory, "orphan.tmp")))
            orphan.SetLength(64L * 1024 * 1024);
        var result = await Inbox().ExecuteAsync(Message(),
            () => throw new InvalidOperationException("must not execute at capacity"), default);
        Assert.Equal("node_inbox_full", result.Error?.Code);
        Assert.Empty(Directory.GetFiles(directory, "*.json"));
    }

    [Fact]
    public async Task OversizedOutcomePreservesUnknownAndNeverRepeatsEffect()
    {
        var message = Message();
        await Assert.ThrowsAsync<IOException>(() => Inbox().ExecuteAsync(message,
            () => Task.FromResult(new NodeAcknowledgement(true, "edge", "deployment", new string('x', 1024 * 1024))), default));
        var result = await Inbox().ExecuteAsync(message,
            () => throw new InvalidOperationException("must not repeat effect"), default);
        Assert.Equal("node_outcome_unknown", result.Error?.Code);
        Assert.True(new FileInfo(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "inbox"), "*.json"))).Length < 1024);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "inbox"), "*.tmp"));
    }

    [Fact]
    public async Task CompletedCommandIsReusedAfterInboxRestart()
    {
        var message = Message();
        var first = await Inbox().ExecuteAsync(message, Ack, default);
        var repeated = await Inbox().ExecuteAsync(message with { CreatedAt = DateTimeOffset.UtcNow },
            () => throw new InvalidOperationException("must not execute twice"), default);
        Assert.Equal(first, repeated);
        Assert.True(repeated.Acknowledgement!.Ack);
    }

    [Fact]
    public async Task ChangedPayloadWithSameMessageIdIsRejected()
    {
        var message = Message();
        await Inbox().ExecuteAsync(message, Ack, default);
        var result = await Inbox().ExecuteAsync(message with { Payload = JsonSerializer.SerializeToElement(new { different = true }) },
            () => throw new InvalidOperationException("must not execute"), default);
        Assert.Equal("node_message_conflict", result.Error?.Code);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Digest\":\"invalid\"}")]
    public async Task CorruptRecordFailsClosedWithoutReplacingEvidence(string contents)
    {
        var message = Message();
        await Inbox().ExecuteAsync(message, Ack, default);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "inbox"), "*.json"));
        await File.WriteAllTextAsync(path, contents);
        var result = await Inbox().ExecuteAsync(message,
            () => throw new InvalidOperationException("must not repeat"), default);
        Assert.Equal("node_inbox_corrupt", result.Error?.Code);
        Assert.Null(result.Acknowledgement);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OversizedRecordFailsClosed()
    {
        var message = Message();
        await Inbox().ExecuteAsync(message, Ack, default);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "inbox"), "*.json"));
        await File.WriteAllTextAsync(path, new string(' ', 1024 * 1024 + 1));
        var result = await Inbox().ExecuteAsync(message,
            () => throw new InvalidOperationException("must not repeat"), default);
        Assert.Equal("node_inbox_corrupt", result.Error?.Code);
    }

    [Fact]
    public async Task LockedRecordFailsClosedAndCanBeReadAfterRelease()
    {
        var message = Message();
        await Inbox().ExecuteAsync(message, Ack, default);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_directory, "inbox"), "*.json"));
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var unavailable = await Inbox().ExecuteAsync(message,
                () => throw new InvalidOperationException("must not repeat"), default);
            Assert.Equal("node_inbox_unavailable", unavailable.Error?.Code);
        }
        var recovered = await Inbox().ExecuteAsync(message,
            () => throw new InvalidOperationException("must not repeat"), default);
        Assert.True(recovered.Acknowledgement!.Ack);
    }

    [Fact]
    public async Task InterruptedCommandRemainsUnknownInsteadOfExecutingAgain()
    {
        var message = Message();
        await Assert.ThrowsAsync<IOException>(() => Inbox().ExecuteAsync(message,
            () => throw new IOException("simulated interruption after effect"), default));
        var result = await Inbox().ExecuteAsync(message, () => throw new InvalidOperationException("must not repeat effect"), default);
        Assert.Equal("node_outcome_unknown", result.Error?.Code);
    }

    [Fact]
    public async Task InterruptedCommandCanRecoverVerifiedOutcomeAndPersistIt()
    {
        var message = Message();
        await Assert.ThrowsAsync<IOException>(() => Inbox().ExecuteAsync(message,
            () => throw new IOException("lost completion write"), default));
        var result = await Inbox().ExecuteAsync(message,
            () => throw new InvalidOperationException("must not repeat"), default,
            async () => await Ack());
        Assert.True(result.Acknowledgement!.Ack);
        var repeated = await Inbox().ExecuteAsync(message,
            () => throw new InvalidOperationException("must not repeat"), default,
            () => throw new InvalidOperationException("completed result needs no reconciliation"));
        Assert.Equal(result, repeated);
    }

    [Fact]
    public async Task MissingReconciliationEvidenceLeavesCommandUnknown()
    {
        var message = Message();
        await Assert.ThrowsAsync<IOException>(() => Inbox().ExecuteAsync(message,
            () => throw new IOException("interrupted"), default));
        var result = await Inbox().ExecuteAsync(message,
            () => throw new InvalidOperationException("must not repeat"), default,
            () => Task.FromResult<NodeAcknowledgement?>(null));
        Assert.Equal("node_outcome_unknown", result.Error?.Code);
    }

    [Fact]
    public async Task ConcurrentDeliveryCannotExecuteCommandTwice()
    {
        var message = Message();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Inbox().ExecuteAsync(message, async () => { started.SetResult(); await release.Task; return await Ack(); }, default);
        await started.Task;
        try
        {
            var second = await Inbox().ExecuteAsync(message, () => throw new InvalidOperationException("duplicate"), default);
            Assert.Equal("node_inbox_busy", second.Error?.Code);
        }
        finally { release.SetResult(); await first; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
