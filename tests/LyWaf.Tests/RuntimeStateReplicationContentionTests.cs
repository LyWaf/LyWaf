using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

// Reproduces a busy shared gate without letting it discard a healthy peer's update.
public sealed class RuntimeStateReplicationContentionTests
{
    [Fact]
    public async Task BusySharedAccountingGateBuffersUpdateAndDeliversToHealthyPeer()
    {
        var options = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "source", StateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            Peers = new() { ["healthy"] = new()
            {
                Endpoint = "http://healthy", Secret = "test-secret",
                Send = new() { AllowedDomains = ["cc.triggered"], AllowUngroupedKeys = true }
            } }
        });
        using var store = new RuntimeStateStore(options);
        var transport = new Capture();
        using var replicator = new RuntimeStateReplicator(store, options, transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await replicator.StartAsync(default);
        try
        {
            store.Set("cc.triggered", "warmup", 1);
            Assert.Equal("warmup", await transport.Keys.Reader.ReadAsync(timeout.Token));
            while (replicator.QueueStatus.Entries != 0) await Task.Delay(1, timeout.Token);
            var pool = typeof(RuntimeStateReplicator).GetField("_queuePool", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(replicator)!;
            var gate = pool.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pool)!;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = Task.Run(() => { lock (gate) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                await Task.Run(() => store.Set("cc.triggered", "lost-before-fanout", 2)).WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { release.Set(); await holder; }
            Assert.True(store.TryGet<int>("cc.triggered", "lost-before-fanout", out _));
            Assert.Equal("lost-before-fanout", await transport.Keys.Reader.ReadAsync(timeout.Token));
            while (replicator.QueueStatus.Entries != 0) await Task.Delay(1, timeout.Token);
            Assert.Equal(0, replicator.QueueStatus.ContentionDropped);
            Assert.Equal(1, replicator.QueueStatus.ContentionBuffered);
            Assert.Equal(0, replicator.QueueStatus.CapacityDropped);
            Assert.Equal(0, replicator.QueueStatus.Entries);
            Assert.False(transport.Keys.Reader.TryRead(out _));
        }
        finally { await replicator.StopAsync(CancellationToken.None); }
    }

    private sealed class Capture : HttpMessageHandler
    {
        internal Channel<string> Keys { get; } = Channel.CreateUnbounded<string>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(token);
            var mutation = JsonSerializer.Deserialize<RuntimeStateMutation>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Keys.Writer.TryWrite(mutation.Key);
            return new(HttpStatusCode.OK);
        }
    }
}
