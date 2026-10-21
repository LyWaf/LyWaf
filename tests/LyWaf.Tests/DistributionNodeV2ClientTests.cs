using System.Net;
using System.Net.Http.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class DistributionNodeV2ClientTests : IDisposable
{
    private const string Key = "integration-test-key-at-least-32-bytes";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lywaf-client-v2-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("prepare")]
    [InlineData("commit")]
    [InlineData("rollback")]
    public async Task ClientEnvelopePassesReceiverAuthentication(string operation)
    {
        var receiver = Receiver("configuration." + operation);
        using var transport = new ReceiverTransport(receiver);
        var client = Client(transport);
        var result = operation == "prepare"
            ? await client.PrepareAsync("edge", new("deployment", "version", "hash", "content"), default)
            : await client.CommandAsync("edge", operation, new("deployment"), default);
        Assert.True(result.Ack);
        Assert.Equal("edge", result.NodeId);
        var message = Assert.Single(transport.Messages);
        Assert.Equal("control", message.SenderNodeId);
        Assert.Equal("deployment", message.CorrelationId);
        Assert.Equal("deployment", message.Payload.GetProperty("deploymentId").GetString());
        if (operation == "prepare") Assert.Equal("content", message.Payload.GetProperty("content").GetString());
    }

    [Fact]
    public async Task RetriedCommandKeepsBusinessMessageIdButPassesFreshReplayAuthentication()
    {
        using var transport = new ReceiverTransport(Receiver("configuration.commit"));
        var client = Client(transport);
        await client.CommandAsync("edge", "commit", new("deployment"), default);
        await client.CommandAsync("edge", "commit", new("deployment"), default);
        await client.CommandAsync("edge", "commit", new("other-deployment"), default);
        Assert.Equal(2, transport.Messages.Count);
        Assert.NotEqual(transport.Messages[0].MessageId, transport.Messages[1].MessageId);
    }

    [Fact]
    public async Task ReceiverPermissionFailureIsNotReportedAsAcknowledgement()
    {
        using var transport = new ReceiverTransport(Receiver("node.status"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(transport)
            .CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("node_operation_forbidden", error.Data["ErrorCode"]);
        Assert.Equal("node.errors.node_operation_forbidden", error.Data["MessageKey"]);
        Assert.Empty(transport.Messages);
    }

    [Theory]
    [InlineData("node", true)]
    [InlineData("deployment", true)]
    [InlineData("stage", true)]
    [InlineData("node", false)]
    [InlineData("deployment", false)]
    [InlineData("stage", false)]
    public async Task UnrelatedAcknowledgementIsRejected(string mismatch, bool ack)
    {
        using var transport = new ReceiverTransport(Receiver("configuration.commit"));
        transport.Transform = value => value with
        {
            Ack = ack,
            NodeId = mismatch == "node" ? "other-edge" : value.NodeId,
            DeploymentId = mismatch == "deployment" ? "old-deployment" : value.DeploymentId,
            Stage = mismatch == "stage" ? "rollback" : value.Stage
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(transport)
            .CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Single(transport.Messages);
    }

    [Theory]
    [InlineData("ftp://edge/")]
    [InlineData("https://edge/prefix")]
    [InlineData("https://user:password@edge/")]
    [InlineData("https://edge/?query=1")]
    public async Task InvalidEndpointNeverReachesTransport(string endpoint)
    {
        using var transport = new ReceiverTransport(Receiver("configuration.commit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(transport, endpoint)
            .CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public async Task DisabledLegacyProtocolCannotSilentlyDowngrade()
    {
        using var transport = new ReceiverTransport(Receiver("configuration.commit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(transport, protocol: 1)
            .CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Equal(0, transport.Requests);
    }

    private NodeMessageAuthentication Receiver(string operation) => new(new StaticOptionsMonitor<NodeCommunicationOptions>(new()
    {
        Enabled = true, ClusterId = "cluster", NodeId = "edge", Role = "Node", StateDirectory = _directory,
        Peers = new() { ["control"] = new() { Role = "Controller", Keys = new() { ["key"] = Key }, AllowedOperations = [operation] } }
    }));

    private DistributionNodeClient Client(ReceiverTransport transport, string endpoint = "https://edge/", int protocol = 2) => new(
        new StaticOptionsMonitor<DistributionOptions>(new()
        {
            Nodes = new() { ["edge"] = new() { Endpoint = endpoint, ProtocolVersion = protocol } }
        }), transport, new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "control", Role = "Controller", RequireHttps = false, AllowLegacyProtocol = false, StateDirectory = _directory,
            Peers = new() { ["edge"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = Key } } }
        }));

    private sealed class ReceiverTransport(NodeMessageAuthentication authentication) : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests { get; private set; }
        public Func<NodeAcknowledgement, NodeAcknowledgement> Transform { get; set; } = value => value;
        public List<NodeMessage> Messages { get; } = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var context = new DefaultHttpContext();
            context.Request.Method = request.Method.Method;
            context.Request.Scheme = request.RequestUri!.Scheme;
            context.Request.Path = request.RequestUri.AbsolutePath;
            context.Request.QueryString = new(request.RequestUri.Query);
            foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
            using var body = new MemoryStream(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            context.Request.Body = body;
            var operation = request.RequestUri.AbsolutePath[NodeMessageAuthentication.Prefix.Length..];
            var (message, error) = await authentication.ReadAsync(context, operation, cancellationToken);
            if (error is not null) return new((HttpStatusCode)error.StatusCode)
            {
                Content = JsonContent.Create(new { success = false, errorCode = error.Code })
            };
            Messages.Add(message!);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Transform(new NodeAcknowledgement(true, "edge", message!.CorrelationId,
                    operation["configuration.".Length..])))
            };
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
