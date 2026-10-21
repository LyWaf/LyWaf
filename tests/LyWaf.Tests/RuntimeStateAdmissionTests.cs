using System.Net;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runtime-admission-" + Guid.NewGuid().ToString("N"));
    private StaticOptionsMonitor<RuntimeStateOptions> Runtime(int version = 2, bool durable = false) => new(new()
    {
        Enabled = true, NodeId = "source", StateDirectory = _root,
        Peers = new() { ["target"] = new() { Endpoint = "https://target", ProtocolVersion = version,
            DurableSyncEnabled = durable, Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true } } }
    });
    private static StaticOptionsMonitor<NodeCommunicationOptions> Communication(string state) => new(new()
    {
        Enabled = true, AllowLegacyProtocol = true, NodeId = "source", ClusterId = "cluster",
        Peers = new() { ["target"] = new() { AdmissionState = state, ActiveKeyId = "key",
            Keys = new() { ["key"] = new string('s', 32) } } }
    });
    private static RuntimeStateMutation Mutation(int version) => new("test", "key", "source", version,
        DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(version));

    [Theory]
    [InlineData("PendingApproval", 1)]
    [InlineData("Draining", 1)]
    [InlineData("Revoked", 1)]
    [InlineData("PendingApproval", 2)]
    [InlineData("Draining", 2)]
    [InlineData("Revoked", 2)]
    public async Task ReplicatorDoesNotSendUntilPeerIsActive(string state, int version)
    {
        var runtime = Runtime(version);
        var communication = Communication(state);
        using var store = new RuntimeStateStore(runtime);
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var replicator = new RuntimeStateReplicator(store, runtime, handler, communication);
        await replicator.StartAsync(default);
        try
        {
            store.Set("test", "key", 1);
            await WaitAsync(() => replicator.AuthorizationRejectedMutations > 0);
            Assert.Equal(0, handler.Calls);
            communication.CurrentValue.Peers["target"].AdmissionState = "Active";
            store.Set("test", "key", 2);
            await WaitAsync(() => handler.Calls > 0);
        }
        finally { await replicator.StopAsync(default); }
    }

    [Theory]
    [InlineData("PendingApproval")]
    [InlineData("Draining")]
    [InlineData("Revoked")]
    public async Task DurableRetryPreservesPendingWhenAdmissionIsDenied(string state)
    {
        var runtime = Runtime(durable: true);
        var sender = new RuntimeStateSyncSender(runtime);
        var pending = sender.Prepare("cluster", "source", "target", [Mutation(1)]);
        var handler = new Handler(_ => new(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var transport = new RuntimeStateSyncTransport(client, runtime, Communication(state));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync("target", null, default));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(pending.Digest, sender.ReadPending("cluster", "source", "target")!.Digest);
    }

    [Fact]
    public async Task RevocationAfterGapResponsePreventsHistoryReplay()
    {
        var runtime = Runtime(durable: true);
        var communication = Communication("Active");
        var sender = new RuntimeStateSyncSender(runtime);
        var first = sender.Prepare("cluster", "source", "target", [Mutation(1)]);
        sender.Confirm(first, new("cluster", "source", "target", first.Epoch, first.Sequence, first.Digest));
        var pending = sender.Prepare("cluster", "source", "target", [Mutation(2)]);
        var handler = new Handler(_ =>
        {
            communication.CurrentValue.Peers["target"].AdmissionState = "Revoked";
            return new(HttpStatusCode.Conflict) { Content = new StringContent(JsonSerializer.Serialize(
                new RuntimeStateSyncAck("cluster", "source", "target", pending.Epoch, 0, "", "Gap", 1), RuntimeStateSyncProtocol.Json)) };
        });
        using var client = new HttpClient(handler);
        var transport = new RuntimeStateSyncTransport(client, runtime, communication);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync("target", null, default));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(pending.Digest, sender.ReadPending("cluster", "source", "target")!.Digest);
    }

    [Theory]
    [InlineData("PendingApproval", true, true, false)]
    [InlineData("Draining", true, true, false)]
    [InlineData("Revoked", true, true, false)]
    [InlineData("Active", false, true, false)]
    [InlineData("Active", true, true, true)]
    [InlineData("Revoked", false, false, true)]
    public async Task LegacyCannotBypassAnyRestrictedRegisteredPeer(string state, bool peerEnabled,
        bool communicationEnabled, bool accepted)
    {
        const string secret = "legacy-runtime-test-secret";
        var runtime = Runtime(version: 1);
        runtime.CurrentValue.Peers["target"].Secret = secret;
        runtime.CurrentValue.Peers["target"].Receive = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true };
        var communication = Communication("Active");
        communication.CurrentValue.Enabled = communicationEnabled;
        // The signed sender is Active; a different restricted peer still closes the shared-key legacy path.
        communication.CurrentValue.Peers["restricted"] = new() { AdmissionState = state, Enabled = peerEnabled };
        var mutation = Mutation(1) with { NodeId = "target" };
        var body = JsonSerializer.SerializeToUtf8Bytes(mutation, RuntimeStateSyncProtocol.Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/runtime-state/agent/merge");
        RuntimeStateAuthentication.Sign(request, "target", secret, body);
        var context = new DefaultHttpContext();
        foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
        using var stream = new MemoryStream(body);
        context.Request.Body = stream;
        var result = await new RuntimeStateAuthentication(runtime, communication).ReadAndVerifyAsync(context, default);
        if (accepted)
        {
            Assert.NotNull(result.Value);
            Assert.Null(result.Error);
        }
        else
        {
            Assert.Null(result.Value);
            Assert.Equal("runtime_peer_forbidden", result.Error);
            Assert.Equal(0, stream.Position);
            Assert.False(Directory.Exists(Path.Combine(_root, "replay")));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(request));
        }
    }
    private static async Task WaitAsync(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!completed() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(completed());
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
