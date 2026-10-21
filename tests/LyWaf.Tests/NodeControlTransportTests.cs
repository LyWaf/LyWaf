using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeControlTransportTests
{
    public static IEnumerable<object[]> Operations() => NodeProtocolOperations.All.SelectMany(operation =>
        new[] { "Node", "Controller" }.Select(mode => new object[] { operation, mode }));

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task EveryV2OperationUsesTheExistingSocketWithoutDialingHttp(string operation, string mode)
    {
        await using var wire = await Wire.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var configuration = Configuration(mode);
        // Local peer grants are incoming permissions, not the remote receiver's grants.
        if (mode == "Controller" && operation.StartsWith("configuration.", StringComparison.Ordinal))
            configuration.Peers["edge"].AllowedOperations.Clear();
        var peer = mode == "Node" ? "controller" : "edge";
        var connections = new NodeSocketConnections();
        using var lease = connections.Reserve(peer, configuration.ClusterId, configuration.NodeId, false)!;
        lease.Attach(wire.A);
        var a = wire.A.RunAsync((bytes, _) => Task.FromResult(bytes), deadline.Token);
        var seen = "";
        var b = wire.B.RunAsync((bytes, _) =>
        {
            seen = JsonSerializer.Deserialize<NodeSocketRequest>(bytes, NodeMessageAuthentication.JsonOptions)!.Operation;
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new NodeSocketResponse(200, "application/json", "{}"u8.ToArray()),
                NodeMessageAuthentication.JsonOptions));
        }, deadline.Token);
        var handler = new HttpRecorder();
        using var http = new HttpClient(handler);
        using var request = Request(configuration, peer, operation);
        try
        {
            using var response = await NodeControlTransport.SendAsync(configuration, connections, peer, http, request, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(operation, seen);
            Assert.Equal(0, handler.Calls);
        }
        finally { deadline.Cancel(); await Shutdown(a, b); }
    }

    [Theory]
    [InlineData("Node")]
    [InlineData("Controller")]
    public async Task DisconnectedManagedModeNeverFallsBackToHttp(string mode)
    {
        var configuration = Configuration(mode);
        var peer = mode == "Node" ? "controller" : "edge";
        var handler = new HttpRecorder();
        using var http = new HttpClient(handler);
        using var request = Request(configuration, peer, "runtime.sync");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => NodeControlTransport.SendAsync(configuration,
            new NodeSocketConnections(), peer, http, request, default));
        Assert.Equal("node_unreachable", NodeFailure.FromException(error).Code);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NodeCannotRouteDirectlyToAnotherNodeEvenWhenItHasACredentialForIt()
    {
        var config = Configuration("Node");
        config.Peers["other"] = config.Peers["controller"];
        var handler = new HttpRecorder();
        using var http = new HttpClient(handler);
        using var request = Request(config, "other", "plugin.message");
        await Assert.ThrowsAsync<InvalidOperationException>(() => NodeControlTransport.SendAsync(config,
            new NodeSocketConnections(), "other", http, request, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task LegacyModeRetainsExplicitHttpCompatibility()
    {
        var config = Configuration("Node");
        config.Mode = "";
        var handler = new HttpRecorder();
        using var http = new HttpClient(handler);
        using var request = Request(config, "controller", "runtime.sync");
        using var response = await NodeControlTransport.SendAsync(config, null, "controller", http, request, default);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("X-LyWaf-Sender")]
    [InlineData("X-LyWaf-Receiver")]
    [InlineData("X-LyWaf-Cluster")]
    public async Task WrongEnvelopeIdentityCannotSelectASocket(string header)
    {
        var config = Configuration("Node");
        var handler = new HttpRecorder();
        using var http = new HttpClient(handler);
        using var request = Request(config, "controller", "runtime.sync");
        request.Headers.Remove(header);
        request.Headers.Add(header, "wrong");
        await Assert.ThrowsAsync<InvalidOperationException>(() => NodeControlTransport.SendAsync(config,
            new NodeSocketConnections(), "controller", http, request, default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("cluster")]
    [InlineData("sender")]
    [InlineData("tls")]
    public async Task ChangedIdentityOrTlsRequirementCannotReuseAnOldSocket(string change)
    {
        await using var wire = await Wire.CreateAsync();
        var config = Configuration("Node");
        config.RequireHttps = change == "tls";
        var connections = new NodeSocketConnections();
        using var lease = connections.Reserve("controller", change == "cluster" ? "old-cluster" : config.ClusterId,
            change == "sender" ? "old-node" : config.NodeId, false)!;
        lease.Attach(wire.A);
        var handler = new HttpRecorder();
        using var http = new HttpClient(handler);
        using var request = Request(config, "controller", "runtime.sync");
        await Assert.ThrowsAsync<HttpRequestException>(() => NodeControlTransport.SendAsync(config,
            connections, "controller", http, request, default));
        Assert.Equal(0, handler.Calls);
    }

    private static NodeCommunicationOptions Configuration(string mode) => new()
    {
        Mode = mode, Role = mode, Enabled = true, RequireHttps = false, ClusterId = "cluster", NodeId = mode == "Node" ? "edge" : "controller",
        ControllerNodeId = "controller", Peers = new()
        {
            [mode == "Node" ? "controller" : "edge"] = new()
                { AllowedOperations = NodeProtocolOperations.All.ToList() }
        }
    };

    private static HttpRequestMessage Request(NodeCommunicationOptions config, string peer, string operation)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new NodeMessage(2, config.ClusterId, config.NodeId, peer, Guid.NewGuid().ToString("D"), "transport-test",
            operation, now, now.AddMinutes(1), JsonSerializer.SerializeToElement(new { }));
        var body = JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://never-dial.invalid" + NodeMessageAuthentication.Prefix + operation)
            { Content = new ByteArrayContent(body) };
        NodeMessageAuthentication.Sign(request, message, "key", "transport-test-secret-at-least-32-bytes", body);
        return request;
    }

    private sealed class HttpRecorder : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task Shutdown(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (IOException) { }
        }
    }

    private sealed class Wire(TcpClient client, TcpClient server, NodeSocketChannel a, NodeSocketChannel b) : IAsyncDisposable
    {
        public NodeSocketChannel A => a;
        public NodeSocketChannel B => b;
        public static async Task<Wire> CreateAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var server = await listener.AcceptTcpClientAsync();
            return new(client, server,
                new(WebSocket.CreateFromStream(client.GetStream(), false, null, TimeSpan.FromSeconds(5))),
                new(WebSocket.CreateFromStream(server.GetStream(), true, null, TimeSpan.FromSeconds(5))));
        }
        public async ValueTask DisposeAsync()
        {
            await a.DisposeAsync(); await b.DisposeAsync(); client.Dispose(); server.Dispose();
        }
    }
}
