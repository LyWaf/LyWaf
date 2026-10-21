using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcHubEvidenceTests
{
    [Theory]
    [InlineData("signature")]
    [InlineData("source")]
    [InlineData("hub")]
    [InlineData("hub-boot")]
    [InlineData("child-boot")]
    [InlineData("child-timing")]
    [InlineData("relay-latency")]
    [InlineData("clock-skew")]
    [InlineData("membership")]
    [InlineData("revocation")]
    [InlineData("key")]
    [InlineData("age")]
    public async Task RelayedCoverageRequiresCurrentControllerEndorsementAndConservativeTiming(string fault)
    {
        const string secret = "cc-hub-evidence-secret-at-least-32-bytes";
        var root = Path.Combine(Path.GetTempPath(), "cc-hub-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var window = CcDecisionService.Window(DateTimeOffset.UtcNow.AddMinutes(1), 1);
            var clock = new Clock(window.AddSeconds(-1));
            var node = new NodeCommunicationOptions { Enabled = true, Mode = "Node", Role = "Node", ClusterId = "cluster",
                NodeId = "a", ControllerNodeId = "hub", StateDirectory = Path.Combine(root, "identity"), Peers = new()
                { ["hub"] = new() { Role = "Controller", ActiveKeyId = "key", Keys = new() { ["key"] = secret },
                    AllowedOperations = ["node.handshake", CcExecutionRelayService.Operation] } } };
            var hub = new NodeCommunicationOptions { Enabled = true, Mode = "Controller", Role = "Controller", ClusterId = "cluster",
                NodeId = "hub", Peers = new() { ["a"] = new() { Role = "Node", ActiveKeyId = "key", Keys = new() { ["key"] = secret },
                    AllowedOperations = ["node.handshake"] }, ["b"] = new() { Role = "Node" } } };
            var membership = new NodeHubMembership(clock);
            var request = new NodeMessage(2, "cluster", "a", "hub", Guid.NewGuid().ToString("D"), "membership", "node.handshake",
                clock.Now, clock.Now.AddMinutes(1), JsonSerializer.SerializeToElement(new { }));
            var handshake = new NodeHandshake("cluster", "hub", Guid.NewGuid().ToString("N"), "Controller", clock.Now, "1", [2]);
            membership.Accept(node, request, handshake, NodeHubMembership.Create(hub, request, handshake, "key", clock.Now));
            var runtime = new RuntimeStateOptions { Enabled = true, NodeId = "a", StateDirectory = Path.Combine(root, "runtime"),
                Peers = new() { ["hub"] = new() { Endpoint = "https://hub", ProtocolVersion = 2 } } };
            var runtimeOptions = new StaticOptionsMonitor<RuntimeStateOptions>(runtime);
            using var store = new RuntimeStateStore(runtimeOptions);
            var communication = new StaticOptionsMonitor<NodeCommunicationOptions>(node);
            using var identity = new NodeIdentityService(communication);
            await identity.StartAsync(default);
            var cohort = new CcCohortState(store, runtimeOptions, identity, clock, communication, membership);
            var rule = new AdvancedCcRule { Id = "rule", Period = 1, ExpectedNodes = ["a", "b"], RequireCompleteCoverage = true };
            var policy = new CcPolicyDescriptor(rule.Id, CcRuleStateKey.Revision(rule), "host:a.example", "192.0.2.1",
                CcRuleStateKey.Create(rule, "192.0.2.1", "host:a.example"), CcPolicyGeneration.Initial);
            cohort.ObserveRules([rule]);
            clock.Now = window.AddMilliseconds(100);
            store.IncrementWindow("cc.access", policy.StateKey, window, TimeSpan.FromSeconds(1), 40);
            var evidence = new CcWindowEvidence("b", Guid.NewGuid().ToString("N"), policy.PolicyKey, policy.PolicyGeneration,
                policy.RuleRevision, true, window.AddSeconds(-1), window, clock.Now, 65, true);
            var reply = new CcPeerReply("request", "digest", evidence,
                new("b", "decision", "Unknown", clock.Now, null, policy.PolicyGeneration), "key", "",
                ViaController: "hub", ControllerBootId: handshake.BootId);
            CcPeerReply Signed(CcPeerReply item) => item with { Signature = CcPeerQueryClient.Sign(item, secret) };
            void Accept(CcPeerReply item, double elapsed = 0) => cohort.Accept(policy, "b", "https://hub", item,
                clock.Now.AddMilliseconds(-elapsed), clock.Now, elapsed);
            Accept(Signed(reply));
            Assert.Equal("Complete", cohort.Read(policy, rule).Coverage.Status);
            Assert.Equal(105, cohort.Read(policy, rule).Count);
            var changed = fault switch
            {
                "source" => reply with { Evidence = evidence with { NodeId = "c" } },
                "hub" => reply with { ViaController = "other" },
                "hub-boot" => reply with { ControllerBootId = Guid.NewGuid().ToString("N") },
                "child-boot" => reply with { Evidence = evidence with { BootId = Guid.NewGuid().ToString("N") } },
                "child-timing" => reply with { ReceivedAt = clock.Now, ProcessingMilliseconds = 0 },
                "clock-skew" => reply with { Evidence = evidence with { Watermark = clock.Now.AddMilliseconds(251) } },
                _ => reply
            };
            changed = Signed(changed);
            if (fault == "signature") changed = changed with { Signature = new string('0', 64) };
            Accept(changed, fault == "relay-latency" ? 600 : 0);
            if (fault == "membership") membership.Invalidate();
            if (fault == "revocation") node.Peers["hub"].AdmissionState = "Revoked";
            if (fault == "key") node.Peers["hub"].Keys.Clear();
            if (fault == "age") clock.Now = clock.Now.AddMilliseconds(251);
            Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
            if (fault == "child-boot")
            {
                Accept(changed);
                Assert.Equal("Complete", cohort.Read(policy, rule).Coverage.Status);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
