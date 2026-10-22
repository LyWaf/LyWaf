using System.Net;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class PluginNodeRequestTests : IDisposable
{
    private const string Secret = "plugin-request-secret-at-least-32-bytes";
    private readonly string root = Path.Combine(Path.GetTempPath(), "lywaf-plugin-rpc-" + Guid.NewGuid().ToString("N"));
    private readonly Manager manager = new();
    private readonly Handler handler = new();
    private readonly ServiceProvider services;
    private readonly NodeCommunicationOptions receiver;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PluginNodeRequestTests()
    {
        receiver = new() { Enabled = true, ClusterId = "cluster", NodeId = "edge", RequireHttps = false,
            StateDirectory = root, Peers = new() { ["sender"] = Peer() } };
        services = new ServiceCollection().AddSingleton<IPluginManager>(manager)
            .AddSingleton<IPluginNodeRequestHandler>(handler)
            .AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<NodeCommunicationOptions>>(new StaticOptionsMonitor<NodeCommunicationOptions>(receiver))
            .AddSingleton<NodeMessageAuthentication>().BuildServiceProvider();
    }

    private static NodePeerOptions Peer() => new() { ActiveKeyId = "key", Keys = new() { ["key"] = Secret },
        AllowedOperations = [PluginNodeRequests.Operation, "sample.echo"] };

    private static NodeMessage Message() => PluginNodeRequests.Wrap(new(2, "cluster", "sender", "edge",
        Guid.NewGuid().ToString("D"), "correlation", "sample.echo", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30),
        JsonSerializer.SerializeToElement(new { value = "hello", senderNodeId = "forged" })), "sample");

    private async Task<IResult> Dispatch(NodeMessage message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://edge" + NodeMessageAuthentication.Prefix + message.Type);
        NodeMessageAuthentication.Sign(request, message, "key", Secret, bytes);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Path = NodeMessageAuthentication.Prefix + message.Type;
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
        return await NodeCommunicationEndpoints.DispatchAsync(context, services.GetRequiredService<NodeMessageAuthentication>(), message.Type);
    }

    private static int Status(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;

    [Fact]
    public async Task ArbitraryPluginUsesTheSharedRouterWithAuthenticatedOriginAndReply()
    {
        var message = Message();
        var result = await Dispatch(message);
        Assert.Equal(200, Status(result));
        var response = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value, Json);
        Assert.Equal("sender", response.GetProperty("sender").GetString());
        Assert.Equal(message.MessageId, response.GetProperty("id").GetString());
        Assert.Equal("hello", response.GetProperty("value").GetString());
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("sample.echo", NodeProtocolOperations.All);
        Assert.DoesNotContain("cc.execution.query", NodeProtocolOperations.All);
        Assert.DoesNotContain("cc.execution.relay", NodeProtocolOperations.All);
    }

    [Theory]
    [InlineData("disabled", 404)]
    [InlineData("missing", 404)]
    [InlineData("permission", 403)]
    [InlineData("declaration", 403)]
    [InlineData("schema", 422)]
    [InlineData("identity", 422)]
    public async Task DispatchFailsClosedBeforeCallingAPlugin(string fault, int status)
    {
        manager.Fault = fault;
        var message = Message();
        if (fault is "schema" or "identity")
        {
            var payload = message.Payload.Deserialize<PluginNodeRequestPayload>(Json)!;
            message = message with { Payload = JsonSerializer.SerializeToElement(fault == "schema"
                ? payload with { SchemaVersion = 2 } : payload with { PluginId = "other" }, Json) };
        }
        Assert.Equal(status, Status(await Dispatch(message)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CompatibilityAliasUsesTheSameLifecycleAndPermissionChecks()
    {
        var wrapped = Message();
        var old = wrapped with { Type = "sample.echo", Payload = wrapped.Payload.Deserialize<PluginNodeRequestPayload>(Json)!.Data };
        Assert.Equal(200, Status(await Dispatch(old)));
        manager.Fault = "disabled";
        Assert.Equal(404, Status(await Dispatch(old with { MessageId = Guid.NewGuid().ToString("D") })));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NodeGrantAndDurableReplayGuardStillRunBeforePluginDispatch()
    {
        var message = Message();
        receiver.Peers["sender"].AllowedOperations.Clear();
        Assert.Equal(403, Status(await Dispatch(message)));
        Assert.Equal(0, handler.Calls);
        receiver.Peers["sender"] = Peer();
        Assert.Equal(200, Status(await Dispatch(message)));
        // A fresh transport signature may retry a business ID; idempotence belongs to the registered plugin handler.
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BoundSdkClientReturnsDataWithoutExposingKeysAndDoesNotRetry()
    {
        var sender = new NodeCommunicationOptions { Enabled = true, ClusterId = "cluster", NodeId = "sender",
            Peers = new() { ["edge"] = Peer() } };
        var transport = new Transport(async request =>
        {
            var message = JsonSerializer.Deserialize<NodeMessage>(await request.Content!.ReadAsStringAsync(), Json)!;
            Assert.Equal(PluginNodeRequests.Operation, message.Type);
            Assert.Equal("sample", message.Payload.GetProperty("pluginId").GetString());
            var result = await Dispatch(message);
            return new HttpResponseMessage((HttpStatusCode)Status(result))
            { Content = new StringContent(JsonSerializer.Serialize(((IValueHttpResult)result).Value, Json)) };
        });
        var client = new PluginNodeRequestClient(manager, new StaticOptionsMonitor<NodeCommunicationOptions>(sender),
            new StaticOptionsMonitor<DistributionOptions>(new() { Nodes = new() { ["edge"] = new() { Enabled = true,
                ProtocolVersion = 2, Endpoint = "https://edge/" } } }), transport).Bind("sample");
        var result = await client.RequestAsync("edge", "sample.echo", 1, JsonSerializer.SerializeToElement(new { value = "sdk" }),
            Guid.NewGuid().ToString("D"), default);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("sdk", result.Payload.GetProperty("value").GetString());
        manager.Fault = "disabled";
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync("edge", "sample.echo", 1,
            JsonSerializer.SerializeToElement(new { value = "sdk" }), Guid.NewGuid().ToString("D"), default));
        Assert.Equal(1, transport.Calls);
    }

    private sealed class Handler : IPluginNodeRequestHandler
    {
        public int Calls;
        public string PluginId => "sample";
        public IReadOnlyList<PluginNodeRequestCapability> Requests => [new("sample.echo", 1, "sample.echo")];
        public Task<IResult> HandleNodeRequestAsync(PluginNodeRequest message, HttpContext context)
        {
            Calls++;
            return Task.FromResult(Results.Json(new { id = message.MessageId, sender = message.SenderNodeId,
                value = message.Payload.GetProperty("value").GetString() }));
        }
    }
    private sealed class Plugin(bool declared) : LyWafPluginBase
    {
        public override PluginMetadata Metadata => new() { Id = "sample", Name = "Sample", RequestedPermissions = PluginPermission.NodeMessaging,
            NodeRequests = declared ? [new("sample.echo", 1, "sample.echo")] : [] };
    }
    private sealed class Manager : IPluginManager
    {
        public string Fault = "";
        public ILyWafPlugin? GetPlugin(string id) => id == "sample" && Fault != "missing" ? new Plugin(Fault != "declaration") : null;
        public IReadOnlyList<ILyWafPlugin> GetAllPlugins() => [];
        public IReadOnlyList<ILyWafPlugin> GetSystemPlugins() => [];
        public bool IsSystemPlugin(string id) => false;
        public Task EnablePluginAsync(string id) => Task.CompletedTask;
        public Task DisablePluginAsync(string id) => Task.CompletedTask;
        public Task ReloadPluginAsync(string id) => Task.CompletedTask;
        public PluginState GetPluginState(string id) => Fault == "disabled" ? PluginState.Stopped : PluginState.Running;
        public bool IsPermissionGranted(string id, PluginPermission permission) => Fault != "permission";
    }
    private sealed class Transport(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return send(request); }
    }
    public void Dispose() { services.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
