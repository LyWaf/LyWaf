using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using LyWaf.Services.RuntimeState;
using Xunit;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Http;

namespace LyWaf.Tests;

public sealed class RuntimeStateReplicatorTests
{
    [Fact]
    public async Task V2ReplicationPassesPeerAuthenticationAndMergesCounter()
    {
        const string secret = "runtime-v2-test-key-at-least-32-bytes";
        var directory = Path.Combine(Path.GetTempPath(), "runtime-v2-" + Guid.NewGuid().ToString("N"));
        var options = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "source", StateDirectory = directory,
            Peers = new() { ["target"] = new() { Endpoint = "https://target", ProtocolVersion = 2,
                Send = new() { AllowedDomains = ["cc.access"], AllowUngroupedKeys = true } } }
        });
        var sender = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "source",
            Peers = new() { ["target"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = secret } } }
        });
        var receiver = new NodeMessageAuthentication(new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "target", StateDirectory = directory,
            Peers = new() { ["source"] = new() { Keys = new() { ["key"] = secret }, AllowedOperations = ["runtime.merge"] } }
        }));
        using var store = new RuntimeStateStore(options);
        using var target = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(new() { NodeId = "target", StateDirectory = directory }));
        var transport = new V2Transport(receiver, target);
        using var replicator = new RuntimeStateReplicator(store, options, transport, sender);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await replicator.StartAsync(default);
        try
        {
            var start = DateTimeOffset.UtcNow;
            store.IncrementWindow("cc.access", "rule:ip", start, TimeSpan.FromMinutes(1), 40);
            await transport.Delivered.Task.WaitAsync(timeout.Token);
            Assert.Equal(40, target.GetWindowValue("cc.access", "rule:ip", start));
        }
        finally
        {
            await replicator.StopAsync(default);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class V2Transport(NodeMessageAuthentication auth, RuntimeStateStore target) : HttpMessageHandler
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = request.Method.Method;
            context.Request.Scheme = request.RequestUri!.Scheme;
            context.Request.Path = request.RequestUri.AbsolutePath;
            foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
            using var body = new MemoryStream(await request.Content!.ReadAsByteArrayAsync(token));
            context.Request.Body = body;
            var (message, error) = await auth.ReadAsync(context, "runtime.merge", token);
            Assert.Null(error);
            foreach (var mutation in RuntimeStateBatch.Read(message!.Payload)) target.Merge(mutation);
            Delivered.TrySetResult();
            return new(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowPeerDoesNotDelayHealthyPeerAndShutdownCancelsPendingSend(bool fillSlowQueue)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-replication-" + Guid.NewGuid().ToString("N"));
        var options = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "source", SharedSecret = "test-secret", StateDirectory = directory, QueueCapacity = 100,
            Peers = new()
            {
                ["slow"] = new() { Endpoint = "http://slow",
                    Send = new() { AllowedDomains = ["cc.triggered"], AllowUngroupedKeys = true } },
                ["healthy"] = new() { Endpoint = "http://healthy",
                    Send = new() { AllowedDomains = ["cc.triggered"], AllowUngroupedKeys = true } }
            }
        });
        using var store = new RuntimeStateStore(options);
        var transport = new PeerTransport();
        using var replicator = new RuntimeStateReplicator(store, options, transport);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await replicator.StartAsync(timeout.Token);
        var waitingFor = "slow-start";
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            store.Set("cc.triggered", "rule:ip", "blocked");
            await transport.SlowStarted.Task.WaitAsync(timeout.Token);
            waitingFor = "rule:ip";
            Assert.Equal("rule:ip", (await transport.Delivered.Reader.ReadAsync(timeout.Token)).Key);
            store.Set("cc.triggered", "rule:second-ip", "blocked");
            waitingFor = "rule:second-ip";
            Assert.Equal("rule:second-ip", (await transport.Delivered.Reader.ReadAsync(timeout.Token)).Key);
            if (fillSlowQueue)
            {
                for (var i = 0; i < 110; i++)
                {
                    waitingFor = $"rule:{i}";
                    store.Set("cc.triggered", $"rule:{i}", "blocked");
                    Assert.Equal($"rule:{i}", (await transport.Delivered.Reader.ReadAsync(timeout.Token)).Key);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Delivery timed out awaiting={waitingFor}, elapsed={elapsed.Elapsed}: {replicator.QueueStatus}; authorizationRejected={replicator.AuthorizationRejectedMutations}");
        }
        finally { await replicator.StopAsync(CancellationToken.None); }
        Assert.True(transport.SlowCancelled);
        if (fillSlowQueue) Assert.True(replicator.DroppedMutations > 0);
        else Assert.Equal(0, replicator.DroppedMutations);
    }

    [Fact]
    public void ThreeNodeCountersConvergeWithoutDoubleCountingReplicatedTotals()
    {
        var stores = new[] { "a", "b", "c" }.Select(id => new RuntimeStateStore(
            new StaticOptionsMonitor<RuntimeStateOptions>(new()
            { NodeId = id, StateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }))).ToArray();
        try
        {
            var start = DateTimeOffset.UtcNow;
            var amounts = new[] { 40, 35, 30 };
            for (var i = 0; i < stores.Length; i++)
                stores[i].IncrementWindow("cc.access", "rule:ip", start, TimeSpan.FromSeconds(10), amounts[i]);
            var mutations = stores.SelectMany(store => store.ExportMutations()).ToArray();
            foreach (var store in stores)
                foreach (var mutation in mutations.Reverse().Concat(mutations)) store.Merge(mutation);
            // Re-exporting an already merged view must not multiply the global total.
            foreach (var store in stores)
                foreach (var mutation in stores[0].ExportMutations()) store.Merge(mutation);
            Assert.All(stores, store => Assert.Equal(105, store.GetWindowValue("cc.access", "rule:ip", start)));
        }
        finally { foreach (var store in stores) store.Dispose(); }
    }

    private sealed class PeerTransport : HttpMessageHandler
    {
        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<RuntimeStateMutation> Delivered { get; } = Channel.CreateUnbounded<RuntimeStateMutation>();
        public bool SlowCancelled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.True(request.Headers.Contains("X-LyWaf-Signature"));
            if (request.RequestUri!.Host == "slow")
            {
                SlowStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { SlowCancelled = true; throw; }
            }
            var body = await request.Content!.ReadAsStringAsync(token);
            var mutation = JsonSerializer.Deserialize<RuntimeStateMutation>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Delivered.Writer.TryWrite(mutation);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
