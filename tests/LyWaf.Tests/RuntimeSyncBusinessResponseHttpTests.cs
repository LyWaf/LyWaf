using System.Net;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeSyncBusinessResponseHttpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sync-business-http-" + Guid.NewGuid().ToString("N"));
    private const string Secret = "sync-business-http-test-key-123456789";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private StaticOptionsMonitor<RuntimeStateOptions> Runtime(string id, string peer) => new(new()
    {
        Enabled = true, NodeId = id, StateDirectory = Path.Combine(_root, id),
        Peers = new() { [peer] = new() { ProtocolVersion = 2, DurableSyncEnabled = true,
            Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true },
            Receive = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true } } }
    });
    private StaticOptionsMonitor<NodeCommunicationOptions> Protocol(string id, string peer) => new(new()
    {
        Enabled = true, ClusterId = "cluster", NodeId = id, RequireHttps = false,
        StateDirectory = Path.Combine(_root, "protocol-" + id),
        Peers = new() { [peer] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = Secret }, AllowedOperations = ["runtime.sync"] } }
    });
    private static RuntimeStateMutation Mutation(int value) => new("test", "key", "source", value,
        DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(value));
    private static RuntimeStateSyncBatch Rehash(RuntimeStateSyncBatch batch) => batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };

    [Fact]
    public async Task ProductionRouteAddsRejectionMetadataAndTransportStillRepairsGapOverHttp200()
    {
        var target = Runtime("target", "source");
        using var store = new RuntimeStateStore(target);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(target);
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(Protocol("target", "source"));
        builder.Services.AddSingleton(store);
        builder.Services.AddHostedService(provider => provider.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddSingleton<RuntimeStateSyncReceiver>();
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        await using var app = builder.Build();
        app.MapNodeCommunication(12345);
        await app.StartAsync();
        try
        {
            using var handler = new WireCapture { InnerHandler = new SocketsHttpHandler { UseProxy = false } };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(Assert.Single(app.Urls)), Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Host = "localhost:12345";
            var source = Runtime("source", "target");
            source.CurrentValue.Peers["target"].Endpoint = client.BaseAddress.ToString();
            var sender = new RuntimeStateSyncSender(source);
            // Model a receiver losing its receipt history while the sender retains acknowledged ranges.
            var first = sender.Prepare("cluster", "source", "target", [Mutation(1)]);
            sender.Confirm(first, new("cluster", "source", "target", first.Epoch, first.Sequence, first.Digest));
            var second = sender.Prepare("cluster", "source", "target", [Mutation(2)]);
            sender.Confirm(second, new("cluster", "source", "target", second.Epoch, second.Sequence, second.Digest));
            var pending = sender.Prepare("cluster", "source", "target", [Mutation(3)]);

            var gap = await Send(client, pending);
            AssertBusinessFailure(gap, "Gap", "node_sync_gap");
            Assert.Equal(1, gap.GetProperty("expectedSequence").GetInt64());
            Assert.Equal(0, gap.GetProperty("sequence").GetInt64());
            Assert.Equal(pending.Epoch, gap.GetProperty("epoch").GetString());

            var transport = new RuntimeStateSyncTransport(client, source, Protocol("source", "target"));
            await transport.SendAsync("target", null, default);
            Assert.Null(sender.ReadPending("cluster", "source", "target"));
            Assert.True(store.TryGet<int>("test", "key", out var actual));
            Assert.Equal(3, actual);
            Assert.Contains(handler.Bodies, body => body.GetProperty("status").GetString() == "Gap"
                && body.GetProperty("errorCode").GetString() == "node_sync_gap");
            foreach (var sequence in new long[] { 1, 2, 3 })
                Assert.Contains(handler.Bodies, body => body.GetProperty("status").GetString() == "Applied"
                    && body.GetProperty("sequence").GetInt64() == sequence && body.GetProperty("success").GetBoolean());

            var conflict = await Send(client, Rehash(first with { Mutations = [Mutation(99)] }));
            AssertBusinessFailure(conflict, "Conflict", "node_sync_conflict");
            var snapshot = await Send(client, Rehash(pending with { Epoch = Guid.NewGuid().ToString("N") }));
            AssertBusinessFailure(snapshot, "SnapshotRequired", "node_sync_snapshot_required");
            Assert.Equal(pending.Epoch, snapshot.GetProperty("epoch").GetString());
            Assert.Equal(4, snapshot.GetProperty("expectedSequence").GetInt64());
            Assert.True(store.TryGet<int>("test", "key", out actual));
            Assert.Equal(3, actual);
            Assert.All(handler.Statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task StoppedJournalReturnsRetryable503WithoutApplyingOrAcknowledgingTheBatch()
    {
        var target = Runtime("target", "source");
        using var store = new RuntimeStateStore(target);
        var source = Runtime("source", "target");
        var sender = new RuntimeStateSyncSender(source);
        var batch = sender.Prepare("cluster", "source", "target", [Mutation(1)]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(target);
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(Protocol("target", "source"));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<RuntimeStateSyncReceiver>();
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        await using (var app = builder.Build())
        {
            app.MapNodeCommunication(12345);
            await app.StartAsync();
            try
            {
                await store.StartAsync(default);
                await store.StopAsync(default);
                using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
                client.DefaultRequestHeaders.Host = "localhost:12345";
                var response = await Send(client, batch, HttpStatusCode.ServiceUnavailable);
                Assert.Equal("node_runtime_store_unavailable", response.GetProperty("errorCode").GetString());
                Assert.True(response.GetProperty("retryable").GetBoolean());
                Assert.False(response.GetProperty("success").GetBoolean());
                Assert.False(store.TryGet<int>("test", "key", out _));
                Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
                Assert.Equal(0, store.GetStatus().QueueDepth);
                Assert.Equal(0, store.GetStatus().JournalQueueBytes);
                Assert.Equal(batch.Digest, sender.ReadPending("cluster", "source", "target")!.Digest);
            }
            finally { await app.StopAsync(); }
        }
        using var restarted = new RuntimeStateStore(target);
        await restarted.StartAsync(default);
        try
        {
            var ack = await new RuntimeStateSyncReceiver(target, restarted).AcceptAsync(batch, default);
            sender.Confirm(batch, ack);
            Assert.Null(sender.ReadPending("cluster", "source", "target"));
            Assert.True(restarted.TryGet<int>("test", "key", out var actual));
            Assert.Equal(1, actual);
        }
        finally { await restarted.StopAsync(default); }
    }

    private static void AssertBusinessFailure(JsonElement body, string status, string code)
    {
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(status, body.GetProperty("status").GetString());
        Assert.Equal(code, body.GetProperty("errorCode").GetString());
        Assert.Equal("node.errors." + code, body.GetProperty("messageKey").GetString());
        Assert.False(body.GetProperty("retryable").GetBoolean()); // Repair, not blind retransmission.
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("messageId").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("operationId").GetString()));
        Assert.Equal("target", body.GetProperty("receiverNodeId").GetString());
    }

    private static async Task<JsonElement> Send(HttpClient client, RuntimeStateSyncBatch batch, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new NodeMessage(2, "cluster", "source", "target", Guid.NewGuid().ToString("D"), batch.Epoch,
            "runtime.sync", now, now.AddMinutes(1), JsonSerializer.SerializeToElement(batch, Json));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, NodeMessageAuthentication.Prefix + "runtime.sync")
        { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        NodeMessageAuthentication.Sign(request, message, "key", Secret, bytes);
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private sealed class WireCapture : DelegatingHandler
    {
        public List<JsonElement> Bodies { get; } = [];
        public List<HttpStatusCode> Statuses { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            Statuses.Add(response.StatusCode);
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(token)));
            return response;
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
