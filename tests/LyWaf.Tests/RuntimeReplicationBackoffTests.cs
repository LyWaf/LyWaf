using System.Net;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeReplicationBackoffTests
{
    [Fact]
    public async Task RejectedDurablePeerDoesNotSpinThroughQueuedChangesAndStopCancelsBackoff()
    {
        var root = Path.Combine(Path.GetTempPath(), "runtime-backoff-" + Guid.NewGuid().ToString("N"));
        var runtime = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "source", StateDirectory = root,
            Peers = new() { ["target"] = new() { Endpoint = "https://target", ProtocolVersion = 2, DurableSyncEnabled = true,
                Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true } } }
        });
        var communication = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, NodeId = "source", ClusterId = "cluster",
            Peers = new() { ["target"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('s', 32) },
                AllowedOperations = [RuntimeStateSyncProtocol.Operation] } }
        });
        try
        {
            using var store = new RuntimeStateStore(runtime);
            using var transport = new RejectedPeer();
            using var replicator = new RuntimeStateReplicator(store, runtime, transport, communication);
            await replicator.StartAsync(default);
            try
            {
                store.Set("test", "first", "value");
                await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (var i = 0; i < 20; i++) store.Set("test", "queued-" + i, "value");
                await Task.Delay(200);
                Assert.Equal(1, Volatile.Read(ref transport.Calls));
                Assert.NotNull(new RuntimeStateSyncSender(runtime).ReadPending("cluster", "source", "target"));
            }
            finally { await replicator.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(2)); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class RejectedPeer : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("""{"errorCode":"node_runtime_store_unavailable","retryable":true}""") });
        }
    }
}
