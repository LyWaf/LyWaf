using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSocketReloadTests
{
    private static NodeCommunicationOptions Configuration() => new()
    {
        Enabled = true, Mode = "Node", Role = "Node", ClusterId = "cluster", NodeId = "edge",
        ControllerNodeId = "controller", ControllerUrl = "https://controller.example:7030/",
        Peers = new() { ["controller"] = new() { Role = "Controller", ActiveKeyId = "key",
            Keys = new() { ["key"] = "test-secret-at-least-thirty-two-bytes" }, AllowedOperations = ["node.handshake"] } }
    };

    [Fact]
    public void FreshOptionsWithUnchangedCommunicationKeepTheConnection()
    {
        var original = Configuration();
        var reloaded = JsonSerializer.Deserialize<NodeCommunicationOptions>(JsonSerializer.Serialize(original))!;
        Assert.NotSame(original, reloaded);
        Assert.Equal(NodeSocketConnector.ConnectionSettings(original), NodeSocketConnector.ConnectionSettings(reloaded));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("url")]
    [InlineData("tls")]
    [InlineData("key")]
    [InlineData("admission")]
    [InlineData("permissions")]
    [InlineData("limit")]
    [InlineData("disabled")]
    public void SecurityOrTransportChangesInvalidateTheCapturedSettings(string change)
    {
        var current = Configuration();
        var captured = NodeSocketConnector.ConnectionSettings(current);
        switch (change)
        {
            case "identity": current.NodeId = "other"; break;
            case "url": current.ControllerUrl = "https://other.example/"; break;
            case "tls": current.RequireHttps = false; break;
            case "key": current.Peers["controller"].Keys["key"] = "rotated-secret-at-least-thirty-two-bytes"; break;
            case "admission": current.Peers["controller"].AdmissionState = "Revoked"; break;
            case "permissions": current.Peers["controller"].AllowedOperations.Clear(); break;
            case "limit": current.MaxMessageBytes++; break;
            case "disabled": current.Enabled = false; break;
        }
        Assert.NotEqual(captured, NodeSocketConnector.ConnectionSettings(current));
    }
}
