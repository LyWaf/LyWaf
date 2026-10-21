using System.Text.Json;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateHubTests
{
    private static RuntimeStateOptions Options(string role, string id) => new()
    {
        Enabled = true, NodeId = id, ManagedTopologyRole = role, HubControllerNodeId = "controller",
        HubSourceIsKnown = source => source is "a" or "b" or "controller",
        Peers = new()
        {
            [role == "Node" ? "controller" : "a"] = new()
            {
                DurableSyncEnabled = true, ProtocolVersion = 2,
                Send = new() { AllowedDomains = ["cc.access", "cc.decision"], AllowUngroupedKeys = true },
                Receive = new() { AllowedDomains = ["cc.access", "cc.decision"], AllowUngroupedKeys = true }
            }
        }
    };
    private static RuntimeStateMutation Counter() => new("cc.access", "counter", "b", 1, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(1), false, JsonSerializer.SerializeToElement(new { }),
        new Dictionary<string, long> { ["a"] = 3, ["b"] = 9999 });

    [Fact]
    public void ControllerOnlyAcceptsTheUploadingNodesOwnComponentsWithoutMutatingTheWireRecord()
    {
        var options = Options("Controller", "controller");
        var raw = Counter();
        var batch = new RuntimeStateSyncBatch("cluster", "a", "controller", Guid.NewGuid().ToString("N"), 1, [raw], "");
        batch = batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };
        var accepted = Assert.Single(RuntimeStateHub.ProjectIncoming(options, "a", batch.Mutations));
        Assert.Equal("a", accepted.NodeId);
        Assert.Equal(3, Assert.Single(accepted.CounterComponents!).Value);
        Assert.Null(RuntimeStateReplicationAuthorization.AuthorizeReceive(options, "a", [accepted], true));
        RuntimeStateSyncProtocol.Validate(batch);
        Assert.Equal(9999, raw.CounterComponents!["b"]);
    }

    [Fact]
    public void CounterWithoutOwnComponentOrWithMalformedSourceCannotBeAccepted()
    {
        var options = Options("Controller", "controller");
        Assert.Throws<UnauthorizedAccessException>(() => RuntimeStateHub.ProjectIncoming(options, "a",
            [Counter() with { CounterComponents = new Dictionary<string, long> { ["b"] = 9 } }]));
        Assert.Throws<UnauthorizedAccessException>(() => RuntimeStateHub.ProjectIncoming(options, "a",
            [Counter() with { CounterComponents = new Dictionary<string, long> { ["[malformed"] = 9, ["a"] = 1 } }]));
    }

    [Fact]
    public void NodesCanUploadObservedAggregatesButCannotEchoOnlyAnotherNodesCount()
    {
        var options = Options("Node", "a");
        Assert.Null(RuntimeStateReplicationAuthorization.AuthorizeSend(options, "controller", Counter()));
        Assert.Equal("runtime_counter_source_forbidden", RuntimeStateReplicationAuthorization.AuthorizeSend(options, "controller",
            Counter() with { CounterComponents = new Dictionary<string, long> { ["b"] = 9 } }));
        Assert.Equal("runtime_source_forbidden", RuntimeStateReplicationAuthorization.AuthorizeSend(options, "controller",
            Counter() with { Domain = "cc.decision", CounterComponents = null }));
    }

    [Fact]
    public void ForwardedCountsRequireFreshControllerEndorsementButOwnCountsDoNot()
    {
        var options = Options("Node", "a");
        Assert.Null(RuntimeStateReplicationAuthorization.AuthorizeReceive(options, "controller", [Counter()], true));
        options.HubSourceIsKnown = _ => false;
        Assert.Equal("runtime_source_forbidden", RuntimeStateReplicationAuthorization.AuthorizeReceive(options, "controller", [Counter()], true));
        Assert.Null(RuntimeStateReplicationAuthorization.AuthorizeSend(options, "controller", Counter() with
            { NodeId = "a", CounterComponents = new Dictionary<string, long> { ["a"] = 3 } }));
    }

    [Fact]
    public void LegacyReplicationDoesNotGainAnyImplicitForwardingPermission()
    {
        var options = Options("Node", "a");
        options.ManagedTopologyRole = "";
        Assert.Equal("runtime_source_forbidden", RuntimeStateReplicationAuthorization.AuthorizeReceive(options, "controller", [Counter()], true));
        Assert.Equal("runtime_source_forbidden", RuntimeStateReplicationAuthorization.AuthorizeSend(options, "controller", Counter()));
    }
}
