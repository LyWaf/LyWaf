using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeMessageAuthenticationTests : IDisposable
{
    private const string Secret = "test-key-with-at-least-thirty-two-bytes";
    private readonly NodeCommunicationOptions _options = new()
    {
        Enabled = true, ClusterId = "cluster", NodeId = "edge", Role = "Node",
        StateDirectory = Path.Combine(Path.GetTempPath(), "lywaf-v2-" + Guid.NewGuid().ToString("N")),
        Peers = new()
        {
            ["control"] = new() { Role = "Controller", ActiveKeyId = "new", Keys = new() { ["new"] = Secret, ["old"] = Secret + "old" },
                AllowedOperations = ["configuration.commit"] }
        }
    };

    private NodeMessage Message() => new(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "deployment",
        "configuration.commit", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), JsonSerializer.SerializeToElement(new { deploymentId = "deployment" }));

    private static HttpRequestMessage Signed(NodeMessage message, string keyId = "new", string secret = Secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://edge" + NodeMessageAuthentication.Prefix + message.Type);
        var body = JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions);
        request.Content = new ByteArrayContent(body);
        NodeMessageAuthentication.Sign(request, message, keyId, secret, body);
        return request;
    }

    private static async Task<DefaultHttpContext> Context(HttpRequestMessage request)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        context.Request.Path = request.RequestUri!.AbsolutePath;
        foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
        context.Request.Body = new MemoryStream(await request.Content!.ReadAsByteArrayAsync());
        return context;
    }

    private Task<(NodeMessage? Message, NodeProtocolError? Error)> Read(HttpContext context) =>
        new NodeMessageAuthentication(new StaticOptionsMonitor<NodeCommunicationOptions>(_options))
            .ReadAsync(context, "configuration.commit", default);

    [Fact]
    public async Task InvalidOptionsDuringReloadFailClosedWithoutLeakingValidationText()
    {
        var auth = new NodeMessageAuthentication(new InvalidOptions());
        var result = await auth.ReadAsync(new DefaultHttpContext(), "configuration.commit", default);
        Assert.Null(result.Message);
        Assert.Equal(new NodeProtocolError("node_identity_unavailable", 503), result.Error);
        Assert.DoesNotContain("private", result.Error!.ToString());
    }

    private sealed class InvalidOptions : IOptionsMonitor<NodeCommunicationOptions>
    {
        public NodeCommunicationOptions CurrentValue => throw new OptionsValidationException("", typeof(NodeCommunicationOptions), ["private-config-value"]);
        public NodeCommunicationOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<NodeCommunicationOptions, string?> listener) => null;
    }

    [Fact]
    public async Task MultipleAdmittedControllersCannotWriteUntilOldWriterIsRevoked()
    {
        _options.Peers["second"] = new()
        {
            Role = "Controller", Keys = new() { ["new"] = Secret },
            AllowedOperations = ["configuration.rollback"]
        };
        using var request = Signed(Message());
        Assert.Equal("node_operation_forbidden", (await Read(await Context(request))).Error?.Code);
        _options.Peers["second"].AdmissionState = "Draining";
        Assert.Equal("node_operation_forbidden", (await Read(await Context(request))).Error?.Code);
        _options.Peers["second"].AdmissionState = "Revoked";
        Assert.NotNull((await Read(await Context(request))).Message);
    }

    [Fact]
    public async Task ReadOnlyControllerDoesNotCountAsConfigurationWriter()
    {
        _options.Peers["observer"] = new()
        {
            Role = "Controller", Keys = new() { ["new"] = Secret },
            AllowedOperations = ["configuration.result", "node.status"]
        };
        using var request = Signed(Message());
        Assert.NotNull((await Read(await Context(request))).Message);
    }

    [Fact]
    public async Task ValidRequestIsAcceptedAndReplayRejectedAcrossAuthenticationInstances()
    {
        using var request = Signed(Message());
        Assert.NotNull((await Read(await Context(request))).Message);
        Assert.Equal("node_replay", (await Read(await Context(request))).Error?.Code);
    }

    [Theory]
    [InlineData("cluster")]
    [InlineData("receiver")]
    [InlineData("method")]
    [InlineData("path")]
    [InlineData("query")]
    [InlineData("https")]
    [InlineData("body")]
    [InlineData("header")]
    [InlineData("duplicate")]
    public async Task BoundIdentityPathAndBodyCannotBeChanged(string field)
    {
        var message = Message();
        if (field == "cluster") message = message with { ClusterId = "other" };
        if (field == "receiver") message = message with { ReceiverNodeId = "other" };
        using var request = Signed(message);
        var context = await Context(request);
        if (field == "method") context.Request.Method = "GET";
        if (field == "path") context.Request.Path = "/api/v2/node-agent/configuration.rollback";
        if (field == "query") context.Request.QueryString = new QueryString("?action=other");
        if (field == "https") context.Request.Scheme = "http";
        if (field == "body") context.Request.Body = new MemoryStream("{}"u8.ToArray());
        if (field == "header") context.Request.Headers["X-LyWaf-Sender"] = "unknown";
        if (field == "duplicate") context.Request.Headers.Append("X-LyWaf-KeyId", "old");
        Assert.Null((await Read(context)).Message);
    }

    [Fact]
    public async Task DisabledPeerOrMissingPermissionCannotExecuteEvenWithValidKey()
    {
        using var request = Signed(Message());
        _options.Peers["control"].Enabled = false;
        Assert.Null((await Read(await Context(request))).Message);
        _options.Peers["control"].Enabled = true;
        _options.Peers["control"].AllowedOperations.Clear();
        Assert.Equal("node_operation_forbidden", (await Read(await Context(request))).Error?.Code);
    }

    [Fact]
    public async Task KeyRotationAllowsOverlapThenRevocation()
    {
        using var oldRequest = Signed(Message(), "old", Secret + "old");
        Assert.NotNull((await Read(await Context(oldRequest))).Message);
        _options.Peers["control"].Keys.Remove("old");
        using var revoked = Signed(Message(), "old", Secret + "old");
        Assert.Equal("node_unknown_identity", (await Read(await Context(revoked))).Error?.Code);
        using var current = Signed(Message());
        Assert.NotNull((await Read(await Context(current))).Message);
    }

    [Theory]
    [InlineData("/api/v2/node-agent/configuration.commit", true)]
    [InlineData("/api/v2/node-agent/%63onfiguration.commit", false)]
    [InlineData("/api/v2/node-agent//configuration.commit", false)]
    [InlineData("/api/v2/node-agent%2Fconfiguration.commit", false)]
    [InlineData("/external/rewritten", false)]
    [InlineData("/api/v2/node-agent/configuration.commit?", false)]
    public async Task RawTargetMustBeCanonicalEvenIfParsedPathWasNormalized(string rawTarget, bool accepted)
    {
        using var request = Signed(Message());
        var context = await Context(request);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        var result = await Read(context);
        Assert.Equal(accepted, result.Message is not null);
        if (!accepted) Assert.Equal("node_invalid_path", result.Error?.Code);
    }

    [Fact]
    public async Task ExpiredBusinessCommandIsRejectedEvenWithFreshSignature()
    {
        using var request = Signed(Message() with { CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        Assert.Equal("node_invalid_message", (await Read(await Context(request))).Error?.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_options.StateDirectory)) Directory.Delete(_options.StateDirectory, true);
    }
}
