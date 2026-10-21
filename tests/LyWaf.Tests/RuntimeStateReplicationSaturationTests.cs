using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateReplicationSaturationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SaturatedIngressSendsCoalescedCcBeforeOrdinaryTrafficAndReclaimsBudget(int version)
    {
        var options = Options(version);
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(options));
        var transport = new BlockingCapture();
        using var replicator = new RuntimeStateReplicator(store, new StaticOptionsMonitor<RuntimeStateOptions>(options),
            transport, Communication());
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < 1000; i++) store.Set("ordinary", i.ToString(), new string('x', 100));
        for (var i = 0; i < 500; i++) store.IncrementWindow("cc.access", "counter", start, TimeSpan.FromMinutes(5));
        store.Set("cc.triggered", "penalty", "blocked", TimeSpan.FromMinutes(5));
        var expectedCounter = Assert.Single(store.ExportMutations(), x => x.Domain == "cc.access");
        var expectedPenalty = Assert.Single(store.ExportMutations(), x => x.Domain == "cc.triggered");
        Assert.Equal(499, replicator.CoalescedMutations);
        Assert.True(replicator.DroppedMutations >= 950);
        Assert.InRange(replicator.QueueStatus.Bytes, 1, options.ReplicationQueueMaxBytes);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await replicator.StartAsync(default);
        try
        {
            var first = await transport.Messages.Reader.ReadAsync(timeout.Token);
            Assert.Equal("cc.access", first.Domain);
            Assert.Equal(500, first.CounterComponents!.Values.Sum());
            Assert.Equal(expectedCounter.NodeId, first.NodeId);
            Assert.Equal(expectedCounter.ExpiresAt, first.ExpiresAt);
            Assert.Equal(expectedCounter.CounterComponents, first.CounterComponents);
            Assert.True(replicator.QueueStatus.Entries > 0); // First HTTP send still owns its lease.
            transport.Release.TrySetResult();
            RuntimeStateMutation penalty;
            do { penalty = await transport.Messages.Reader.ReadAsync(timeout.Token); }
            while (penalty.Domain != "cc.triggered");
            Assert.Equal(expectedPenalty.ExpiresAt, penalty.ExpiresAt);
            Assert.Equal("blocked", penalty.Value!.Value.GetString());
        }
        finally { transport.Release.TrySetResult(); await replicator.StopAsync(default); }
        Assert.Equal(0, replicator.QueueStatus.Bytes);
        Assert.Equal(0, replicator.QueueStatus.Entries);
    }

    [Fact]
    public async Task CoalescedCounterStillChecksAllComponentSourcesAtSendTime()
    {
        var options = Options(2);
        options.Peers["target"].Send.TrustedForwardedSources = ["relay"];
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(options));
        var transport = new BlockingCapture();
        using var replicator = new RuntimeStateReplicator(store, new StaticOptionsMonitor<RuntimeStateOptions>(options), transport, Communication());
        var now = DateTimeOffset.UtcNow;
        var first = new RuntimeStateMutation("cc.access", "key", "source", 1, now, now.AddMinutes(5), false, null,
            new Dictionary<string, long> { ["source"] = 40 });
        store.Merge(first, replicate: true);
        store.Merge(first with { Sequence = 2, CounterComponents = new Dictionary<string, long> { ["relay"] = 35 } }, replicate: true);
        Assert.Equal(1, replicator.CoalescedMutations);
        options.Peers["target"].Send.TrustedForwardedSources.Clear();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await replicator.StartAsync(default);
        try
        {
            while (replicator.AuthorizationRejectedMutations == 0) await Task.Delay(10, timeout.Token);
        }
        finally { await replicator.StopAsync(default); }
        Assert.False(transport.Messages.Reader.TryRead(out _));
        Assert.Equal(0, replicator.QueueStatus.Bytes);
    }

    [Fact]
    public void InvalidQueueBudgetsCannotDisableBounds()
    {
        var options = Options(1);
        Assert.True(RuntimeStateReplicator.IsValidQueueConfiguration(options));
        options.ReplicationMaxMutationBytes = (int)options.ReplicationQueuePerQueueMaxBytes;
        Assert.False(RuntimeStateReplicator.IsValidQueueConfiguration(options));
        options = Options(1);
        options.ReplicationQueueMaxEntries = 0;
        Assert.False(RuntimeStateReplicator.IsValidQueueConfiguration(options));
    }

    private static RuntimeStateOptions Options(int version) => new()
    {
        Enabled = true, NodeId = "source", StateDirectory = Path.Combine(Path.GetTempPath(), "replication-saturation-" + Guid.NewGuid().ToString("N")),
        QueueCapacity = 100, ReplicationQueueMaxEntries = 200, ReplicationQueueMaxBytes = 64 * 1024,
        ReplicationQueuePerQueueMaxBytes = 32 * 1024, ReplicationMaxMutationBytes = 4096,
        Peers = new()
        {
            ["target"] = new()
            {
                Endpoint = "https://target", ProtocolVersion = version, Secret = "replication-saturation-key-32-bytes",
                Send = new() { AllowedDomains = ["ordinary", "cc.access", "cc.triggered"], AllowUngroupedKeys = true }
            }
        }
    };

    private static StaticOptionsMonitor<NodeCommunicationOptions> Communication() => new(new()
    {
        Enabled = true, ClusterId = "cluster", NodeId = "source",
        Peers = new() { ["target"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = "replication-saturation-key-32-bytes" } } }
    });

    private sealed class BlockingCapture : HttpMessageHandler
    {
        internal Channel<RuntimeStateMutation> Messages { get; } = Channel.CreateUnbounded<RuntimeStateMutation>();
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(token);
            var items = request.RequestUri!.AbsolutePath.StartsWith(NodeMessageAuthentication.Prefix, StringComparison.Ordinal)
                ? RuntimeStateBatch.Read(JsonSerializer.Deserialize<NodeMessage>(body, Json)!.Payload)
                : [JsonSerializer.Deserialize<RuntimeStateMutation>(body, Json)!];
            foreach (var item in items) Messages.Writer.TryWrite(item);
            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK);
        }
    }
}
