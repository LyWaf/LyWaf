using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateReplicationAuthorizationTests : IDisposable
{
    private const string Secret = "runtime-authorization-test-secret-32-bytes";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "runtime-acl-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private RuntimeStateOptions Options(string node = "receiver") => new()
    {
        Enabled = true, NodeId = node, StateDirectory = Path.Combine(directory, node),
        ProtectionGroupKeyPrefixes = new() { ["blue"] = ["v2:blue:"], ["red"] = ["v2:red:"] },
        Peers = new() { ["relay"] = new() { Endpoint = "https://relay", Secret = Secret, Receive = Policy(), Send = Policy() } }
    };

    private static RuntimeStateReplicationPolicy Policy() => new()
    {
        AllowedDomains = ["cc.access"], AllowedProtectionGroups = ["blue"],
        TrustedForwardedSources = ["source-a", "source-b"]
    };

    private static string Component(string source) => JsonSerializer.Serialize(new[] { source, new string('a', 32) });

    private static RuntimeStateMutation Mutation(string source = "relay", string key = "v2:blue:192.0.2.1|1") => new(
        "cc.access", key, source, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(2), false, null,
        new Dictionary<string, long> { [Component(source)] = 40 });

    [Fact]
    public void EmptyPolicyDeniesLegacyAndV2RatherThanInferringClusterTrust()
    {
        var options = Options();
        options.Peers["relay"] = new();
        Assert.Equal("runtime_domain_forbidden", Receive(options, Mutation()));
        Assert.Equal("runtime_domain_forbidden", RuntimeStateReplicationAuthorization.AuthorizeSend(options, "relay", Mutation("receiver")));
        options.Peers.Clear();
        Assert.Equal("runtime_peer_forbidden", Receive(options, Mutation()));
    }

    [Theory]
    [InlineData("domain", "runtime_domain_forbidden")]
    [InlineData("group", "runtime_group_forbidden")]
    [InlineData("ungrouped", "runtime_group_forbidden")]
    [InlineData("source", "runtime_source_forbidden")]
    [InlineData("counter", "runtime_counter_source_forbidden")]
    [InlineData("malformed", "runtime_counter_source_forbidden")]
    [InlineData("case", "runtime_source_forbidden")]
    public void RejectsCrossDomainGroupAndForgedOrigins(string kind, string expected)
    {
        var mutation = Bad(kind);
        Assert.Equal(expected, Receive(Options(), mutation));
        Assert.Equal(expected, RuntimeStateReplicationAuthorization.AuthorizeSend(Options("relay"), "relay", mutation));
    }

    private static RuntimeStateMutation Bad(string kind) => kind switch
    {
        "domain" => Mutation() with { Domain = "ip.blocked" },
        "group" => Mutation(key: "v2:red:192.0.2.1|1"),
        "ungrouped" => Mutation(key: "unmapped:ip"),
        "source" => Mutation("victim"),
        "counter" => Mutation() with { CounterComponents = new Dictionary<string, long> { [Component("victim")] = 99 } },
        "case" => Mutation("RELAY"),
        _ => Mutation() with { CounterComponents = new Dictionary<string, long> { ["[\"relay\",\"invalid-epoch\"]"] = 1 } }
    };

    private static string? Receive(RuntimeStateOptions options, params RuntimeStateMutation[] mutations)
        => RuntimeStateReplicationAuthorization.AuthorizeReceive(options, "relay", mutations);

    [Fact]
    public void TrustedRelayPreservesEveryCounterComponentAndOriginalMutationIdentity()
    {
        var mutation = Mutation("source-a") with
        {
            CounterComponents = new Dictionary<string, long>
            { [Component("source-a")] = 40, [Component("source-b")] = 35, [Component("relay")] = 30 }
        };
        Assert.Null(Receive(Options(), mutation));
        Assert.Null(RuntimeStateReplicationAuthorization.AuthorizeSend(Options("relay"), "relay", mutation));
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(Options()));
        store.Merge(mutation);
        store.Merge(mutation);
        Assert.Equal(105, Assert.Single(store.GetSnapshots()).CounterValue);
        Assert.Equal("source-a", Assert.Single(store.ExportMutations()).NodeId);
        Assert.Equal(3, Assert.Single(store.ExportMutations()).CounterComponents!.Count);
    }

    [Fact]
    public void GenerationWrapperCannotRelabelProtectionGroupAndExactLegacyComponentsAreSupported()
    {
        var options = Options();
        Assert.Null(Receive(options, Mutation(key: $"ccg:2:{new string('a', 32)}:v2:blue:ip|1")));
        Assert.Equal("runtime_group_forbidden", Receive(options, Mutation(key: $"ccg:2:{new string('a', 32)}:v2:red:ip|1")));
        Assert.Equal("runtime_group_forbidden", Receive(options, Mutation(key: "ccg:broken:v2:blue:ip")));
        Assert.Null(Receive(options, Mutation() with { CounterComponents = new Dictionary<string, long> { ["relay"] = 40 } }));
        Assert.Equal("runtime_counter_source_forbidden", Receive(options,
            Mutation() with { CounterComponents = new Dictionary<string, long> { ["relay-arbitrary-epoch"] = 40 } }));
        options.Peers["relay"].Receive.AllowUngroupedKeys = true;
        Assert.Null(Receive(options, Mutation(key: "legacy-key")));
        Assert.Equal("runtime_group_forbidden", Receive(options, Mutation(key: "v2:red:ip")));
    }

    [Fact]
    public void ConfigurationRejectsAmbiguousBindingsUnknownGroupsAndWildcardAuthority()
    {
        var options = Options();
        Assert.True(RuntimeStateReplicationAuthorization.IsValidConfiguration(options));
        options.ProtectionGroupKeyPrefixes["red"] = ["v2:"];
        Assert.False(RuntimeStateReplicationAuthorization.IsValidConfiguration(options));
        Assert.Equal("runtime_group_ambiguous", Receive(options, Mutation()));
        options = Options();
        options.Peers["relay"].Receive.AllowedProtectionGroups = ["missing"];
        Assert.False(RuntimeStateReplicationAuthorization.IsValidConfiguration(options));
        options = Options();
        options.Peers["relay"].Receive.TrustedForwardedSources = ["*"];
        Assert.False(RuntimeStateReplicationAuthorization.IsValidConfiguration(options));
        options = Options();
        Assert.Equal("runtime_peer_forbidden", RuntimeStateReplicationAuthorization.AuthorizeReceive(options, "RELAY", [Mutation()]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealEndpointsEnforceAuthorizationAndKeepLegitimateRelays(bool v2)
    {
        var runtime = Options();
        var protocol = new NodeCommunicationOptions
        {
            Enabled = true, RequireHttps = false, ClusterId = "cluster", NodeId = "receiver", StateDirectory = directory,
            Peers = new() { ["relay"] = new() { Keys = new() { ["key"] = Secret }, AllowedOperations = ["runtime.merge"] } }
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new StaticOptionsMonitor<RuntimeStateOptions>(runtime));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new StaticOptionsMonitor<NodeCommunicationOptions>(protocol));
        builder.Services.AddSingleton<IRuntimeStateStore, RuntimeStateStore>();
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<RuntimeStateAuthentication>();
        builder.Services.AddSingleton<RuntimeStateReplicator>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        app.MapNodeCommunication(7030);
        app.MapLyWafRuntimeStateApi(7030);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(5) };
            var store = app.Services.GetRequiredService<IRuntimeStateStore>();
            foreach (var kind in new[] { "domain", "group", "source", "counter", "malformed", "ungrouped" })
            {
                using var request = Request(v2 ? [Mutation(), Bad(kind)] : [Bad(kind)]);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Empty(store.GetSnapshots());
            }
            using var accepted = await client.SendAsync(Request([Mutation("source-a")]));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal("source-a", Assert.Single(store.GetSnapshots()).NodeId);
            runtime.Peers["relay"].Receive.TrustedForwardedSources.Clear();
            using var revoked = await client.SendAsync(Request([Mutation("source-a")]));
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);

            HttpRequestMessage Request(RuntimeStateMutation[] mutations)
            {
                var now = DateTimeOffset.UtcNow;
                var envelope = new NodeMessage(2, "cluster", "relay", "receiver", Guid.NewGuid().ToString("D"), "state",
                    "runtime.merge", now, now.AddMinutes(1), JsonSerializer.SerializeToElement(mutations, Json));
                var body = v2 ? JsonSerializer.SerializeToUtf8Bytes(envelope, Json) : JsonSerializer.SerializeToUtf8Bytes(mutations[0], Json);
                var request = new HttpRequestMessage(HttpMethod.Post,
                    v2 ? NodeMessageAuthentication.Prefix + "runtime.merge" : "/api/runtime-state/agent/merge") { Content = new ByteArrayContent(body) };
                request.Headers.Host = "localhost:7030";
                request.Content.Headers.ContentType = new("application/json");
                if (v2) NodeMessageAuthentication.Sign(request, envelope, "key", Secret, body);
                else RuntimeStateAuthentication.Sign(request, "relay", Secret, body);
                return request;
            }
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StartupAntiEntropyFiltersUnauthorizedDataButForwardsTrustedOrigins(int version)
    {
        var options = Options("relay");
        options.Peers["target"] = options.Peers["relay"];
        options.Peers.Remove("relay");
        options.Peers["target"].Endpoint = "https://target";
        options.Peers["target"].ProtocolVersion = version;
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(options));
        store.Merge(Mutation("source-a"));
        store.Merge(Bad("group"));
        store.Merge(Bad("domain"));
        store.Merge(Mutation("victim", "v2:blue:victim|1"));
        var communication = new NodeCommunicationOptions
        {
            Enabled = true, NodeId = "relay", ClusterId = "cluster",
            Peers = new() { ["target"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = Secret } } }
        };
        var transport = new CaptureTransport();
        using var replicator = new RuntimeStateReplicator(store, new StaticOptionsMonitor<RuntimeStateOptions>(options),
            transport, new StaticOptionsMonitor<NodeCommunicationOptions>(communication));
        await replicator.StartAsync(default);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var delivered = await transport.Messages.Reader.ReadAsync(timeout.Token);
            Assert.Equal("source-a", delivered.NodeId);
            while (replicator.AuthorizationRejectedMutations < 3) await Task.Delay(10, timeout.Token);
        }
        finally { await replicator.StopAsync(default); }
        Assert.False(transport.Messages.Reader.TryRead(out _));
    }

    [Fact]
    public void ConfigurationBindsSeparateDirectionalPolicies()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RuntimeState:ProtectionGroupKeyPrefixes:blue:0"] = "v2:blue:",
            ["RuntimeState:Peers:relay:Send:AllowedDomains:0"] = "cc.access",
            ["RuntimeState:Peers:relay:Send:AllowedProtectionGroups:0"] = "blue",
            ["RuntimeState:Peers:relay:Receive:AllowedDomains:0"] = "ip.blocked",
            ["RuntimeState:Peers:relay:Receive:AllowUngroupedKeys"] = "true",
            ["RuntimeState:Peers:relay:Receive:TrustedForwardedSources:0"] = "source-a"
        }).Build();
        var options = configuration.GetSection("RuntimeState").Get<RuntimeStateOptions>()!;
        Assert.True(RuntimeStateReplicationAuthorization.IsValidConfiguration(options));
        Assert.Equal("cc.access", Assert.Single(options.Peers["relay"].Send.AllowedDomains));
        Assert.Equal("ip.blocked", Assert.Single(options.Peers["relay"].Receive.AllowedDomains));
        Assert.Empty(options.Peers["relay"].Send.TrustedForwardedSources);
        Assert.True(options.Peers["relay"].Receive.AllowUngroupedKeys);
    }

    [Theory]
    [InlineData("[ \"relay\", \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" ]")]
    [InlineData("[\"relay\",\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"]")]
    [InlineData("[\"relay\",\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"victim\"]")]
    public void NonCanonicalOrExtendedCounterIdentityCannotCreateAliasComponents(string component)
    {
        Assert.Equal("runtime_counter_source_forbidden", Receive(Options(), Mutation() with
        { CounterComponents = new Dictionary<string, long> { [component] = 1 } }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RevocationWhilePeerIsSlowIsRecheckedBeforeQueuedSend(int version)
    {
        var options = Options("source-a");
        options.Peers["relay"].ProtocolVersion = version;
        var communication = new NodeCommunicationOptions
        {
            Enabled = true, NodeId = "source-a", ClusterId = "cluster",
            Peers = new() { ["relay"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = Secret } } }
        };
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(options));
        var transport = new BlockingTransport();
        using var replicator = new RuntimeStateReplicator(store, new StaticOptionsMonitor<RuntimeStateOptions>(options),
            transport, new StaticOptionsMonitor<NodeCommunicationOptions>(communication));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await replicator.StartAsync(default);
        try
        {
            store.Set("cc.access", "v2:blue:first", 1);
            await transport.Started.Task.WaitAsync(timeout.Token);
            store.Set("cc.access", "v2:blue:queued", 2);
            // Establish that fanout has processed the preceding item while the first HTTP call is blocked.
            store.Set("not.allowed", "v2:blue:barrier", 3);
            while (replicator.AuthorizationRejectedMutations == 0) await Task.Delay(10, timeout.Token);
            options.Peers["relay"].Send = new();
            transport.Release.TrySetResult();
            while (replicator.AuthorizationRejectedMutations < 2) await Task.Delay(10, timeout.Token);
        }
        finally
        {
            transport.Release.TrySetResult();
            await replicator.StopAsync(default);
        }
        Assert.Equal(1, transport.Calls);
    }

    private sealed class BlockingTransport : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void BatchPackingRechecksEveryQueuedMutation()
    {
        var options = Options("relay");
        var queue = Channel.CreateUnbounded<RuntimeStateMutation>();
        queue.Writer.TryWrite(Bad("group"));
        queue.Writer.TryWrite(Bad("counter"));
        queue.Writer.TryWrite(Mutation("source-a"));
        var now = DateTimeOffset.UtcNow;
        var envelope = new NodeMessage(2, "cluster", "relay", "receiver", Guid.NewGuid().ToString("D"), "state",
            "runtime.merge", now, now.AddMinutes(1), default);
        var batch = RuntimeStateBatch.Pack(envelope, Mutation(), queue.Reader, 4096,
            next => RuntimeStateReplicationAuthorization.AuthorizeSend(options, "relay", next) is null);
        Assert.Equal(2, RuntimeStateBatch.Read(batch.Message.Payload).Length);
        Assert.False(queue.Reader.TryRead(out _));
    }

    private sealed class CaptureTransport : HttpMessageHandler
    {
        public Channel<RuntimeStateMutation> Messages { get; } = Channel.CreateUnbounded<RuntimeStateMutation>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(token);
            var items = request.RequestUri!.AbsolutePath.StartsWith(NodeMessageAuthentication.Prefix, StringComparison.Ordinal)
                ? RuntimeStateBatch.Read(JsonSerializer.Deserialize<NodeMessage>(body, Json)!.Payload)
                : [JsonSerializer.Deserialize<RuntimeStateMutation>(body, Json)!];
            foreach (var item in items) Messages.Writer.TryWrite(item);
            return new(HttpStatusCode.OK);
        }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
