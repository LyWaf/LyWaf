using LyWaf.Services.NodeCommunication;
using LyWaf.Services.Distribution;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeAdmissionPolicyTests
{
    [Theory]
    [InlineData(1, "PendingApproval")]
    [InlineData(2, "PendingApproval")]
    [InlineData(1, "Draining")]
    [InlineData(2, "Draining")]
    [InlineData(1, "Revoked")]
    [InlineData(2, "Revoked")]
    public async Task RestrictedTargetsCannotStartCommandsOrCreateHttpClients(int version, string state)
    {
        var distribution = new DistributionOptions
        {
            Nodes = new() { ["target"] = new() { Endpoint = "http://127.0.0.1:1", ProtocolVersion = version } }
        };
        var communication = new NodeCommunicationOptions
        {
            Enabled = true, Peers = new() { ["target"] = new() { AdmissionState = state } }
        };
        var factory = new RejectHttpFactory();
        var client = new DistributionNodeClient(new StaticOptionsMonitor<DistributionOptions>(distribution), factory,
            new StaticOptionsMonitor<NodeCommunicationOptions>(communication));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CommandAsync("target", "commit", new("deployment"), CancellationToken.None));
        Assert.Equal("Node admission state does not allow this operation", error.Message);
        Assert.Equal(0, factory.Calls);
    }

    private sealed class RejectHttpFactory : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name)
        {
            Calls++;
            throw new InvalidOperationException("No request may be sent");
        }
    }

    [Theory]
    [InlineData("PendingApproval")]
    [InlineData("Draining")]
    [InlineData("Revoked")]
    public async Task LegacyCannotBypassPeerAdmission(string state)
    {
        var communication = new NodeCommunicationOptions
        {
            Enabled = true, AllowLegacyProtocol = true,
            Peers = new() { ["controller"] = new() { AdmissionState = state } }
        };
        var auth = new DistributionAuthentication(
            new StaticOptionsMonitor<DistributionOptions>(new() { Enabled = true, Role = "Node" }),
            new StaticOptionsMonitor<NodeCommunicationOptions>(communication));
        var (value, error) = await auth.ReadAndVerifyAsync<DistributionCommandRequest>(
            new DefaultHttpContext(), "commit", CancellationToken.None);
        Assert.Null(value);
        Assert.Equal("Legacy node protocol cannot enforce restricted peer admission", error);
    }

    [Theory]
    [InlineData("PendingApproval", "node.handshake", true)]
    [InlineData("PendingApproval", "node.status", false)]
    [InlineData("PendingApproval", "configuration.prepare", false)]
    [InlineData("Draining", "configuration.result", true)]
    [InlineData("Draining", "configuration.rollback", true)]
    [InlineData("Draining", "configuration.prepare", false)]
    [InlineData("Draining", "configuration.commit", false)]
    [InlineData("Draining", "runtime.sync", false)]
    [InlineData("Revoked", "node.handshake", false)]
    [InlineData("Revoked", "configuration.result", false)]
    [InlineData("unknown", "node.handshake", false)]
    [InlineData("Active", "runtime.sync", true)]
    public void ExplicitAdmissionStatesGateOperations(string state, string operation, bool expected)
    {
        var peer = new NodePeerOptions { AdmissionState = state };
        Assert.Equal(expected, NodeAdmissionPolicy.Allows(peer, operation));
        peer.Enabled = false;
        Assert.False(NodeAdmissionPolicy.Allows(peer, operation));
    }
}
