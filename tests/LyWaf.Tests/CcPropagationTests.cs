using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcPropagationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-acks-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ActiveCcReceiptSurvivesOrdinaryTrafficAndRestartButNotEndpointChange()
    {
        var options = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        { Enabled = true, NodeId = "source", StateDirectory = _root, Peers = new() { ["a"] = Peer() } });
        options.CurrentValue.Peers["a"].Send.AllowedDomains.Add("cc.access");
        var sender = new RuntimeStateSyncSender(options);
        var mutation = new RuntimeStateMutation("cc.decision", "v2:policy:192.0.2.1", "source", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), false, JsonSerializer.SerializeToElement("decision"));
        var first = sender.Prepare("cluster", "source", "a", [mutation]);
        sender.Confirm(first, Ack(first));
        for (var i = 0; i < 70; i++)
        {
            var batch = sender.Prepare("cluster", "source", "a", [mutation with { Domain = "cc.access", Sequence = i + 2 }]);
            sender.Confirm(batch, Ack(batch));
        }
        var restarted = new RuntimeStateSyncSender(options);
        Assert.Single(restarted.ReadCcConfirmations("cluster", "source", "a"));
        options.CurrentValue.Peers["a"].Endpoint = "https://replacement";
        Assert.Empty(restarted.ReadCcConfirmations("cluster", "source", "a"));
    }

    [Fact]
    public void PendingWrongPayloadAndOfflinePeersCannotBecomeAllConfirmed()
    {
        var options = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "source", StateDirectory = _root,
            Peers = new() { ["a"] = Peer(), ["b"] = Peer() }
        });
        var protocol = new StaticOptionsMonitor<NodeCommunicationOptions>(new() { Enabled = true, NodeId = "source", ClusterId = "cluster" });
        var sender = new RuntimeStateSyncSender(options);
        var tracker = new CcPropagationTracker(options, protocol, sender);
        var mutation = new RuntimeStateMutation("cc.decision", "v2:policy:192.0.2.1", "source", 1,
            DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement("decision-1"));
        CcPropagationStatus Read() => tracker.Read(m => m.Domain == mutation.Domain && m.Key == mutation.Key && m.Value?.GetString() == "decision-1");
        var first = sender.Prepare("cluster", "source", "a", [mutation]);
        Assert.Empty(Read().AcknowledgedNodes);
        sender.Confirm(first, Ack(first));
        Assert.Equal(new[] { "a" }, Read().AcknowledgedNodes);
        Assert.False(Read().AllNodesConfirmed);
        var wrong = sender.Prepare("cluster", "source", "b", [mutation with { Value = JsonSerializer.SerializeToElement("decision-2") }]);
        sender.Confirm(wrong, Ack(wrong));
        Assert.False(Read().AllNodesConfirmed);
        var second = sender.Prepare("cluster", "source", "b", [mutation]);
        sender.Confirm(second, Ack(second));
        Assert.True(Read().AllNodesConfirmed);
        Assert.True(new CcPropagationTracker(options, protocol, new RuntimeStateSyncSender(options))
            .Read(m => m.Domain == mutation.Domain && m.Key == mutation.Key && m.Value?.GetString() == "decision-1").AllNodesConfirmed);
        options.CurrentValue.Peers["b"].Enabled = false;
        Assert.False(Read().AllNodesConfirmed);
        Assert.Equal(new[] { "b" }, Read().PendingNodes);
        options.CurrentValue.Peers["b"].Enabled = true;
        options.CurrentValue.Peers["b"].DurableSyncEnabled = false;
        Assert.False(Read().AllNodesConfirmed);
        options.CurrentValue.Peers["b"].DurableSyncEnabled = true;
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "runtime-sync"), "*.json")) File.WriteAllText(file, "corrupt");
        Assert.Empty(Read().AcknowledgedNodes);
        Assert.False(Read().AllNodesConfirmed);
    }

    private static RuntimeStatePeerOptions Peer() => new()
    {
        Endpoint = "https://peer", ProtocolVersion = 2, DurableSyncEnabled = true,
        Send = new() { AllowedDomains = ["cc.decision"], AllowUngroupedKeys = true }
    };
    private static RuntimeStateSyncAck Ack(RuntimeStateSyncBatch batch) => new(batch.ClusterId, batch.SenderNodeId,
        batch.ReceiverNodeId, batch.Epoch, batch.Sequence, batch.Digest);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
