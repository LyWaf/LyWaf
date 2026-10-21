using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed partial class PluginNodeMessageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plugin-node-" + Guid.NewGuid().ToString("N"));
    private readonly Manager _manager = new();
    private readonly StaticOptionsMonitor<PluginNodeMessageOptions> _policy = new(new()
    {
        Enabled = true, Groups = new() { ["workers"] = ["edge"] },
        Grants = new() { ["sample"] = new() { SendGroups = ["workers"], ReceiveGroups = ["workers"], AllowedSenders = ["control"] } }
    });
    private readonly StaticOptionsMonitor<NodeCommunicationOptions> _receiver;
    private readonly StaticOptionsMonitor<NodeCommunicationOptions> _sender;
    private readonly StaticOptionsMonitor<DistributionOptions> _distribution = new(new()
    { Nodes = new() { ["edge"] = new() { Enabled = true, ProtocolVersion = 2, Endpoint = "https://edge" } } });
    private readonly Transport _transport = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonElement Data = JsonSerializer.SerializeToElement(new { job = "SECRET_PAYLOAD" });
    private static string Id() => Guid.NewGuid().ToString("D");
    private static NodePeerOptions Peer() => new()
    { ActiveKeyId = "key", Keys = new() { ["key"] = new string('k', 32) }, AllowedOperations = [PluginNodeMessageService.Operation] };
    public PluginNodeMessageTests()
    {
        _receiver = new(new() { Enabled = true, ClusterId = "cluster", NodeId = "edge", StateDirectory = Path.Combine(_directory, "receiver"), Peers = new() { ["control"] = Peer() } });
        _sender = new(new() { Enabled = true, ClusterId = "cluster", NodeId = "control", StateDirectory = Path.Combine(_directory, "sender"), Peers = new() { ["edge"] = Peer() } });
    }
    private PluginNodeMessageService Service(bool send = false) => new(_manager, _policy, send ? _sender : _receiver,
        _distribution, _transport, new NodeMessageAuthentication(send ? _sender : _receiver), new FixedClock());
    private NodeMessage Message(string? id = null, string plugin = "sample", int schema = 1, string group = "workers") => new(2,
        "cluster", "control", "edge", id ?? Id(), plugin, PluginNodeMessageService.Operation,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), JsonSerializer.SerializeToElement(new PluginNodeMessagePayload(plugin, "job", schema, group, Data), Json));
    private static HttpContext Signed(NodeMessage message, string secret = "kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk")
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://edge" + NodeMessageAuthentication.Prefix + PluginNodeMessageService.Operation);
        NodeMessageAuthentication.Sign(request, message, "key", secret, bytes);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Path = NodeMessageAuthentication.Prefix + PluginNodeMessageService.Operation;
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        foreach (var pair in request.Headers) context.Request.Headers[pair.Key] = pair.Value.ToArray();
        return context;
    }

    [Fact]
    public async Task SignedReceiveAuthenticatesBeforeDispatchAndReturnsCachedReceiptAcrossRestart()
    {
        var message = Message();
        var rejected = await Service().ReceiveAsync(Signed(message, new string('x', 32)));
        Assert.Equal(401, ((IStatusCodeHttpResult)rejected).StatusCode);
        Assert.Equal(0, _manager.Plugin.Calls);
        var result = await Service().ReceiveAsync(Signed(message));
        Assert.True(((PluginNodeMessageReply)((IValueHttpResult)result).Value!).Ack);
        var repeated = await Service().ReceiveAsync(Signed(message));
        Assert.True(((PluginNodeMessageReply)((IValueHttpResult)repeated).Value!).Ack);
        Assert.Equal(1, _manager.Plugin.Calls);
        var files = Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories);
        Assert.DoesNotContain(files.Select(File.ReadAllText), text => text.Contains("SECRET_PAYLOAD"));
        Assert.DoesNotContain(files.Select(File.ReadAllText), text => text.Contains(new string('k', 32)));
    }

    [Theory]
    [InlineData("missing", 1, "workers", "node_plugin_missing", 404)]
    [InlineData("sample", 2, "workers", "node_plugin_schema_unsupported", 422)]
    [InlineData("sample", 1, "https://evil", "node_plugin_invalid_payload", 400)]
    [InlineData("sample", 1, "other", "node_plugin_forbidden", 403)]
    public async Task MissingPluginSchemaAndGroupReturnExplicitNackWithoutHandler(string plugin, int schema, string group, string code, int status)
    {
        var result = await Service().HandleAuthenticatedAsync(Message(plugin: plugin, schema: schema, group: group), default);
        Assert.Equal(code, result.Error?.Code);
        Assert.Equal(status, result.Error?.StatusCode);
        Assert.Equal(0, _manager.Plugin.Calls);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("declaration")]
    [InlineData("sender")]
    [InlineData("membership")]
    [InlineData("stopped")]
    public async Task HostAndPluginMustBothAuthorizeReceive(string denied)
    {
        if (denied == "permission") _manager.Granted = false;
        if (denied == "declaration") _manager.Plugin.Declare = false;
        if (denied == "sender") _policy.CurrentValue.Grants["sample"].AllowedSenders = ["other"];
        if (denied == "membership") _policy.CurrentValue.Groups["workers"] = ["other"];
        if (denied == "stopped") _manager.Running = false;
        var result = await Service().HandleAuthenticatedAsync(Message(), default);
        Assert.NotNull(result.Error);
        Assert.Equal(0, _manager.Plugin.Calls);
    }

    [Theory]
    [InlineData("Draining")]
    [InlineData("Revoked")]
    [InlineData("PendingApproval")]
    public async Task AdmissionBlocksSendAndReceive(string state)
    {
        _sender.CurrentValue.Peers["edge"].AdmissionState = state;
        _receiver.CurrentValue.Peers["control"].AdmissionState = state;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, Id(), default));
        Assert.Equal(403, (await Service().HandleAuthenticatedAsync(Message(), default)).Error?.StatusCode);
        Assert.Equal(0, _transport.Calls);
        Assert.Equal(0, _manager.Plugin.Calls);
    }

    [Fact]
    public async Task SenderUsesHostEndpointSigningAndStableIdWithoutResendingCompletedOrUnknown()
    {
        var id = Id();
        var client = Service(true).Bind("sample");
        Assert.True(Assert.Single(await client.SendAsync("job", 1, "workers", Data, id, default)).Ack);
        Assert.True(Assert.Single(await Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, id, default)).Ack);
        Assert.Equal(1, _transport.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("job", 1, "workers", JsonSerializer.SerializeToElement(new { changed = true }), id, default));
        _transport.Respond = (_, _) => throw new HttpRequestException("response lost");
        var lost = Id();
        Assert.False(Assert.Single(await client.SendAsync("job", 1, "workers", Data, lost, default)).Ack);
        Assert.Equal("node_plugin_outcome_unknown", Assert.Single(await Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, lost, default)).ErrorCode);
        Assert.Equal(2, _transport.Calls);
        Assert.All(_transport.Urls, url => Assert.Equal("https://edge/api/v2/node-agent/plugin.message", url));
    }

    [Fact]
    public async Task UnknownReceiveAndChangedContentNeverExecuteAgain()
    {
        _manager.Plugin.Handle = (_, _) => throw new IOException("SECRET_ERROR");
        var message = Message();
        Assert.NotNull((await Service().HandleAuthenticatedAsync(message, default)).Error);
        Assert.Equal("node_plugin_outcome_unknown", (await Service().HandleAuthenticatedAsync(message, default)).Error?.Code);
        var changed = message with { Payload = JsonSerializer.SerializeToElement(new PluginNodeMessagePayload("sample", "job", 1, "workers",
            JsonSerializer.SerializeToElement(new { different = true })), Json) };
        Assert.Equal("node_plugin_message_conflict", (await Service().HandleAuthenticatedAsync(changed, default)).Error?.Code);
        Assert.Equal(1, _manager.Plugin.Calls);
        Assert.DoesNotContain(Directory.GetFiles(_directory, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText), text => text.Contains("SECRET_ERROR"));
    }

    [Fact]
    public async Task RateAndPayloadLimitsApplyBeforeHandler()
    {
        var service = Service();
        var message = Message();
        for (var i = 0; i < 60; i++) Assert.NotNull((await service.HandleAuthenticatedAsync(message, default)).Reply);
        Assert.Equal(429, (await service.HandleAuthenticatedAsync(message, default)).Error?.StatusCode);
        var oversized = Message() with { Payload = JsonSerializer.SerializeToElement(new PluginNodeMessagePayload("sample", "job", 1, "workers",
            JsonSerializer.SerializeToElement(new { data = new string('x', PluginNodeMessageService.MaximumPayloadBytes) })), Json) };
        Assert.Equal(400, (await Service().HandleAuthenticatedAsync(oversized, default)).Error?.StatusCode);
        Assert.Equal(1, _manager.Plugin.Calls);
    }

    [Fact]
    public async Task AuditQuotaPreventsEffectsAndWriteFailureCanRecover()
    {
        var incoming = Path.Combine(_receiver.CurrentValue.StateDirectory, "plugin-messages", "incoming");
        Directory.CreateDirectory(incoming);
        var filler = Path.Combine(incoming, "capacity.bin");
        using (var file = File.Create(filler)) file.SetLength(64 * 1024 * 1024);
        var message = Message();
        Assert.Equal("node_plugin_store_unavailable", (await Service().HandleAuthenticatedAsync(message, default)).Error?.Code);
        Assert.Equal(0, _manager.Plugin.Calls);
        File.Delete(filler);
        Assert.True((await Service().HandleAuthenticatedAsync(message, default)).Reply!.Ack);
    }

    [Fact]
    public async Task CancellationRetainsUnknownAndUncooperativeHandlersKeepTheirSlots()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.Plugin.Handle = async (_, _) =>
        {
            if (Volatile.Read(ref _manager.Plugin.Calls) >= 16) entered.TrySetResult();
            await release.Task;
            if (Interlocked.Increment(ref _manager.Plugin.Finished) == 16) finished.TrySetResult();
            return new(true);
        };
        var service = Service();
        using var stop = new CancellationTokenSource();
        var messages = Enumerable.Range(0, 16).Select(_ => Message()).ToArray();
        var tasks = messages.Select(message => service.HandleAuthenticatedAsync(message, stop.Token)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            foreach (var task in tasks) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(429, (await service.HandleAuthenticatedAsync(Message(), default)).Error?.StatusCode);
            Assert.Equal(16, _manager.Plugin.Calls);
        }
        finally { release.TrySetResult(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal("node_plugin_outcome_unknown", (await Service().HandleAuthenticatedAsync(messages[0], default)).Error?.Code);
    }

    [Fact]
    public async Task BoundPluginCannotSpoofAnotherPluginOrReadNodeCredentialsThroughNewContract()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPluginTaskCoordinator, UnusedTasks>();
        services.AddHttpClient();
        services.AddSingleton(Service(true));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["NodeCommunication:Peers:edge:Keys:key"] = new string('k', 32), ["Plugins:other:Secret"] = "OTHER_SECRET" }).Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IPluginManager>(_manager);
        services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(_sender);
        services.AddSingleton<IOptions<NodeCommunicationOptions>>(Options.Create(_sender.CurrentValue));
        services.AddScoped<IOptionsSnapshot<NodeCommunicationOptions>>(_ => new OptionsManager<NodeCommunicationOptions>(
            new OptionsFactory<NodeCommunicationOptions>([], [])));
        using var provider = services.BuildServiceProvider();
        var context = new PluginContext("sample", provider, configuration, _manager, new PluginEventBus(), Path.Combine(_directory, "data"));
        Assert.Null(context.Configuration["NodeCommunication:Peers:edge:Keys:key"]);
        Assert.Null(context.Configuration["Plugins:other:Secret"]);
        Assert.NotNull(provider.GetService<PluginNodeMessageService>());
        Assert.NotNull(provider.GetService<IOptionsMonitor<NodeCommunicationOptions>>());
        Assert.Same(context.Configuration, context.Services.GetRequiredService<IConfiguration>());
        Assert.Null(context.Services.GetRequiredService<IConfiguration>()["NodeCommunication:Peers:edge:Keys:key"]);
        Assert.Null(context.Services.GetRequiredService<IConfiguration>()["Plugins:other:Secret"]);
        Assert.Same(context.Services, context.Services.GetRequiredService<IServiceProvider>());
        foreach (var forbidden in new[] { typeof(PluginNodeMessageService), typeof(IPluginManager),
            typeof(IConfigurationRoot), typeof(IServiceScopeFactory), typeof(IHttpClientFactory),
            typeof(IOptionsMonitor<NodeCommunicationOptions>), typeof(IOptions<NodeCommunicationOptions>),
            typeof(IOptionsSnapshot<NodeCommunicationOptions>), typeof(IEnumerable<IPluginManager>),
            typeof(IEnumerable<IOptionsMonitor<NodeCommunicationOptions>>) })
        {
            Assert.Null(context.Services.GetService(forbidden));
            Assert.Throws<InvalidOperationException>(() => context.Services.GetRequiredService(forbidden));
        }
        Assert.Same(context.NodeMessages, context.Services.GetRequiredService<IPluginNodeMessageClient>());
        Assert.Same(context.Tasks, context.Services.GetRequiredService<IPluginTaskCoordinator>());
        Assert.Same(context.Network, context.Services.GetRequiredService<IPluginNetworkClientFactory>());
        Assert.Same(context.Storage, context.Services.GetRequiredService<IPluginStorage>());
        Assert.Same(context.Logger, context.Services.GetRequiredService<NLog.Logger>());
        Assert.Empty(typeof(IPluginNodeMessageClient).GetProperties());
        Assert.True(Assert.Single(await context.NodeMessages.SendAsync("job", 1, "workers", Data, Id(), default)).Ack);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(true).Bind("other").SendAsync("job", 1, "workers", Data, Id(), default));
        Assert.Equal(1, _transport.Calls);
    }

    [Fact]
    public async Task WrongReplyAndUnauthorizedDestinationNeverBecomeAck()
    {
        _transport.Respond = (message, _) => new(HttpStatusCode.OK) { Content = JsonContent.Create(new PluginNodeMessageReply("other", "job", 1,
            message.MessageId, "edge", true)) };
        Assert.False(Assert.Single(await Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, Id(), default)).Ack);
        _distribution.CurrentValue.Nodes["edge"].Endpoint = "https://edge/path";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, Id(), default));
        Assert.Equal(1, _transport.Calls);
    }

    [Fact]
    public async Task RealSignedTransportDispatchesOnlyTheDeclaredInstalledPlugin()
    {
        var receiver = Service();
        _transport.Forward = async (request, token) =>
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Scheme = "https";
            context.Request.Path = request.RequestUri!.AbsolutePath;
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
            foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
            var result = await receiver.ReceiveAsync(context);
            return new((HttpStatusCode)(((IStatusCodeHttpResult)result).StatusCode ?? 200))
            { Content = JsonContent.Create(((IValueHttpResult)result).Value, options: Json) };
        };
        var id = Id();
        Assert.True(Assert.Single(await Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, id, default)).Ack);
        Assert.True(Assert.Single(await Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, id, default)).Ack);
        Assert.Equal(1, _manager.Plugin.Calls);
        Assert.Equal(1, _transport.Calls);
    }

    [Theory]
    [InlineData("sample", 1, 200, null)]
    [InlineData("missing", 1, 404, "node_plugin_missing")]
    [InlineData("sample", 2, 422, "node_plugin_schema_unsupported")]
    public async Task ProductionRouterAndRegistrationHandleRealSignedHttp(string plugin, int schema, int status, string? errorCode)
    {
        Assert.True(NodeProtocolOperations.IsSupported(PluginNodeMessageService.Operation));
        Assert.Single(NodeProtocolOperations.All, name => name == PluginNodeMessageService.Operation);
        // Only this loopback test opts into plaintext; production defaults continue to require HTTPS.
        _receiver.CurrentValue.RequireHttps = false;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddPluginNodeMessaging(new ConfigurationBuilder().Build());
        builder.Services.AddSingleton<IPluginManager>(_manager);
        builder.Services.AddSingleton<IOptionsMonitor<PluginNodeMessageOptions>>(_policy);
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(_receiver);
        builder.Services.AddSingleton<IOptionsMonitor<DistributionOptions>>(_distribution);
        await using var app = builder.Build();
        app.MapNodeCommunication(12345);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)), Timeout = TimeSpan.FromSeconds(10) };
            var message = Message(plugin: plugin, schema: schema);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            using var request = new HttpRequestMessage(HttpMethod.Post, NodeMessageAuthentication.Prefix + PluginNodeMessageService.Operation)
            { Content = new ByteArrayContent(bytes) };
            // The application binds an ephemeral socket; the production route's explicit host constraint remains enforced.
            request.Headers.Host = "edge:12345";
            request.Content.Headers.ContentType = new("application/json");
            NodeMessageAuthentication.Sign(request, message, "key", new string('k', 32), bytes);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True((int)response.StatusCode == status, $"HTTP {(int)response.StatusCode}: {body}");
            if (errorCode is null)
            {
                var reply = JsonSerializer.Deserialize<PluginNodeMessageReply>(body, Json)!;
                Assert.True(reply.Ack);
                Assert.Equal(message.MessageId, reply.MessageId);
                Assert.Equal("edge", reply.NodeId);
                Assert.Equal(1, _manager.Plugin.Calls);
            }
            else
            {
                var error = JsonSerializer.Deserialize<NodeErrorResponse>(body, Json)!;
                Assert.False(error.Success);
                Assert.Equal(errorCode, error.ErrorCode);
                Assert.Equal("node.errors." + errorCode, error.MessageKey);
                Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
                Assert.Equal(0, _manager.Plugin.Calls);
            }
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task GroupMembershipIsPinnedAndRevokedMemberRejectsWholeNewFanout()
    {
        var id = Id();
        await Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, id, default);
        _policy.CurrentValue.Groups["workers"] = ["edge", "second"];
        _distribution.CurrentValue.Nodes["second"] = new() { Endpoint = "https://second", ProtocolVersion = 2 };
        _sender.CurrentValue.Peers["second"] = Peer();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, id, default));
        _sender.CurrentValue.Peers["second"].AdmissionState = "Revoked";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, Id(), default));
        Assert.Equal(1, _transport.Calls);
    }

    [Fact]
    public async Task CorruptReceiptCannotForgeAckAndOutgoingAuditFailurePreventsNetwork()
    {
        var message = Message();
        Assert.True((await Service().HandleAuthenticatedAsync(message, default)).Reply!.Ack);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_receiver.CurrentValue.StateDirectory, "plugin-messages", "incoming"), "*.json"));
        File.WriteAllText(path, "{");
        Assert.Equal("node_plugin_store_unavailable", (await Service().HandleAuthenticatedAsync(message, default)).Error?.Code);
        Assert.Equal(1, _manager.Plugin.Calls);
        var directory = Path.Combine(_sender.CurrentValue.StateDirectory, "plugin-messages", "outgoing");
        Directory.CreateDirectory(directory);
        using (var filler = File.Create(Path.Combine(directory, "capacity.bin"))) filler.SetLength(64 * 1024 * 1024);
        Assert.Equal("node_plugin_store_unavailable", Assert.Single(await Service(true).Bind("sample").SendAsync("job", 1, "workers", Data, Id(), default)).ErrorCode);
        Assert.Equal(0, _transport.Calls);
    }

    private sealed class Plugin : LyWafPluginBase, IPluginNodeMessageHandler
    {
        public bool Declare = true;
        public int Calls;
        public int Finished;
        public Func<PluginNodeReceivedMessage, CancellationToken, Task<PluginNodeHandlerResult>> Handle = (_, _) => Task.FromResult(new PluginNodeHandlerResult(true));
        public override PluginMetadata Metadata => new()
        { Id = "sample", Name = "Sample", RequestedPermissions = Declare ? PluginPermission.NodeMessaging : PluginPermission.None,
            NodeMessages = [new("job", 1, ["workers"], true, true)] };
        public Task<PluginNodeHandlerResult> HandleNodeMessageAsync(PluginNodeReceivedMessage message, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Handle(message, token); }
    }
    private sealed class UnusedTasks : IPluginTaskCoordinator
    {
        public bool TryStart(string pluginId, string idempotencyKey, TimeSpan ttl, out PluginTaskState state) => throw new NotSupportedException();
        public PluginTaskState Complete(string pluginId, string idempotencyKey, bool success, string? error = null) => throw new NotSupportedException();
        public bool TryGet(string pluginId, string idempotencyKey, out PluginTaskState? state) => throw new NotSupportedException();
    }
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
    }
    private sealed class Manager : IPluginManager
    {
        public Plugin Plugin = new();
        public bool Granted = true;
        public bool Running = true;
        public ILyWafPlugin? GetPlugin(string pluginId) => pluginId == "sample" ? Plugin : null;
        public IReadOnlyList<ILyWafPlugin> GetAllPlugins() => [Plugin];
        public IReadOnlyList<ILyWafPlugin> GetSystemPlugins() => [];
        public bool IsSystemPlugin(string pluginId) => false;
        public Task EnablePluginAsync(string pluginId) => Task.CompletedTask;
        public Task DisablePluginAsync(string pluginId) => Task.CompletedTask;
        public Task ReloadPluginAsync(string pluginId) => Task.CompletedTask;
        public PluginState GetPluginState(string pluginId) => Running ? PluginState.Running : PluginState.Stopped;
        public bool IsPermissionGranted(string pluginId, PluginPermission permission) => Granted && pluginId == "sample";
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public List<string> Urls = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Forward;
        public Func<NodeMessage, CancellationToken, HttpResponseMessage> Respond = (message, _) => new(HttpStatusCode.OK)
        { Content = JsonContent.Create(new PluginNodeMessageReply("sample", "job", 1, message.MessageId, message.ReceiverNodeId, true)) };
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Assert.True(request.Headers.Contains("X-LyWaf-Signature"));
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            if (Forward is not null) return await Forward(request, token);
            var message = (await request.Content!.ReadFromJsonAsync<NodeMessage>(Json, token))!;
            return Respond(message, token);
        }
    }
    public void Dispose() { _transport.Dispose(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
