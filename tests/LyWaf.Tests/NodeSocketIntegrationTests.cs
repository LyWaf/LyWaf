using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.Distribution;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSocketIntegrationTests
{
    private const string Secret = "socket-integration-key-at-least-32bytes";

    [Fact]
    public async Task EquivalentOptionsReloadDoesNotDropTheExistingControlSocket()
    {
        await using var hosts = await Hosts.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var monitor = new CloningMonitor(hosts.Node.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>());
        var connection = hosts.Connector(monitor).ConnectOnceAsync(deadline.Token);
        try
        {
            await WaitFor(() => hosts.Controller.Services.GetRequiredService<NodeSocketConnections>().IsConnected("edge"), deadline.Token);
            await Task.Delay(TimeSpan.FromSeconds(17), deadline.Token);
            Assert.False(connection.IsCompleted);
            using var request = await hosts.SignedAsync(hosts.Controller, "controller", "edge");
            using var response = await hosts.Controller.Services.GetRequiredService<NodeSocketConnections>()
                .SendAsync("edge", request, deadline.Token);
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            deadline.Cancel();
            try { await connection; } catch (OperationCanceledException) { }
        }
    }

    private sealed class CloningMonitor(IOptionsMonitor<NodeCommunicationOptions> source) : IOptionsMonitor<NodeCommunicationOptions>
    {
        public NodeCommunicationOptions CurrentValue => JsonSerializer.Deserialize<NodeCommunicationOptions>(JsonSerializer.Serialize(source.CurrentValue))!;
        public NodeCommunicationOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<NodeCommunicationOptions, string?> listener) => null;
    }

    [Fact]
    public async Task NodeConnectsOutboundToControlPortAndControllerCallsBackOverSameSocket()
    {
        await using var hosts = await Hosts.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var connector = hosts.Connector();
        var connection = connector.ConnectOnceAsync(deadline.Token);
        try
        {
            await WaitFor(() => hosts.Controller.Services.GetRequiredService<NodeSocketConnections>().IsConnected("edge"), deadline.Token);
            using var request = await hosts.SignedAsync(hosts.Controller, "controller", "edge");
            using var response = await hosts.Controller.Services.GetRequiredService<NodeSocketConnections>()
                .SendAsync("edge", request, deadline.Token);
            response.EnsureSuccessStatusCode();
            var reply = JsonSerializer.Deserialize<NodeHandshakeExchange>(await response.Content.ReadAsByteArrayAsync(),
                NodeMessageAuthentication.JsonOptions)!;
            Assert.Equal("edge", reply.Local.NodeId);
            Assert.Null(reply.Registration);
            using var probe = new NodeHandshakeClient(
                hosts.Controller.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>(),
                new StaticOptionsMonitor<DistributionOptions>(new() { Nodes = new()
                {
                    ["edge"] = new() { Enabled = true, ProtocolVersion = 2, Endpoint = "http://not-routable.invalid/" }
                } }), hosts.Controller.Services.GetRequiredService<NodeHandshakeMetadataService>(),
                hosts.Controller.Services.GetRequiredService<NodeSocketConnections>());
            Assert.Equal("edge", (await probe.CaptureAsync("edge", deadline.Token)).NodeId);
            Assert.Single(hosts.Controller.Urls);
            Assert.Equal(hosts.ControlPort, new Uri(hosts.Controller.Urls.Single()).Port);
        }
        finally { deadline.Cancel(); await Stop(connection); }
        await WaitFor(() => !hosts.Controller.Services.GetRequiredService<NodeSocketConnections>().IsConnected("edge"), CancellationToken.None);
        Assert.False(hosts.Node.Services.GetRequiredService<NodeSocketConnections>().IsConnected("controller"));
    }

    [Fact]
    public async Task DurableStateSyncUsesControlSocketAndResumesPendingBatchAfterReconnect()
    {
        await using var hosts = await Hosts.StartAsync();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var connected = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var connection = hosts.Connector().ConnectOnceAsync(connected.Token);
        var source = hosts.Node.Services.GetRequiredService<RuntimeStateStore>();
        var target = hosts.Controller.Services.GetRequiredService<RuntimeStateStore>();
        var runtime = hosts.Node.Services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>();
        var protocol = hosts.Node.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>();
        var recorder = new HttpRecorder();
        using var http = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(2) };
        var transport = new RuntimeStateSyncTransport(http, runtime, protocol, source,
            sockets: hosts.Node.Services.GetRequiredService<NodeSocketConnections>());
        var sender = new RuntimeStateSyncSender(runtime);
        var window = DateTimeOffset.UtcNow;
        try
        {
            await WaitFor(() => hosts.Node.Services.GetRequiredService<NodeSocketConnections>().IsConnected("controller"), lifetime.Token);
            source.IncrementWindow("socket.counter", "total", window, TimeSpan.FromMinutes(5), 3);
            var mutation = source.ExportPage(null, 10).Items.Single();
            await transport.SendAsync("controller", mutation, lifetime.Token);
            Assert.Equal(3, target.GetWindowValue("socket.counter", "total", window));
            Assert.Null(sender.ReadPending("cluster", "edge", "controller"));
            await transport.SendAsync("controller", mutation, lifetime.Token);
            Assert.Equal(3, target.GetWindowValue("socket.counter", "total", window));
            connected.Cancel();
            await Stop(connection);
            await WaitFor(() => !hosts.Controller.Services.GetRequiredService<NodeSocketConnections>().IsConnected("edge"), lifetime.Token);
            source.IncrementWindow("socket.counter", "total", window, TimeSpan.FromMinutes(5), 7);
            mutation = source.ExportPage(null, 10).Items.Single();
            await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync("controller", mutation, lifetime.Token));
            Assert.NotNull(sender.ReadPending("cluster", "edge", "controller"));
            Assert.Equal(3, target.GetWindowValue("socket.counter", "total", window));
            using var reconnected = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var next = hosts.Connector().ConnectOnceAsync(reconnected.Token);
            try
            {
                await WaitFor(() => hosts.Node.Services.GetRequiredService<NodeSocketConnections>().IsConnected("controller"), lifetime.Token);
                await transport.SendAsync("controller", null, lifetime.Token);
                Assert.Equal(10, target.GetWindowValue("socket.counter", "total", window));
                Assert.Null(sender.ReadPending("cluster", "edge", "controller"));
                Assert.Equal(0, recorder.Calls);
            }
            finally { reconnected.Cancel(); await Stop(next); }
        }
        finally { connected.Cancel(); await Stop(connection); }
    }

    private sealed class HttpRecorder : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new IOException("Unexpected direct HTTP call"); }
    }

    [Fact]
    public async Task UpgradeRequiresSignatureAndCannotReplayPreviouslyAcceptedHandshake()
    {
        await using var hosts = await Hosts.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var url = new UriBuilder(new Uri(hosts.Controller.Urls.Single())) { Scheme = "ws", Path = NodeSocketEndpoints.Path }.Uri;
        using (var anonymous = new ClientWebSocket())
            await Assert.ThrowsAsync<WebSocketException>(() => anonymous.ConnectAsync(url, deadline.Token));
        Assert.False(hosts.Controller.Services.GetRequiredService<NodeSocketConnections>().IsConnected("edge"));
        using var request = await hosts.SignedAsync(hosts.Node, "edge", "controller");
        async Task<ClientWebSocket> Open()
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader(NodeSocketEndpoints.HandshakeHeader,
                Convert.ToBase64String(await request.Content!.ReadAsByteArrayAsync()));
            foreach (var name in NodeSocketProtocol.SignedHeaders)
                socket.Options.SetRequestHeader(name, request.Headers.GetValues(name).Single());
            try { await socket.ConnectAsync(url, deadline.Token); return socket; }
            catch { socket.Dispose(); throw; }
        }
        using (var first = await Open())
        {
            Assert.Equal(WebSocketState.Open, first.State);
            first.Abort();
        }
        await WaitFor(() => !hosts.Controller.Services.GetRequiredService<NodeSocketConnections>().IsConnected("edge"), deadline.Token);
        await Assert.ThrowsAsync<WebSocketException>(async () => { using var replay = await Open(); });
    }

    private static async Task WaitFor(Func<bool> condition, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private static async Task Stop(Task connection)
    {
        try { await connection; }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
    }

    private sealed class Revision : INodeHandshakeConfigurationSource
    {
        public Task<NodeConfigurationRevision> CaptureAsync(CancellationToken token) => Task.FromResult(
            new NodeConfigurationRevision(null, new string('A', 64), null));
    }

    private sealed class Hosts : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "lywaf-socket-http-" + Guid.NewGuid().ToString("N"));
        public WebApplication Controller { get; private set; } = null!;
        public WebApplication Node { get; private set; } = null!;
        public int ControlPort { get; private set; }

        public static async Task<Hosts> StartAsync()
        {
            var hosts = new Hosts();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            hosts.ControlPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            hosts.Controller = hosts.Build("controller", "Controller", "edge", "Node", hosts.ControlPort);
            hosts.Node = hosts.Build("edge", "Node", "controller", "Controller", 0);
            await hosts.Controller.StartAsync();
            hosts.Node.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue.ControllerUrl = hosts.Controller.Urls.Single();
            await hosts.Node.StartAsync();
            return hosts;
        }

        private WebApplication Build(string id, string role, string peer, string peerRole, int port)
        {
            var config = new NodeCommunicationOptions
            {
                Enabled = true, RequireHttps = false, ClusterId = "cluster", NodeId = id, Role = role, Mode = role,
                StateDirectory = Path.Combine(directory, id), Peers = new()
                {
                    [peer] = new() { Role = peerRole, ActiveKeyId = "key", Keys = new() { ["key"] = Secret },
                        AllowedOperations = ["node.handshake", "runtime.merge", "runtime.sync"] }
                }
            };
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new StaticOptionsMonitor<NodeCommunicationOptions>(config));
            builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new StaticOptionsMonitor<RuntimeStateOptions>(new()
            {
                Enabled = true, NodeId = id, StateDirectory = Path.Combine(directory, id, "runtime"), Peers = new()
                {
                    [peer] = new() { Enabled = true, ProtocolVersion = 2, DurableSyncEnabled = true,
                        Endpoint = "https://never-dial.invalid/",
                        Send = new() { AllowedDomains = ["socket.counter"], AllowUngroupedKeys = true },
                        Receive = new() { AllowedDomains = ["socket.counter"], AllowUngroupedKeys = true } }
                }
            }));
            builder.Services.AddSingleton<RuntimeStateStore>();
            builder.Services.AddSingleton<IRuntimeStateStore>(s => s.GetRequiredService<RuntimeStateStore>());
            builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateStore>());
            builder.Services.AddSingleton<RuntimeStateSyncReceiver>();
            builder.Services.AddSingleton<IOptionsMonitor<PluginNodeMessageOptions>>(new StaticOptionsMonitor<PluginNodeMessageOptions>(new()));
            builder.Services.AddSingleton(DispatchProxy.Create<IPluginManager, NodeHandshakeMetadataTests.ManagerProxy>());
            builder.Services.AddSingleton<INodeHandshakeConfigurationSource, Revision>();
            builder.Services.AddSingleton<NodeIdentityService>();
            builder.Services.AddHostedService(s => s.GetRequiredService<NodeIdentityService>());
            builder.Services.AddSingleton<NodeMessageAuthentication>();
            builder.Services.AddSingleton<NodeSocketConnections>();
            builder.Services.AddSingleton<NodeSocketProtocol>();
            builder.Services.AddNodeHandshakeServices();
            var app = builder.Build();
            app.UseWebSockets();
            app.MapNodeCommunication(port);
            app.MapNodeSocket(port);
            return app;
        }

        public NodeSocketConnector Connector(IOptionsMonitor<NodeCommunicationOptions>? monitor = null) => new(monitor ?? Node.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>(),
            Node.Services.GetRequiredService<NodeHandshakeMetadataService>(), Node.Services.GetRequiredService<NodeSocketConnections>(),
            Node.Services.GetRequiredService<NodeSocketProtocol>(), Node.Services.GetRequiredService<ILogger<NodeSocketConnector>>());

        public async Task<HttpRequestMessage> SignedAsync(WebApplication app, string sender, string receiver)
        {
            var local = await app.Services.GetRequiredService<NodeHandshakeMetadataService>().CaptureAsync(default);
            var now = DateTimeOffset.UtcNow;
            var message = new NodeMessage(2, "cluster", sender, receiver, Guid.NewGuid().ToString("D"), "socket-test", "node.handshake",
                now, now.AddMinutes(1), JsonSerializer.SerializeToElement(local, NodeMessageAuthentication.JsonOptions));
            var body = JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions);
            var request = new HttpRequestMessage(HttpMethod.Post, "http://not-routable.invalid" + NodeMessageAuthentication.Prefix + message.Type)
                { Content = new ByteArrayContent(body) };
            NodeMessageAuthentication.Sign(request, message, "key", Secret, body);
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            if (Node is not null) { await Node.StopAsync(); await Node.DisposeAsync(); }
            if (Controller is not null) { await Controller.StopAsync(); await Controller.DisposeAsync(); }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
