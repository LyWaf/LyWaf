using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateDurableBatchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "durable-batch-" + Guid.NewGuid().ToString("N"));
    private static RuntimeStateMutation Mutation(int n) => new("test", n.ToString(), "source", n,
        DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement("value"));
    private static NodeCommunicationOptions Config() => new()
    {
        Enabled = true, NodeId = "source", ClusterId = "cluster",
        Peers = new() { ["target"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('s', 32) } } }
    };

    [Fact]
    public void BoundedBatchKeepsOverflowQueuedAndSkipsDeniedSource()
    {
        var queue = Channel.CreateUnbounded<RuntimeStateMutation>();
        queue.Writer.TryWrite(Mutation(99) with { NodeId = "forged" });
        for (var i = 1; i <= 40; i++) queue.Writer.TryWrite(Mutation(i));
        var items = RuntimeStateSyncBatchPacking.Take(Config(), "target", Mutation(0), queue.Reader, m => m.NodeId == "source");
        Assert.Equal(32, items.Count);
        Assert.All(items, m => Assert.Equal("source", m.NodeId));
        Assert.True(queue.Reader.TryPeek(out var next));
        Assert.Equal("32", next.Key);
    }

    [Fact]
    public void OversizedNextMutationRemainsQueuedAndFirstFailsBeforeConsumption()
    {
        var queue = Channel.CreateUnbounded<RuntimeStateMutation>();
        queue.Writer.TryWrite(Mutation(1) with { Value = JsonSerializer.SerializeToElement(new string('x', 4000)) });
        var config = Config(); config.MaxMessageBytes = 2048;
        Assert.Single(RuntimeStateSyncBatchPacking.Take(config, "target", Mutation(0), queue.Reader, _ => true));
        Assert.True(queue.Reader.TryPeek(out _));
        config.MaxMessageBytes = 1;
        Assert.Throws<InvalidOperationException>(() => RuntimeStateSyncBatchPacking.Take(config, "target", Mutation(0), queue.Reader, _ => true));
        Assert.True(queue.Reader.TryPeek(out _));
    }

    [Fact]
    public async Task ActualTransportPersistsWholeBatchAndOnlyClearsMatchingAckAfterRetry()
    {
        var runtime = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "source", StateDirectory = root,
            Peers = new() { ["target"] = new() { Endpoint = "https://target", DurableSyncEnabled = true, ProtocolVersion = 2,
                Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true } } }
        });
        var sender = new RuntimeStateSyncSender(runtime);
        using var handler = new Handler(sender);
        using var client = new HttpClient(handler);
        var transport = new RuntimeStateSyncTransport(client, runtime, new StaticOptionsMonitor<NodeCommunicationOptions>(Config()));
        var queue = Channel.CreateUnbounded<RuntimeStateMutation>();
        queue.Writer.TryWrite(Mutation(2)); queue.Writer.TryWrite(Mutation(3) with { NodeId = "forged" });
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync("target", Mutation(1), default, queue.Reader));
        var pending = sender.ReadPending("cluster", "source", "target")!;
        Assert.Equal(2, pending.Mutations.Count);
        Assert.Equal(1, handler.Calls);
        handler.Reject = false;
        await transport.SendAsync("target", null, default);
        Assert.Equal(2, handler.Calls);
        Assert.Null(sender.ReadPending("cluster", "source", "target"));
        Assert.Equal(pending.Digest, handler.LastDigest);
    }

    [Fact]
    public async Task MultiMutationReceiptWaitsForFsyncAndSurvivesRestart()
    {
        var options = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "target", StateDirectory = root,
            Peers = new() { ["source"] = new() { DurableSyncEnabled = true,
                Receive = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true } } }
        });
        var batch = new RuntimeStateSyncBatch("cluster", "source", "target", Guid.NewGuid().ToString("N"), 1,
            Enumerable.Range(1, 32).Select(Mutation).ToArray(), "");
        batch = batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };
        using (var store = new RuntimeStateStore(options))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            store.JournalBeforeDurableFlush = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
            var accept = new RuntimeStateSyncReceiver(options, store).AcceptAsync(batch, default);
            await store.StartAsync(default);
            try
            {
                Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
                Assert.False(accept.IsCompleted);
                Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
            }
            finally { release.Set(); }
            Assert.Equal("Applied", (await accept.WaitAsync(TimeSpan.FromSeconds(10))).Status);
            await store.StopAsync(default);
        }
        using var recovered = new RuntimeStateStore(options);
        Assert.Equal("Applied", (await new RuntimeStateSyncReceiver(options, recovered).AcceptAsync(batch, default)).Status);
        foreach (var mutation in batch.Mutations)
        {
            Assert.True(recovered.TryGet<string>("test", mutation.Key, out var value));
            Assert.Equal("value", value);
        }
    }

    private sealed class Handler(RuntimeStateSyncSender sender) : HttpMessageHandler
    {
        internal bool Reject = true;
        internal int Calls;
        internal string? LastDigest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var message = JsonSerializer.Deserialize<NodeMessage>(await request.Content!.ReadAsByteArrayAsync(token), RuntimeStateSyncProtocol.Json)!;
            var batch = message.Payload.Deserialize<RuntimeStateSyncBatch>(RuntimeStateSyncProtocol.Json)!;
            RuntimeStateSyncProtocol.Validate(batch);
            Assert.Equal(batch.Digest, sender.ReadPending("cluster", "source", "target")!.Digest);
            LastDigest = batch.Digest;
            if (Reject) return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
                new RuntimeStateSyncAck(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence, batch.Digest), RuntimeStateSyncProtocol.Json)) };
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
