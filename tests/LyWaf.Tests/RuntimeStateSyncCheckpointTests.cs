using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateSyncCheckpointTests : IDisposable
{
    private const string Domain = "runtime.sync.checkpoint";
    private const string Secret = "sync-checkpoint-injection-test-32-bytes";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sync-fence-" + Guid.NewGuid().ToString("N"));
    private StaticOptionsMonitor<RuntimeStateOptions> Options() => new(new()
    {
        Enabled = true, NodeId = "target", StateDirectory = _root,
        Peers = new() { ["source"] = new() { Secret = Secret, ProtocolVersion = 2,
            Receive = new() { AllowedDomains = [Domain, "test"], AllowUngroupedKeys = true,
                TrustedForwardedSources = ["target"] } } }
    });
    private static RuntimeStateMutation Fence() => new(Domain,
        RuntimeStateStore.SyncStreamKey("cluster", "source", "target"), "target", 0,
        DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(new RuntimeStateSyncAck(
            "cluster", "source", "target", Guid.NewGuid().ToString("N"), 1, new string('A', 64)), RuntimeStateSyncProtocol.Json));

    [Fact]
    public async Task EveryExternalWriteRejectsReservedDomainEvenWhenGranted()
    {
        var options = Options();
        using var store = new RuntimeStateStore(options);
        var fence = Fence();
        Assert.Null(RuntimeStateReplicationAuthorization.AuthorizeReceive(options.CurrentValue, "source", [fence]));
        Assert.Throws<ArgumentException>(() => store.Set(Domain, fence.Key, fence.Value));
        Assert.Throws<ArgumentException>(() => store.Merge(fence));
        Assert.Throws<ArgumentException>(() => store.Remove(Domain, fence.Key));
        Assert.Throws<ArgumentException>(() => store.TryClaim(Domain, fence.Key, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => store.IncrementWindow(Domain, fence.Key, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        options.CurrentValue.Peers["source"].DurableSyncEnabled = true;
        var batch = new RuntimeStateSyncBatch("cluster", "source", "target", Guid.NewGuid().ToString("N"), 1, [fence], "");
        batch = batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };
        await Assert.ThrowsAsync<ArgumentException>(() => new RuntimeStateSyncReceiver(options, store).AcceptAsync(batch, default));
        Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
        Assert.Empty(store.ExportMutations());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedHttpCannotInjectFenceDespiteExplicitGrant(bool v2)
    {
        var options = Options();
        using var store = new RuntimeStateStore(options);
        var communication = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, RequireHttps = false, ClusterId = "cluster", NodeId = "target", StateDirectory = _root,
            Peers = new() { ["source"] = new() { Keys = new() { ["key"] = Secret }, AllowedOperations = ["runtime.merge"] } }
        });
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(s => s.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(options);
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(communication);
        builder.Services.AddSingleton<IRuntimeStateStore>(store);
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<RuntimeStateAuthentication>();
        builder.Services.AddSingleton<RuntimeStateReplicator>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        app.MapNodeCommunication(7030);
        app.MapLyWafRuntimeStateApi(7030);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            var mutation = Fence();
            var now = DateTimeOffset.UtcNow;
            var message = new NodeMessage(2, "cluster", "source", "target", Guid.NewGuid().ToString("D"), "test",
                "runtime.merge", now, now.AddMinutes(1), JsonSerializer.SerializeToElement(new[] { mutation }, RuntimeStateSyncProtocol.Json));
            var body = v2 ? JsonSerializer.SerializeToUtf8Bytes(message, RuntimeStateSyncProtocol.Json)
                : JsonSerializer.SerializeToUtf8Bytes(mutation, RuntimeStateSyncProtocol.Json);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                v2 ? NodeMessageAuthentication.Prefix + "runtime.merge" : "/api/runtime-state/agent/merge")
                { Content = new ByteArrayContent(body) };
            request.Headers.Host = "localhost:7030";
            request.Content.Headers.ContentType = new("application/json");
            if (v2) NodeMessageAuthentication.Sign(request, message, "key", Secret, body);
            else RuntimeStateAuthentication.Sign(request, "source", Secret, body);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
            Assert.Empty(store.ExportMutations());
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task NullWriterCannotConfirmFence()
    {
        using var store = new RuntimeStateStore(Options());
        var method = typeof(RuntimeStateStore).GetMethod("CompleteDurableWriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task)method.Invoke(store, [Fence(), null])!;
        await Assert.ThrowsAsync<IOException>(() => pending);
        Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("key")]
    [InlineData("nodeId")]
    [InlineData("sequence")]
    [InlineData("missing-value")]
    public async Task MalformedJournalFenceFailsClosedAndCannotBeCompacted(string field)
    {
        Directory.CreateDirectory(_root);
        var json = JsonSerializer.SerializeToNode(Fence(), RuntimeStateSyncProtocol.Json)!;
        if (field == "missing-value") json.AsObject().Remove("value");
        else json[field] = field switch { "deleted" => JsonValue.Create(true), "sequence" => JsonValue.Create(7), _ => JsonValue.Create("forged") };
        var path = Path.Combine(_root, "runtime-state.jsonl");
        await File.WriteAllTextAsync(path, json.ToJsonString() + "\n");
        using var store = new RuntimeStateStore(Options());
        Assert.False(store.SyncJournalHealthy);
        Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
        Assert.False(await store.CompactJournalAsync(path, default));
    }

    [Fact]
    public void TamperedSenderFileFailsChecksumInsteadOfAdvancingAck()
    {
        var options = Options();
        options.CurrentValue.Peers["source"].Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true };
        var sender = new RuntimeStateSyncSender(options);
        sender.Prepare("cluster", "target", "source", [new("test", "key", "target", 1,
            DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(1))]);
        var path = Directory.GetFiles(Path.Combine(_root, "runtime-sync"), "out-*.json").Single();
        using (var held = new RuntimeStateSyncFiles(_root).Lock(path))
            Assert.NotNull(sender.ReadPending("cluster", "target", "source"));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["value"]!["acknowledgedSequence"] = 99;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => sender.ReadPending("cluster", "target", "source"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TamperedReceiptOrJournalFenceCannotAcknowledgeDuplicate(bool journal)
    {
        var options = Options();
        options.CurrentValue.Peers["source"].DurableSyncEnabled = true;
        var batch = new RuntimeStateSyncBatch("cluster", "source", "target", Guid.NewGuid().ToString("N"), 1,
            [new("test", "key", "source", 1, DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(1))], "");
        batch = batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };
        using (var store = new RuntimeStateStore(options))
        {
            await store.StartAsync(default);
            await new RuntimeStateSyncReceiver(options, store).AcceptAsync(batch, default);
            await store.StopAsync(default);
        }
        if (journal)
        {
            var path = Path.Combine(_root, "runtime-state.jsonl");
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var json = JsonNode.Parse(lines[i])!;
                if (json["domain"]?.GetValue<string>() != Domain) continue;
                json["value"]!["digest"] = new string('B', 64);
                lines[i] = json.ToJsonString();
            }
            File.WriteAllLines(path, lines);
        }
        else
        {
            var path = Directory.GetFiles(Path.Combine(_root, "runtime-sync"), "in-*.json").Single();
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["value"]!["sequence"] = 99;
            File.WriteAllText(path, json.ToJsonString());
        }
        using var recovered = new RuntimeStateStore(options);
        var receiver = new RuntimeStateSyncReceiver(options, recovered);
        if (journal) Assert.Equal("SnapshotRequired", (await receiver.AcceptAsync(batch, default)).Status);
        else await Assert.ThrowsAsync<InvalidDataException>(() => receiver.AcceptAsync(batch, default));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
