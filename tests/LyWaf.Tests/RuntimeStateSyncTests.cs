using System.Net;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Telemetry;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sync-" + Guid.NewGuid().ToString("N"));
    private StaticOptionsMonitor<RuntimeStateOptions> Options(string id) => new(new()
    {
        Enabled = true, NodeId = id, StateDirectory = Path.Combine(_root, id),
        Peers = new() { [id == "source" ? "target" : "source"] = new()
        {
            Endpoint = "https://target", ProtocolVersion = 2, DurableSyncEnabled = true,
            Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true },
            Receive = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true }
        } }
    });
    private static RuntimeStateMutation Mutation(int value = 1) => new("test", "key", "source", value,
        DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(value));
    private RuntimeStateSyncBatch Batch() => new RuntimeStateSyncSender(Options("source")).Prepare("cluster", "source", "target", [Mutation()]);
    private static RuntimeStateSyncBatch Rehash(RuntimeStateSyncBatch batch) => batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };

    [Fact]
    public async Task StructuredConflictPreservesSafeErrorAndPendingBatch()
    {
        var options = Options("source");
        var config = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "source",
            Peers = new() { ["target"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('s', 32) } } }
        });
        var sender = new RuntimeStateSyncSender(options);
        var pending = sender.Prepare("cluster", "source", "target", [Mutation()]);
        using var client = new HttpClient(new RejectionTransport());
        var telemetry = new NodeCommunicationTelemetry();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new RuntimeStateSyncTransport(client, options, config, telemetry: telemetry)
            .SendAsync("target", null, default));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal("node_outcome_unknown", error.Data["ErrorCode"]);
        Assert.DoesNotContain("private-remote-content", error.ToString());
        Assert.Equal(pending.Digest, sender.ReadPending("cluster", "source", "target")!.Digest);
        Assert.Equal(0, telemetry.Capture().ReplicationAcknowledgement!.Samples);
    }

    private sealed class RejectionTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"errorCode":"node_outcome_unknown","traceId":"safe-id","message":"private-remote-content"}""")
            });
    }

    [Fact]
    public async Task RecoveryDoesNotWaitForSlowFirstPeerAndStopsPromptly()
    {
        var options = Options("source");
        options.CurrentValue.Peers["fast"] = new()
        {
            Endpoint = "https://fast", ProtocolVersion = 2, DurableSyncEnabled = true,
            Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true }
        };
        var config = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "source",
            Peers = new()
            {
                ["target"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('s', 32) } },
                ["fast"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('s', 32) } }
            }
        });
        var sender = new RuntimeStateSyncSender(options);
        sender.Prepare("cluster", "source", "target", [Mutation()]);
        sender.Prepare("cluster", "source", "fast", [Mutation() with { ChangedAt = DateTimeOffset.UtcNow.AddSeconds(-2) }]);
        using var handler = new RecoveryTransport();
        using var client = new HttpClient(handler);
        using var stop = new CancellationTokenSource();
        var telemetry = new NodeCommunicationTelemetry();
        var transport = new RuntimeStateSyncTransport(client, options, config, telemetry: telemetry);
        var recovery = transport.RecoverAsync(stop.Token);
        try
        {
            await handler.SlowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await handler.FastDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sender.ReadPending("cluster", "source", "fast") is not null && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Null(sender.ReadPending("cluster", "source", "fast"));
            Assert.NotNull(sender.ReadPending("cluster", "source", "target"));
        }
        finally
        {
            stop.Cancel();
            await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var measured = telemetry.Capture().ReplicationAcknowledgement!;
        Assert.Equal(1, measured.Samples);
        Assert.True(measured.TotalMilliseconds >= 2000);
        await transport.SendAsync("fast", null, default);
        Assert.Equal(1, telemetry.Capture().ReplicationAcknowledgement!.Samples);
    }

    private sealed class RecoveryTransport : HttpMessageHandler
    {
        public TaskCompletionSource SlowEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FastDelivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "target")
            {
                SlowEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            var message = JsonSerializer.Deserialize<NodeMessage>(await request.Content!.ReadAsByteArrayAsync(token), RuntimeStateSyncProtocol.Json)!;
            var batch = message.Payload.Deserialize<RuntimeStateSyncBatch>(RuntimeStateSyncProtocol.Json)!;
            var ack = new RuntimeStateSyncAck(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence, batch.Digest);
            FastDelivered.TrySetResult();
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(ack, RuntimeStateSyncProtocol.Json)) };
        }
    }

    [Fact]
    public async Task AckWaitsForJournalFlushNotJustEnqueue()
    {
        var options = Options("target");
        using var store = new RuntimeStateStore(options);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        store.JournalBeforeDurableFlush = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var batch = Batch();
        var pending = new RuntimeStateSyncReceiver(options, store).AcceptAsync(batch, default);
        Assert.False(pending.IsCompleted);
        Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
        await store.StartAsync(default);
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
            Assert.False(pending.IsCompleted);
            Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
            Assert.Empty(Directory.GetFiles(Path.Combine(_root, "target", "runtime-sync"), "*.json"));
        }
        finally { release.Set(); }
        Assert.Equal("Applied", (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        await store.StopAsync(default);
    }

    [Fact]
    public async Task CompactionAndRestartPreserveDuplicateAckAndRejectEpochChange()
    {
        var options = Options("target");
        var batch = Batch();
        using (var store = new RuntimeStateStore(options))
        {
            await store.StartAsync(default);
            Assert.Equal("Applied", (await new RuntimeStateSyncReceiver(options, store).AcceptAsync(batch, default)).Status);
            await store.StopAsync(default);
            Assert.True(await store.CompactJournalAsync(Path.Combine(options.CurrentValue.StateDirectory, "runtime-state.jsonl"), default));
        }
        using var recovered = new RuntimeStateStore(options);
        var receiver = new RuntimeStateSyncReceiver(options, recovered);
        Assert.Equal("Applied", (await receiver.AcceptAsync(batch, default)).Status);
        Assert.True(recovered.TryGet<int>("test", "key", out var value));
        Assert.Equal(1, value);
        Assert.Equal("SnapshotRequired", (await receiver.AcceptAsync(Rehash(batch with { Epoch = Guid.NewGuid().ToString("N") }), default)).Status);
        Assert.Equal("Conflict", (await receiver.AcceptAsync(Rehash(batch with { Mutations = [Mutation(2)] }), default)).Status);
        Assert.Equal("Gap", (await receiver.AcceptAsync(Rehash(batch with { Sequence = 3 }), default)).Status);
    }

    [Fact]
    public async Task ReceiptWithoutJournalNeverAcknowledges()
    {
        var options = Options("target");
        var batch = Batch();
        using (var store = new RuntimeStateStore(options))
        {
            await store.StartAsync(default);
            await new RuntimeStateSyncReceiver(options, store).AcceptAsync(batch, default);
            await store.StopAsync(default);
        }
        File.Delete(Path.Combine(options.CurrentValue.StateDirectory, "runtime-state.jsonl"));
        using var recovered = new RuntimeStateStore(options);
        Assert.Equal("SnapshotRequired", (await new RuntimeStateSyncReceiver(options, recovered).AcceptAsync(batch, default)).Status);
    }

    [Fact]
    public void SenderRestartRetainsPayloadAndRejectsForgedAck()
    {
        var batch = Batch();
        var sender = new RuntimeStateSyncSender(Options("source"));
        Assert.Equal(batch.Digest, sender.ReadPending("cluster", "source", "target")!.Digest);
        Assert.Throws<InvalidOperationException>(() => sender.Prepare("cluster", "source", "target", [Mutation(2)]));
        var ack = new RuntimeStateSyncAck("cluster", "source", "target", batch.Epoch, batch.Sequence, batch.Digest);
        foreach (var forged in new[] { ack with { ReceiverNodeId = "other" }, ack with { Digest = "bad" },
                     ack with { Sequence = 2 }, ack with { Epoch = Guid.NewGuid().ToString("N") }, ack with { Status = "Gap" } })
            Assert.Throws<InvalidDataException>(() => sender.Confirm(batch, forged));
        Assert.NotNull(sender.ReadPending("cluster", "source", "target"));
        sender.Confirm(batch, ack);
        Assert.Null(sender.ReadPending("cluster", "source", "target"));
        Assert.Equal(2, sender.Prepare("cluster", "source", "target", [Mutation(2)]).Sequence);
    }

    [Fact]
    public async Task JournalFenceWithoutReceiptIsConfirmedWithoutReapplyingPayload()
    {
        var options = Options("target");
        var batch = Batch();
        using var store = new RuntimeStateStore(options);
        await store.StartAsync(default);
        try
        {
            await store.MergeDurablyAsync(batch, default);
            store.Set("test", "key", 99);
            Assert.Equal("Applied", (await new RuntimeStateSyncReceiver(options, store).AcceptAsync(batch, default)).Status);
            Assert.True(store.TryGet<int>("test", "key", out var value));
            Assert.Equal(99, value);
        }
        finally { await store.StopAsync(default); }
    }

    [Fact]
    public async Task TrustedRelayRequiresExplicitSourceGrant()
    {
        var options = Options("target");
        var batch = Rehash(Batch() with { Mutations = [Mutation() with { NodeId = "origin" }] });
        using var store = new RuntimeStateStore(options);
        var receiver = new RuntimeStateSyncReceiver(options, store);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => receiver.AcceptAsync(batch, default));
        options.CurrentValue.Peers["source"].Receive.TrustedForwardedSources = ["origin"];
        await store.StartAsync(default);
        try { Assert.Equal("Applied", (await receiver.AcceptAsync(batch, default)).Status); }
        finally { await store.StopAsync(default); }
    }

    [Fact]
    public async Task DurableModeCannotBypassAuthorizationThroughLegacyMerge()
    {
        var options = Options("target");
        using var store = new RuntimeStateStore(options);
        Assert.Equal("runtime_sync_required", RuntimeStateReplicationAuthorization.AuthorizeReceive(options.CurrentValue, "source", [Mutation()]));
        var batch = Batch();
        foreach (var mutation in new[] { Mutation() with { Domain = "cc.new-domain" }, Mutation() with { NodeId = "forged" } })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new RuntimeStateSyncReceiver(options, store)
                .AcceptAsync(Rehash(batch with { Mutations = [mutation] }), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplicatorSendsNewMutationOrRecoversDiskPending(bool restart)
    {
        var batch = restart ? Batch() : null;
        var options = Options("source");
        var config = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "source",
            Peers = new() { ["target"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('s', 32) } } }
        });
        var targetOptions = Options("target");
        using var target = new RuntimeStateStore(targetOptions);
        await target.StartAsync(default);
        using var source = new RuntimeStateStore(options);
        var handler = new Transport(new(targetOptions, target));
        using var replicator = new RuntimeStateReplicator(source, options, handler, config);
        await replicator.StartAsync(default);
        try
        {
            if (!restart) source.Set("test", "key", 1);
            await handler.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var sender = new RuntimeStateSyncSender(options);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var confirmed = false;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    confirmed = sender.ReadPending("cluster", "source", "target") is null;
                    if (confirmed) break;
                }
                // Delivery precedes the sender's durable ACK commit, which holds this stream lock.
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { }
                await Task.Delay(20);
            }
            Assert.True(confirmed, "Sender did not durably confirm the pending batch within 10 seconds");
            Assert.True(target.TryGet<int>("test", "key", out _));
            if (restart) Assert.Equal(batch!.Digest, handler.Digest);
        }
        finally { await replicator.StopAsync(default); await target.StopAsync(default); }
    }

    [Fact]
    public async Task CancelledWaitCannotPersistReceiptBeforeJournalRuns()
    {
        var options = Options("target");
        using var store = new RuntimeStateStore(options);
        using var cancel = new CancellationTokenSource();
        var batch = Batch();
        var pending = new RuntimeStateSyncReceiver(options, store).AcceptAsync(batch, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "target", "runtime-sync"), "*.json"));
        await store.StartAsync(default);
        await store.StopAsync(default);
        Assert.NotNull(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
        Assert.Equal("Applied", (await new RuntimeStateSyncReceiver(options, store).AcceptAsync(batch, default)).Status);
    }

    [Fact]
    public async Task RollingHistoryDoesNotStopAtMaximumHistory()
    {
        var options = Options("target");
        using var store = new RuntimeStateStore(options);
        await store.StartAsync(default);
        try
        {
            var batch = Rehash(Batch() with { Sequence = RuntimeStateSyncProtocol.MaximumHistory });
            await store.MergeDurablyAsync(batch, default);
            var files = new RuntimeStateSyncFiles(options.CurrentValue.StateDirectory);
            files.Save(files.PathFor("in", "cluster", "source", "target"), new
            {
                batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence,
                Digests = Enumerable.Range(1, RuntimeStateSyncProtocol.MaximumHistory).ToDictionary(i => (long)i, _ => batch.Digest),
                LastAcceptedAt = DateTimeOffset.UtcNow
            });
            var next = Rehash(batch with { Sequence = batch.Sequence + 1, Mutations = [Mutation(2)] });
            var receiver = new RuntimeStateSyncReceiver(options, store);
            Assert.Equal("Applied", (await receiver.AcceptAsync(next, default)).Status);
            Assert.Equal("Applied", (await receiver.AcceptAsync(batch, default)).Status);
            Assert.Equal("SnapshotRequired", (await receiver.AcceptAsync(Rehash(batch with { Sequence = 1 }), default)).Status);
        }
        finally { await store.StopAsync(default); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundReplicatorRepairsGapFromDurableRetainedRange(bool businessStatus)
    {
        var sourceOptions = Options("source");
        var sender = new RuntimeStateSyncSender(sourceOptions);
        RuntimeStateSyncBatch? first = null;
        for (var i = 1; i <= 2; i++)
        {
            var batch = sender.Prepare("cluster", "source", "target", [Mutation(i)]);
            first ??= batch;
            sender.Confirm(batch, new("cluster", "source", "target", batch.Epoch, batch.Sequence, batch.Digest));
        }
        sender.Prepare("cluster", "source", "target", [Mutation(3)]);
        var targetOptions = Options("target");
        using var target = new RuntimeStateStore(targetOptions);
        await target.StartAsync(default);
        var receiver = new RuntimeStateSyncReceiver(targetOptions, target);
        await receiver.AcceptAsync(first!, default);
        var handler = new Transport(receiver) { BusinessStatus = businessStatus };
        var config = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "source",
            Peers = new() { ["target"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('s', 32) } } }
        });
        using var source = new RuntimeStateStore(sourceOptions);
        using var replicator = new RuntimeStateReplicator(source, sourceOptions, handler, config);
        await replicator.StartAsync(default);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (sender.ReadPending("cluster", "source", "target") is not null && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Null(sender.ReadPending("cluster", "source", "target"));
            Assert.True(target.TryGet<int>("test", "key", out var value));
            Assert.Equal(3, value);
            Assert.Equal(new long[] { 3, 2, 3 }, handler.Sequences.ToArray());
        }
        finally { await replicator.StopAsync(default); await target.StopAsync(default); }
    }

    [Fact]
    public void MissingRetainedRangeDoesNotDiscardPending()
    {
        var sender = new RuntimeStateSyncSender(Options("source"));
        for (var i = 1; i <= 65; i++)
        {
            var batch = sender.Prepare("cluster", "source", "target", [Mutation(i)]);
            sender.Confirm(batch, new("cluster", "source", "target", batch.Epoch, batch.Sequence, batch.Digest));
        }
        var pending = sender.Prepare("cluster", "source", "target", [Mutation(66)]);
        Assert.Throws<RuntimeStateSyncHistoryUnavailableException>(() => sender.ReadRange(pending, 1));
        Assert.Equal(64, sender.ReadRange(pending, 2).Count);
        Assert.Equal(pending.Digest, sender.ReadPending("cluster", "source", "target")!.Digest);
    }

    private sealed class Transport(RuntimeStateSyncReceiver receiver) : HttpMessageHandler
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Digest;
        public bool BusinessStatus { get; init; }
        public System.Collections.Concurrent.ConcurrentQueue<long> Sequences { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.EndsWith("runtime.sync", request.RequestUri!.AbsolutePath);
            var message = JsonSerializer.Deserialize<NodeMessage>(await request.Content!.ReadAsByteArrayAsync(token), RuntimeStateSyncProtocol.Json)!;
            var batch = message.Payload.Deserialize<RuntimeStateSyncBatch>(RuntimeStateSyncProtocol.Json)!;
            Sequences.Enqueue(batch.Sequence);
            var ack = await receiver.AcceptAsync(batch, token);
            Digest = batch.Digest;
            Delivered.TrySetResult();
            return new(ack.Status == "Applied" || BusinessStatus ? HttpStatusCode.OK : HttpStatusCode.Conflict)
                { Content = new StringContent(JsonSerializer.Serialize(ack, RuntimeStateSyncProtocol.Json)) };
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
