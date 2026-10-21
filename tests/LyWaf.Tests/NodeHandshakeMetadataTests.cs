using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.Distribution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeHandshakeMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "handshake-http-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Secret = "handshake-test-transport-secret-32bytes";

    [Fact]
    public async Task MetadataComesFromIdentityRevisionAndGrantedRunningPluginContracts()
    {
        var config = Options("edge", "Node");
        using var identity = new NodeIdentityService(new StaticOptionsMonitor<NodeCommunicationOptions>(config));
        await identity.StartAsync(default);
        var manager = Manager();
        var revision = new Revision();
        var policy = new StaticOptionsMonitor<PluginNodeMessageOptions>(Policy());
        var metadata = new NodeHandshakeMetadataService(identity, revision, manager, policy);
        var first = await metadata.CaptureAsync(default);
        Assert.Equal(identity.Capture().BootId, first.BootId);
        Assert.Equal(revision.Value, first.Configuration);
        var plugin = Assert.Single(first.Plugins!.Items);
        var capability = Assert.Single(plugin.Messages);
        Assert.True(capability.CanSend);
        Assert.False(capability.CanReceive);
        Assert.Equal(new[] { "workers" }, capability.TargetGroups);
        Assert.Equal(NodeHandshakeMetadataService.ComputeDigest(first.Plugins.Items), first.Plugins.Digest);
        Assert.Equal(first.Plugins.Digest, (await metadata.CaptureAsync(default)).Plugins!.Digest);
        ((ManagerProxy)(object)manager).Granted = false;
        var denied = await metadata.CaptureAsync(default);
        Assert.Empty(Assert.Single(denied.Plugins!.Items).Messages);
        Assert.NotEqual(first.Plugins.Digest, denied.Plugins.Digest);
        ((ManagerProxy)(object)manager).Granted = true;
        ((ManagerProxy)(object)manager).Running = false;
        Assert.Empty(Assert.Single((await metadata.CaptureAsync(default)).Plugins!.Items).Messages);
        revision.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => metadata.CaptureAsync(default));
    }

    [Fact]
    public async Task RealHttpSignedHandshakeRegistersMetadataButBadSignatureAndCloneCannotOpenGate()
    {
        var config = Options("controller", "Controller");
        config.RequireHttps = false; // Loopback fixture only; production keeps certificate validation.
        config.Peers["edge"] = new() { Role = "Node", ActiveKeyId = "key", Keys = new() { ["key"] = Secret }, AllowedOperations = ["node.handshake"] };
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = _root });
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new StaticOptionsMonitor<NodeCommunicationOptions>(config));
        builder.Services.AddSingleton<IOptionsMonitor<PluginNodeMessageOptions>>(new StaticOptionsMonitor<PluginNodeMessageOptions>(Policy()));
        builder.Services.AddSingleton(Manager());
        builder.Services.AddSingleton<INodeHandshakeConfigurationSource>(new Revision());
        builder.Services.AddSingleton<NodeIdentityService>();
        builder.Services.AddHostedService(s => s.GetRequiredService<NodeIdentityService>());
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddNodeHandshakeServices();
        await using var app = builder.Build();
        app.MapNodeCommunication(port);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            using var remoteIdentity = new NodeIdentityService(new StaticOptionsMonitor<NodeCommunicationOptions>(Options("edge", "Node")));
            await remoteIdentity.StartAsync(default);
            var remote = await new NodeHandshakeMetadataService(remoteIdentity, new Revision(), Manager(),
                new StaticOptionsMonitor<PluginNodeMessageOptions>(Policy())).CaptureAsync(default);
            var observer = app.Services.GetRequiredService<NodeRegistrationObserver>();
            async Task<HttpResponseMessage> Send(NodeHandshake body, string secret = Secret)
            {
                var now = DateTimeOffset.UtcNow;
                var message = new NodeMessage(2, "cluster", "edge", "controller", Guid.NewGuid().ToString("D"), "handshake",
                    "node.handshake", now, now.AddMinutes(1), JsonSerializer.SerializeToElement(body, Json));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
                using var request = new HttpRequestMessage(HttpMethod.Post, NodeMessageAuthentication.Prefix + message.Type)
                    { Content = new ByteArrayContent(bytes) };
                NodeMessageAuthentication.Sign(request, message, "key", secret, bytes);
                return await http.SendAsync(request);
            }
            using var bad = await Send(remote, new string('x', 32));
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
            Assert.Null(observer.Get("edge"));
            using var accepted = await Send(remote);
            accepted.EnsureSuccessStatusCode();
            var exchange = (await accepted.Content.ReadFromJsonAsync<NodeHandshakeExchange>(Json))!;
            Assert.Equal("controller", exchange.Local.NodeId);
            Assert.NotNull(exchange.Local.Configuration);
            Assert.NotNull(exchange.Local.Plugins);
            Assert.Equal("NeedsReconciliation", exchange.Registration!.State);
            Assert.False(observer.CanStartTasks("edge", exchange.Registration.ObservationId));
            config.Peers["edge"].AllowedOperations.Clear();
            using var unauthorized = await Send(remote);
            Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
            config.Peers["edge"].AllowedOperations.Add("node.handshake");
            var caller = Options("edge", "Node");
            caller.RequireHttps = false;
            caller.Peers["controller"] = new() { Role = "Controller", ActiveKeyId = "key",
                Keys = new() { ["key"] = Secret }, AllowedOperations = ["node.handshake"] };
            using var handshakeClient = new NodeHandshakeClient(new StaticOptionsMonitor<NodeCommunicationOptions>(caller),
                new StaticOptionsMonitor<DistributionOptions>(new() { Nodes = new() { ["controller"] = new()
                    { Endpoint = http.BaseAddress!.ToString(), ProtocolVersion = 2, Enabled = true } } }),
                new NodeHandshakeMetadataService(remoteIdentity, new Revision(), Manager(), new StaticOptionsMonitor<PluginNodeMessageOptions>(Policy())));
            var probed = await handshakeClient.CaptureAsync("controller", default);
            Assert.Equal(exchange.Local.BootId, probed.BootId);
            Assert.Equal(exchange.Local.Plugins!.Digest, probed.Plugins!.Digest);
            using var mismatch = await Send(remote with { NodeId = "other" });
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            using var clone = await Send(remote with { BootId = Guid.NewGuid().ToString("N") });
            var conflict = (await clone.Content.ReadFromJsonAsync<NodeHandshakeExchange>(Json))!;
            Assert.Equal("IdentityConflict", conflict.Registration!.State);
            Assert.False(observer.CanStartTasks("edge", exchange.Registration.ObservationId));
        }
        finally { await app.StopAsync(); }
    }

    private NodeCommunicationOptions Options(string node, string role)
    {
        Directory.CreateDirectory(_root);
        return new() { Enabled = true, ClusterId = "cluster", NodeId = node, Role = role, StateDirectory = Path.Combine(_root, node) };
    }
    private static PluginNodeMessageOptions Policy() => new()
    {
        Enabled = true, Groups = new() { ["workers"] = ["edge"] },
        Grants = new() { ["sample"] = new() { SendGroups = ["workers"], ReceiveGroups = [], AllowedSenders = [] } }
    };
    private static IPluginManager Manager() => DispatchProxy.Create<IPluginManager, ManagerProxy>();
    public class ManagerProxy : DispatchProxy
    {
        public bool Granted = true, Running = true;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IPluginManager.GetAllPlugins) => new ILyWafPlugin[] { new Plugin() },
            nameof(IPluginManager.GetPluginState) => Running ? PluginState.Running : PluginState.Stopped,
            nameof(IPluginManager.IsPermissionGranted) => Granted,
            _ => throw new NotSupportedException()
        };
    }
    private sealed class Plugin : LyWafPluginBase
    {
        public override PluginMetadata Metadata => new() { Id = "sample", Name = "Sample", Version = "2.3.4",
            NodeMessages = [new("event", 2, ["workers", "ungranted"], true, true)] };
    }
    private sealed class Revision : INodeHandshakeConfigurationSource
    {
        public NodeConfigurationRevision Value = new("applied-revision", new string('A', 64), "file-revision");
        public bool Fail;
        public Task<NodeConfigurationRevision> CaptureAsync(CancellationToken token) => Fail
            ? throw new IOException("Unavailable") : Task.FromResult(Value);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
