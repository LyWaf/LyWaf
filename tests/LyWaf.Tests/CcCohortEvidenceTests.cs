using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcCohortEvidenceTests
{
    [Fact]
    public void LegacyProofWithoutTimingRetainsExactSigningShape()
    {
        var now = DateTimeOffset.UtcNow;
        var reply = new CcPeerReply("request", "digest", new("b", "boot", "policy", CcPolicyGeneration.Initial,
            "revision", true, now, now, now, 0, true),
            new("b", "decision", "Unknown", now, null, CcPolicyGeneration.Initial), "key", "");
        const string secret = "legacy-proof-key-at-least-32-bytes";
        var original = new { reply.RequestId, reply.RequestDigest, reply.Evidence, reply.Execution, reply.KeyId, Signature = "" };
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(original, NodeMessageAuthentication.JsonOptions)));
        Assert.Equal(expected, CcPeerQueryClient.Sign(reply, secret));
    }

    [Fact]
    public async Task RemoteEvidenceWakesIdleDecisionWorkerWithoutAnotherBusinessRequest()
    {
        var window = CcDecisionService.Window(DateTimeOffset.UtcNow.AddMinutes(1), 1);
        var clock = new Clock(window.AddSeconds(-1));
        var runtime = new RuntimeStateOptions { Enabled = true, NodeId = "a", StateDirectory = Temp(),
            Peers = new() { ["b"] = new() { Endpoint = "https://b", ProtocolVersion = 2 } } };
        CcReleaseTestKeys.Configure(runtime);
        var options = new StaticOptionsMonitor<RuntimeStateOptions>(runtime);
        using var store = new RuntimeStateStore(options);
        using var identity = new NodeIdentityService(new StaticOptionsMonitor<NodeCommunicationOptions>(new()
            { Enabled = true, NodeId = "a", ClusterId = "cluster", StateDirectory = Temp() }));
        await identity.StartAsync(default);
        var cohort = new CcCohortState(store, options, identity, clock);
        var rule = new AdvancedCcRule { Id = "wake", Period = 1, Threshold = 100, ActionSeconds = 10,
            Action = CcAction.Block, ExpectedNodes = ["a", "b"], RequireCompleteCoverage = true };
        var statistics = System.Reflection.DispatchProxy.Create<IStatisticService, CcCohortHttpTests.Rules>();
        ((CcCohortHttpTests.Rules)(object)statistics).Rule = rule;
        var log = new CcBanLog();
        using var decisions = new CcDecisionService(statistics, store, log, clock, cohort);
        cohort.ObserveRules([rule]); clock.Now = window.AddMilliseconds(100);
        var policy = decisions.Register(rule, "host:a.example", "192.0.2.1");
        store.IncrementWindow("cc.access", policy.StateKey, window, TimeSpan.FromSeconds(1), 40);
        await decisions.StartAsync(default);
        try
        {
            await Task.Delay(200); Assert.Empty(log.Read());
            var reply = new CcPeerReply("request", "digest", new("b", Guid.NewGuid().ToString("N"),
                policy.PolicyKey, policy.PolicyGeneration, policy.RuleRevision, true, window.AddSeconds(-1),
                window, clock.Now, 65, true), new("b", "decision", "Unknown", clock.Now, null, policy.PolicyGeneration), "key", "");
            cohort.Accept(policy, "b", "https://b", reply, clock.Now, clock.Now, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            while (log.Read().Length == 0) await Task.Delay(5, timeout.Token);
            Assert.Equal(105, Assert.Single(log.Read()).Nodes!["a"].ObservedCount);
            var decision = Assert.Single(store.GetValues<CcDecision>(CcDecisionService.DecisionDomain)).Value;
            cohort.Invalidate("b");
            Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
            Assert.Equal(decision.DecisionId, decisions.Evaluate(policy, rule, window, 0).Decision?.DecisionId);
            Assert.Equal("Applied", decisions.InspectExecution(policy, decision.DecisionId).Status);
            // A new process-local evaluator cannot trust a received projection without fresh admission.
            using var restarted = new CcDecisionService(statistics, store, new CcBanLog(), clock, cohort);
            Assert.Null(restarted.Evaluate(policy, rule, window, 0).Decision);
            store.AdvanceCcPolicyGeneration(policy.PolicyKey);
            Assert.Null(decisions.Evaluate(policy, rule, window, 0).Decision);
            Assert.Equal("Released", decisions.InspectExecution(policy, decision.DecisionId).Status);
        }
        finally { await decisions.StopAsync(default); }
    }

    [Fact]
    public async Task BootChangeClockSkewOfflineAndGenerationFenceInvalidateCoverage()
    {
        var clock = new Clock(CcDecisionService.Window(DateTimeOffset.UtcNow.AddMinutes(1), 1));
        var runtime = new RuntimeStateOptions { Enabled = true, NodeId = "a", StateDirectory = Temp(), Peers = new()
        { ["b"] = new() { Endpoint = "https://b", ProtocolVersion = 2 } } };
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(runtime));
        using var identity = new NodeIdentityService(new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        { Enabled = true, NodeId = "a", ClusterId = "cluster", StateDirectory = Temp() }));
        await identity.StartAsync(default);
        var cohort = new CcCohortState(store, new StaticOptionsMonitor<RuntimeStateOptions>(runtime), identity, clock);
        var rule = new AdvancedCcRule { Id = "rule", Period = 1, Action = CcAction.Block, ExpectedNodes = ["a", "b"], RequireCompleteCoverage = true };
        var policy = new CcPolicyDescriptor(rule.Id, CcRuleStateKey.Revision(rule), "host:a.example", "192.0.2.1",
            CcRuleStateKey.Create(rule, "192.0.2.1", "host:a.example"), CcPolicyGeneration.Initial);
        cohort.ObserveRules([rule]); clock.Now = clock.Now.AddSeconds(1);
        var window = clock.Now; var boot = Guid.NewGuid().ToString("N");
        var evidence = new CcWindowEvidence("b", boot, policy.PolicyKey, policy.PolicyGeneration, policy.RuleRevision, true,
            window.AddSeconds(-1), window, clock.Now, 0, true);
        CcPeerReply Reply(CcWindowEvidence e) => new("request", "digest", e,
            new("b", "decision", "Applied", clock.Now, clock.Now.AddSeconds(10), policy.PolicyGeneration), "key", "");
        void Accept(CcWindowEvidence e) => cohort.Accept(policy, "b", "https://b", Reply(e), clock.Now, clock.Now, 0);
        Accept(evidence);
        Assert.Equal("Complete", cohort.Read(policy, rule).Coverage.Status);
        var timed = Reply(evidence with { Watermark = clock.Now.AddMilliseconds(-10) }) with
        { ReceivedAt = clock.Now.AddMilliseconds(-440), ProcessingMilliseconds = 430 };
        cohort.Accept(policy, "b", "https://b", timed, clock.Now.AddMilliseconds(-450), clock.Now, 450);
        Assert.Equal("Complete", cohort.Read(policy, rule).Coverage.Status);
        cohort.Accept(policy, "b", "https://b", timed with { ReceivedAt = null, ProcessingMilliseconds = null },
            clock.Now.AddMilliseconds(-450), clock.Now, 450);
        Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
        cohort.Accept(policy, "b", "https://b", timed with { ProcessingMilliseconds = 200 },
            clock.Now.AddMilliseconds(-450), clock.Now, 450);
        Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
        cohort.Accept(policy, "b", "https://b", timed with
            { ReceivedAt = timed.ReceivedAt!.Value.AddMilliseconds(251), Evidence = timed.Evidence with { Watermark = timed.Evidence.Watermark.AddMilliseconds(251) } },
            clock.Now.AddMilliseconds(-450), clock.Now, 450);
        Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
        var newBoot = evidence with { BootId = Guid.NewGuid().ToString("N") };
        Accept(newBoot); Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
        Accept(newBoot); Assert.Equal("Complete", cohort.Read(policy, rule).Coverage.Status);
        Accept(newBoot with { Watermark = clock.Now.AddMilliseconds(251) });
        Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
        Accept(newBoot with { LossFree = false }); Assert.Equal("Degraded", cohort.Read(policy, rule).Coverage.Status);
        Accept(newBoot with { Generation = new(1, new string('a', 32)) }); Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
        Accept(newBoot); runtime.Peers["b"].Enabled = false;
        Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
        runtime.Peers["b"].Enabled = true; runtime.Peers["b"].Endpoint = "https://replacement";
        Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
        runtime.Peers["b"].Endpoint = "https://b"; clock.Now = clock.Now.AddMilliseconds(251);
        Assert.Equal("Partial", cohort.Read(policy, rule).Coverage.Status);
    }

    [Fact]
    public async Task ReleaseConfirmationRequiresFreshMatchingBootBoundFenceNotAnOldExecutionReceipt()
    {
        var runtime = CcReleaseTestKeys.Configure(new RuntimeStateOptions { Enabled = true, NodeId = "a", StateDirectory = Temp(),
            Peers = new() { ["b"] = new() { Endpoint = "https://b", ProtocolVersion = 2 } } });
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(runtime));
        using var identity = new NodeIdentityService(new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        { Enabled = true, NodeId = "a", ClusterId = "cluster", StateDirectory = Temp() }));
        await identity.StartAsync(default);
        var cohort = new CcCohortState(store, new StaticOptionsMonitor<RuntimeStateOptions>(runtime), identity);
        var rule = new AdvancedCcRule { Id = "rule", Action = CcAction.Block, ExpectedNodes = ["a", "b"] };
        var key = CcRuleStateKey.Create(rule, "192.0.2.1", "host:a.example");
        var generation = store.AdvanceCcPolicyGeneration(key);
        var policy = new CcPolicyDescriptor(rule.Id, CcRuleStateKey.Revision(rule), "host:a.example", "192.0.2.1", key, generation);
        var now = DateTimeOffset.UtcNow;
        var evidence = new CcWindowEvidence("b", Guid.NewGuid().ToString("N"), key, CcPolicyGeneration.Initial,
            policy.RuleRevision, true, now, now, now, 0, true);
        var reply = new CcPeerReply("request", "digest", evidence,
            new("b", "old-decision", "Applied", now, now.AddSeconds(10), CcPolicyGeneration.Initial), "key", "");
        cohort.Accept(policy, "b", "https://b", reply, now, now, 0);
        Assert.False(cohort.Release(policy, rule).AllNodesConfirmed);
        cohort.Accept(policy, "b", "https://b", reply with { Evidence = evidence with { Generation = generation } }, now, now, 0);
        Assert.True(cohort.Release(policy, rule).AllNodesConfirmed);
        Assert.Equal("boot-bound-release-fence", cohort.Release(policy, rule).ConfirmationKind);
        cohort.Invalidate("b"); Assert.False(cohort.Release(policy, rule).AllNodesConfirmed);
    }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "cc-evidence-" + Guid.NewGuid().ToString("N"));
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
}
