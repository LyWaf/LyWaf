using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSocketProtocolTests
{
    [Theory]
    [MemberData(nameof(NodeOperationContractHttpTests.AuthenticationCases), MemberType = typeof(NodeOperationContractHttpTests))]
    public async Task SocketAdapterKeepsAllOperationAuthenticationContracts(string operation, string fault)
    {
        await using var host = await NodeContractHttpHost.Start();
        if (fault == "permission") host.Protocol.Peers["sender"].AllowedOperations.Clear();
        if (fault == "storage") File.WriteAllText(Path.Combine(host.Directory, "replay-v2"), "not a directory");
        using var request = Signed(operation, fault == "signature" ? new string('x', 40) : NodeContractHttpHost.Secret);
        var protocol = new NodeSocketProtocol(host.App.Services.GetRequiredService<IServiceScopeFactory>());
        using var response = NodeSocketProtocol.Decode(await protocol.DispatchAsync(
            await NodeSocketProtocol.EncodeAsync(request, default), "sender", false, default));
        var expected = fault switch
        {
            "signature" => (401, "node_invalid_signature"),
            "permission" => (403, "node_operation_forbidden"),
            _ => (503, "node_replay_store_unavailable")
        };
        Assert.Equal(expected.Item1, (int)response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        NodeOperationContractHttpTests.AssertError(json.RootElement, expected.Item2, expected.Item1 == 503);
    }

    [Fact]
    public async Task CannotImpersonateAnotherSenderEvenWithValidSignature()
    {
        await using var host = await NodeContractHttpHost.Start();
        using var request = Signed("runtime.merge");
        var protocol = new NodeSocketProtocol(host.App.Services.GetRequiredService<IServiceScopeFactory>());
        using var response = NodeSocketProtocol.Decode(await protocol.DispatchAsync(
            await NodeSocketProtocol.EncodeAsync(request, default), "different-peer", false, default));
        Assert.Equal(403, (int)response.StatusCode);
        Assert.Contains("node_identity_mismatch", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PersistentReplayGuardIsSharedAcrossSocketAndHttp()
    {
        await using var host = await NodeContractHttpHost.Start();
        var protocol = new NodeSocketProtocol(host.App.Services.GetRequiredService<IServiceScopeFactory>());
        using var request = Signed("node.handshake");
        var bytes = await NodeSocketProtocol.EncodeAsync(request, default);
        using var first = NodeSocketProtocol.Decode(await protocol.DispatchAsync(bytes, "sender", false, default));
        Assert.Equal(503, (int)first.StatusCode); // The fixture intentionally has no handshake business service.
        using var second = NodeSocketProtocol.Decode(await protocol.DispatchAsync(bytes, "sender", false, default));
        Assert.Equal(401, (int)second.StatusCode);
        request.Headers.Host = "localhost:7030";
        request.RequestUri = new Uri(host.Client.BaseAddress!, NodeMessageAuthentication.Prefix + "node.handshake");
        using var third = await host.Client.SendAsync(request);
        Assert.Equal(401, (int)third.StatusCode);
        Assert.Contains("node_replay", await third.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SocketCannotPretendPlaintextTransportIsHttps()
    {
        await using var host = await NodeContractHttpHost.Start();
        host.Protocol.RequireHttps = true;
        using var request = Signed("node.handshake");
        var protocol = new NodeSocketProtocol(host.App.Services.GetRequiredService<IServiceScopeFactory>());
        using var response = NodeSocketProtocol.Decode(await protocol.DispatchAsync(
            await NodeSocketProtocol.EncodeAsync(request, default), "sender", false, default));
        Assert.Equal(403, (int)response.StatusCode);
        Assert.Contains("node_https_required", await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Signed(string operation, string secret = NodeContractHttpHost.Secret)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new NodeMessage(2, "contract", "sender", "receiver", Guid.NewGuid().ToString("D"),
            "socket-contract", operation, now, now.AddMinutes(1), JsonSerializer.SerializeToElement(new { }));
        var body = JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:7030" + NodeMessageAuthentication.Prefix + operation)
            { Content = new ByteArrayContent(body) };
        NodeMessageAuthentication.Sign(request, message, "key", secret, body);
        return request;
    }
}
