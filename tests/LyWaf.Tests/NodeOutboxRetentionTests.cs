using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeOutboxRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "outbox-retention-" + Guid.NewGuid().ToString("N"));
    private NodeCommandOutbox Store => new(_directory);
    private static Task<NodeAcknowledgement> NeverSend(NodeMessage _, bool query, CancellationToken token) =>
        throw new Exception("Archived commands must not reach transport");

    [Fact]
    public async Task SevenDayArchiveRemovesPayloadButPreservesResultAndReplayFenceAcrossRestart()
    {
        var record = Completed();
        var path = Seed(record);
        Assert.Equal(1, await Store.ArchiveCompletedAsync(default));
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("private-config-content", await File.ReadAllTextAsync(ArchivePath(record)));
        var archived = Assert.Single(Store.ReadRecords());
        Assert.True(archived.Archived);
        Assert.Equal(JsonValueKind.Null, archived.Message.Payload.ValueKind);
        Assert.Equal(record.Result, archived.Result);
        Assert.Equal(record.Result, await Store.ExecuteAsync(record.Message, record.Endpoint, NeverSend, default));
        Assert.Equal(record.Result, await Store.ExecuteAsync(archived.Message, record.Endpoint, NeverSend, default, recoveryOnly: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ExecuteAsync(record.Message with
        { Payload = JsonSerializer.SerializeToElement(new { content = "different" }) }, record.Endpoint, NeverSend, default));
        Assert.False(File.Exists(path));
        Assert.Equal(0, await Store.ArchiveCompletedAsync(default));
    }

    [Fact]
    public async Task UnknownExhaustedRejectedStoppedRecentAndLegacyRecordsNeverAgeOut()
    {
        var records = new[]
        {
            Completed() with { State = "Unknown", Result = null, Attempts = 3 },
            Completed() with { State = "Stopped", Result = null, StatusCode = 409 },
            Completed() with { State = "Rejected", Result = null, StatusCode = 401 },
            Completed() with { CompletedAt = null },
            Completed() with { CompletedAt = DateTimeOffset.UtcNow.AddDays(-6) }
        };
        foreach (var record in records) Seed(record);
        var nack = Completed();
        nack = nack with { Result = nack.Result! with { Ack = false } };
        Seed(nack);
        Assert.Equal(1, await Store.ArchiveCompletedAsync(default));
        Assert.All(records, record => Assert.True(File.Exists(ActivePath(record))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.ExecuteAsync(records[0].Message,
            records[0].Endpoint, NeverSend, default));
        Assert.Equal(3, Store.ReadRecords().Single(r => r.Message.MessageId == records[0].Message.MessageId).Attempts);
        Assert.Equal(nack.Result, await Store.ExecuteAsync(nack.Message, nack.Endpoint, NeverSend, default));
    }

    [Fact]
    public async Task CrashBetweenArchiveAndDeleteIsIdempotentlyRecovered()
    {
        var record = Completed();
        Seed(record);
        await Store.ArchiveCompletedAsync(default);
        Seed(record); // Crash left both durable archive and original source.
        Assert.True(Assert.Single(Store.ReadRecords()).Archived);
        Assert.Equal(record.Result, await Store.ExecuteAsync(record.Message, record.Endpoint, NeverSend, default));
        Assert.Equal(1, await Store.ArchiveCompletedAsync(default));
        Assert.False(File.Exists(ActivePath(record)));
    }

    [Fact]
    public async Task ArchiveWriteFailureKeepsSourceAndReleasesLocks()
    {
        var record = Completed();
        var path = Seed(record);
        Directory.CreateDirectory(ArchivePath(record) + ".tmp");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Store.ArchiveCompletedAsync(default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(File.Exists(path));
        Assert.Equal(record.Result, await Store.ExecuteAsync(record.Message, record.Endpoint, NeverSend, default));
        Directory.Delete(ArchivePath(record) + ".tmp");
        Assert.Equal(1, await Store.ArchiveCompletedAsync(default));
    }

    [Fact]
    public async Task ArchiveCapacityFailureDoesNotDeleteSourceAndCorruptionNeverAllowsReplay()
    {
        var record = Completed();
        var path = Seed(record);
        var archiveDirectory = Path.GetDirectoryName(ArchivePath(record))!;
        Directory.CreateDirectory(archiveDirectory);
        var reservation = Path.Combine(archiveDirectory, "quota-reservation");
        using (var file = File.Create(reservation)) file.SetLength(64L * 1024 * 1024);
        await Assert.ThrowsAsync<IOException>(() => Store.ArchiveCompletedAsync(default));
        Assert.True(File.Exists(path));
        File.Delete(reservation);
        await Store.ArchiveCompletedAsync(default);
        await File.WriteAllTextAsync(ArchivePath(record), "{broken");
        await Assert.ThrowsAsync<JsonException>(() => Store.ExecuteAsync(record.Message, record.Endpoint, NeverSend, default));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CancellationBeforeMaintenanceDoesNotDeleteAnything()
    {
        var record = Completed();
        var path = Seed(record);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.ArchiveCompletedAsync(stop.Token));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(ArchivePath(record)));
    }

    [Fact]
    public async Task HostedRecoveryRunsMaintenanceAutomaticallyAndThrottlesItsFrequency()
    {
        var record = Completed();
        var path = Seed(record);
        var protocol = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, Role = "Controller", ClusterId = "cluster", NodeId = "control", StateDirectory = _directory,
            Peers = new() { ["edge"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('k', 32) } } }
        });
        var client = new DistributionNodeClient(new StaticOptionsMonitor<DistributionOptions>(new()
        { Nodes = new() { ["edge"] = new() { Endpoint = "https://edge", ProtocolVersion = 2 } } }), new NoTransport(), protocol);
        using var worker = new NodeOutboxRecoveryService(client, protocol, NullLogger<NodeOutboxRecoveryService>.Instance);
        await worker.StartAsync(default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (File.Exists(path)) await Task.Delay(20, deadline.Token);
        }
        finally { await worker.StopAsync(default); }
        Assert.True(File.Exists(ArchivePath(record)));
        var next = Completed();
        Seed(next);
        await worker.MaintainOutboxAsync(default);
        Assert.True(File.Exists(ActivePath(next)));
        Assert.Equal(record.Result, await client.RecoverAsync(record.Message.MessageId, default));
    }

    [Fact]
    public async Task MaintenanceSkipsBusyPeerWithoutBlockingAndResumesAfterSend()
    {
        var old = Completed();
        Seed(old);
        var next = Completed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var now = DateTimeOffset.UtcNow;
        var pending = Store.ExecuteAsync(next.Message with { CreatedAt = now, ExpiresAt = now.AddMinutes(5) },
            next.Endpoint, async (_, _, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return next.Result!;
            }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, await Store.ArchiveCompletedAsync(default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(File.Exists(ActivePath(old)));
        }
        finally { release.TrySetResult(); await pending; }
        Assert.Equal(1, await Store.ArchiveCompletedAsync(default));
        Assert.False(File.Exists(ActivePath(old)));
    }

    [Fact]
    public async Task MaintenanceBatchIsBoundedAndDoesNotLoseResultVisibility()
    {
        for (var i = 0; i < 101; i++) Seed(Completed());
        Assert.Equal(100, await Store.ArchiveCompletedAsync(default));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "outbox"), "*.json"));
        Assert.Equal(101, Store.ReadRecords().Count);
        Assert.Equal(1, await Store.ArchiveCompletedAsync(default));
        Assert.All(Store.ReadRecords(), record => Assert.True(record.Archived));
    }

    [Fact]
    public async Task CompletionTimeIsPersistedOnlyWhenResultIsConfirmed()
    {
        var record = Completed();
        var message = record.Message with { CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        await Store.ExecuteAsync(message, record.Endpoint, (_, _, _) => Task.FromResult(record.Result!), default);
        var saved = Assert.Single(Store.ReadRecords());
        Assert.NotNull(saved.CompletedAt);
        Assert.True(saved.CompletedAt >= message.CreatedAt);
        Assert.Equal(0, await Store.ArchiveCompletedAsync(default));
    }

    private static NodeOutboxRecord Completed()
    {
        var completed = DateTimeOffset.UtcNow.AddDays(-8);
        var message = new NodeMessage(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "deployment",
            "configuration.prepare", completed.AddMinutes(-1), completed.AddMinutes(4),
            JsonSerializer.SerializeToElement(new { content = "private-config-content" }));
        return new(message, "https://edge", 1, "Completed", new(true, "edge", "deployment", "prepare"), CompletedAt: completed);
    }
    private string ActivePath(NodeOutboxRecord record)
    {
        var m = record.Message;
        return Path.Combine(_directory, "outbox", Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { m.ClusterId, m.SenderNodeId, m.ReceiverNodeId, m.MessageId }))) + ".json");
    }
    private string ArchivePath(NodeOutboxRecord record) => Path.Combine(_directory, "outbox", "archive", Path.GetFileName(ActivePath(record)));
    private string Seed(NodeOutboxRecord record)
    {
        var path = ActivePath(record);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(record));
        return path;
    }
    private sealed class NoTransport : IHttpClientFactory
    { public HttpClient CreateClient(string name) => throw new Exception("Maintenance must not send requests"); }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
