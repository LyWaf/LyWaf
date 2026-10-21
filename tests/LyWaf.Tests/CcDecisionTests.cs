using System.Reflection;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Http;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcDecisionTests
{
    [Theory]
    [InlineData(true, true, 200)]
    [InlineData(false, true, 403)]
    [InlineData(true, false, 403)]
    public void ExecutionQueryRequiresIndependentOperationAndGroupDisclosure(bool operation, bool disclosure, int status)
    {
        using var node = new Node("a");
        var decision = node.Decide();
        var runtime = new RuntimeStateOptions { Enabled = true, NodeId = "a", Peers = new()
        { ["reader"] = new() { Send = new() { AllowedDomains = disclosure ? ["cc.decision"] : [], AllowUngroupedKeys = true } } } };
        var config = new NodeCommunicationOptions { Enabled = true, NodeId = "a", ClusterId = "cluster", Peers = new()
        { ["reader"] = new() { AllowedOperations = operation ? [CcExecutionQueryService.Operation] : ["runtime.merge"] } } };
        var service = new CcExecutionQueryService(node.Service, new StaticOptionsMonitor<NodeCommunicationOptions>(config),
            new StaticOptionsMonitor<RuntimeStateOptions>(runtime));
        var now = DateTimeOffset.UtcNow;
        var message = new NodeMessage(2, "cluster", "reader", "a", Guid.NewGuid().ToString("D"), "query",
            CcExecutionQueryService.Operation, now, now.AddMinutes(1), JsonSerializer.SerializeToElement(
                new CcExecutionQuery(decision.Policy, decision.DecisionId), NodeMessageAuthentication.JsonOptions));
        var context = new DefaultHttpContext();
        NodeProtocolResponse.BindAuthenticatedMessage(context, message);
        var result = service.HandleAuthenticated(message, context);
        Assert.Equal(status, ((IStatusCodeHttpResult)result).StatusCode ?? 200);
        Assert.Equal("Applied", node.Service.InspectExecution(decision.Policy, decision.DecisionId).Status);
    }

    [Fact]
    public void SharedCoverageIsPartialAndCompleteOnlyModeDoesNotActOnObservedSum()
    {
        using var node = new Node("a");
        node.Rule.ExpectedNodes = ["a", "b", "c"];
        node.Rule.RequireCompleteCoverage = true;
        var coverage = node.Service.Coverage(node.Rule);
        Assert.Equal("Partial", coverage.Status);
        Assert.Equal(new[] { "a", "b", "c" }, coverage.MissingNodes);
        Assert.Contains("window-clock-bound-unavailable", coverage.Reasons);
        var p = node.Register();
        var window = CcDecisionService.Window(DateTimeOffset.UtcNow, node.Rule.Period);
        Assert.Null(node.Service.Evaluate(p, node.Rule, window, 105).Decision);
        Assert.Equal("Degraded", node.Service.InspectExecution(p, CcDecisionService.DecisionId(p, window)).Status);
        Assert.Empty(node.Store.GetSnapshots("cc.triggered"));
        node.Rule.Scope = CcScope.Node;
        Assert.Equal("Complete", node.Service.Coverage(node.Rule).Status);
        Assert.NotNull(node.Service.Evaluate(node.Register(), node.Rule, window, 105).Decision);
    }

    [Fact]
    public void ExecutionQuerySeparatesReceivedProjectionReleaseAndInactiveRevision()
    {
        using var node = new Node("a");
        var decision = node.Decide();
        var applied = node.Service.InspectExecution(decision.Policy, decision.DecisionId);
        Assert.Equal("Applied", applied.Status);
        Assert.False(applied.AllNodesConfirmed);
        Assert.Equal("local-projection", applied.ConfirmationKind);
        node.Store.Remove("cc.triggered", decision.Policy.StateKey);
        Assert.Equal("Pending", node.Service.InspectExecution(decision.Policy, decision.DecisionId).Status);
        node.Store.AdvanceCcPolicyGeneration(decision.Policy.PolicyKey);
        Assert.Equal("Released", node.Service.InspectExecution(decision.Policy, decision.DecisionId).Status);
        node.Rule.Threshold++;
        Assert.Equal("RevisionInactive", node.Service.InspectExecution(decision.Policy, decision.DecisionId).Status);
    }

    [Fact]
    public void IdentityBindsScopeRevisionSubjectWindowAndGenerationButNotNodeOrCount()
    {
        using var a = new Node("a");
        using var b = new Node("b", a.Rule);
        var p = a.Register();
        var q = b.Register();
        var window = CcDecisionService.Window(DateTimeOffset.UtcNow, a.Rule.Period);
        Assert.Equal(CcDecisionService.DecisionId(p, window), CcDecisionService.DecisionId(q, window));
        Assert.NotEqual(CcDecisionService.DecisionId(p, window), CcDecisionService.DecisionId(p, window.AddSeconds(a.Rule.Period)));
        Assert.NotEqual(CcDecisionService.DecisionId(p, window), CcDecisionService.DecisionId(p with { PolicyGeneration = new(1, new string('a', 32)) }, window));
        Assert.NotEqual(CcDecisionService.DecisionId(p, window), CcDecisionService.DecisionId(a.Service.Register(a.Rule, "host:other.example", p.Subject), window));
        a.Rule.Threshold++;
        Assert.NotEqual(CcDecisionService.DecisionId(p, window), CcDecisionService.DecisionId(a.Register(), window));
    }

    [Fact]
    public async Task BackgroundMergeCreatesPenaltyWithoutAnotherRequest()
    {
        using var a = new Node("a");
        using var b = new Node("b", a.Rule);
        var p = a.Register();
        var window = CcDecisionService.Window(DateTimeOffset.UtcNow, a.Rule.Period);
        a.Store.IncrementWindow("cc.access", p.StateKey, window, TimeSpan.FromSeconds(a.Rule.Period), 105);
        await b.Service.StartAsync(default);
        try
        {
            foreach (var m in a.Store.ExportMutations()) b.Store.Merge(m);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (b.Log.Read().Length == 0) await Task.Delay(20, timeout.Token);
            var log = Assert.Single(b.Log.Read());
            Assert.Equal(105, log.Nodes!["b"].ObservedCount);
            Assert.True(b.Store.TryGet<DateTimeOffset>("cc.triggered", p.StateKey, out _));
        }
        finally { await b.Service.StopAsync(default); }
    }

    [Fact]
    public void ThreeConcurrentOriginsProduceOneLogicalLogAndDoNotRenewOnReplay()
    {
        using var a = new Node("a"); using var b = new Node("b", a.Rule); using var c = new Node("c", a.Rule);
        var nodes = new[] { a, b, c };
        foreach (var node in nodes) node.Decide();
        var messages = nodes.SelectMany(n => n.Store.ExportMutations()).ToArray();
        foreach (var node in nodes)
        {
            foreach (var message in messages.Reverse().Concat(messages)) node.Store.Merge(message);
            node.Service.Sweep();
        }
        var id = Assert.Single(a.Log.Read()).DecisionId;
        Assert.All(nodes, node =>
        {
            var entry = Assert.Single(node.Log.Read());
            Assert.Equal(id, entry.DecisionId);
            Assert.Equal(3, entry.Nodes!.Count);
            var key = node.Register().StateKey;
            Assert.True(node.Store.TryGet<DateTimeOffset>("cc.triggered", key, out var before));
            for (var i = 0; i < 10; i++) node.Service.Sweep();
            Assert.True(node.Store.TryGet<DateTimeOffset>("cc.triggered", key, out var after));
            Assert.Equal(before, after);
            Assert.Single(node.Log.Read());
        });
    }

    [Fact]
    public void ExpiredWindowAndInvalidPolicyNeverCreateDecision()
    {
        using var node = new Node("a");
        var p = node.Register();
        var window = CcDecisionService.Window(DateTimeOffset.UtcNow, node.Rule.Period);
        node.Store.IncrementWindow("cc.access", p.StateKey, window.AddSeconds(-node.Rule.Period), TimeSpan.FromSeconds(node.Rule.Period), 1000);
        node.Service.Sweep();
        Assert.Empty(node.Log.Read());
        node.Store.Set(CcDecisionService.PolicyDomain, p.StateKey, p with { RuleRevision = "forged" });
        node.Store.IncrementWindow("cc.access", p.StateKey, window, TimeSpan.FromSeconds(node.Rule.Period), 105);
        node.Service.Sweep();
        Assert.Empty(node.Log.Read());
        node.Store.Set(CcDecisionService.PolicyDomain, p.StateKey, p with { PolicyGeneration = null! });
        node.Service.Sweep();
        Assert.Empty(node.Log.Read());
    }

    [Fact]
    public void WindowIsConsumedAfterPunishmentExpiresAndUiEvictionDoesNotRearmIt()
    {
        var now = CcDecisionService.Window(DateTimeOffset.UtcNow, 3600).AddSeconds(1);
        using var node = new Node("a", clock: new Clock(now));
        var p = node.Register();
        var window = CcDecisionService.Window(now, node.Rule.Period);
        var first = node.Service.Evaluate(p, node.Rule, window, 105).Decision!;
        for (var i = 0; i < 1001; i++) node.Log.Add("192.0.2.9", "manual", TimeSpan.FromSeconds(1));
        Assert.Equal(1000, node.Log.Read().Length);
        node.Clock!.Now = first.ExpiresAt.AddSeconds(1);
        Assert.Null(node.Service.Evaluate(p, node.Rule, window, 999).Decision);
        node.Clock.Now = window.AddSeconds(node.Rule.Period);
        var next = node.Service.Evaluate(p, node.Rule, node.Clock.Now, 105).Decision;
        Assert.NotNull(next);
        Assert.NotEqual(first.DecisionId, next.DecisionId);
    }

    [Fact]
    public void ReleaseRejectsOldDecisionsButRetainsAcceptedHistory()
    {
        using var node = new Node("a");
        var decision = node.Decide();
        var messages = node.Store.ExportMutations().ToArray();
        node.Store.AdvanceCcPolicyGeneration(decision.Policy.PolicyKey);
        foreach (var message in messages) Assert.False(node.Store.Merge(message with { Sequence = long.MaxValue }));
        node.Service.Sweep();
        Assert.Empty(node.Store.GetSnapshots("cc.triggered"));
        Assert.Single(node.Log.Read());
        using var freshProjection = new CcDecisionService(node.Statistics, node.Store, new CcBanLog());
        freshProjection.Sweep();
        Assert.Empty(node.Store.GetSnapshots("cc.triggered"));
    }

    [Fact]
    public async Task GracefulRestartRestoresDecisionAndDoesNotDuplicateLog()
    {
        var directory = Temp();
        AdvancedCcRule rule;
        CcDecision decision;
        using (var first = new Node("a", directory: directory))
        {
            rule = first.Rule;
            await first.Store.StartAsync(default);
            decision = first.Decide();
            first.Service.RecordHit(decision, rule.Name);
            await first.Store.StopAsync(default);
        }
        using var restarted = new Node("a", rule, directory: directory);
        restarted.Service.Sweep();
        var log = Assert.Single(restarted.Log.Read());
        Assert.Equal(decision.DecisionId, log.DecisionId);
        Assert.Equal(1, log.Nodes!["a"].InterceptedRequests);
        Assert.False(restarted.Service.Evaluate(restarted.Register(), rule, decision.TriggerWindow, 105).Created);
    }

    [Fact]
    public void ReplayedHitEvidenceIsNotSummedTwice()
    {
        using var a = new Node("a"); using var b = new Node("b", a.Rule);
        var d = a.Decide();
        a.Service.RecordHit(d, a.Rule.Name);
        a.Service.RecordHit(d, a.Rule.Name);
        var messages = a.Store.ExportMutations().ToArray();
        foreach (var m in messages.Concat(messages)) b.Store.Merge(m);
        b.Service.Sweep();
        Assert.Equal(2, Assert.Single(b.Log.Read()).Nodes!["a"].InterceptedRequests);
        b.Service.RecordHit(d, b.Rule.Name);
        var result = Assert.Single(b.Log.Read());
        Assert.Equal(2, result.Nodes!["a"].InterceptedRequests);
        Assert.Equal(1, result.Nodes["b"].InterceptedRequests);
    }

    [Fact]
    public void DelayedSameWindowDecisionCannotRenewExpiredCanonicalPenalty()
    {
        var start = CcDecisionService.Window(DateTimeOffset.UtcNow, 3600).AddSeconds(1);
        using var a = new Node("a", clock: new Clock(start));
        using var b = new Node("b", a.Rule, new Clock(start.AddSeconds(5)));
        var p = a.Register();
        var window = CcDecisionService.Window(start, a.Rule.Period);
        var first = a.Service.Evaluate(p, a.Rule, window, 105).Decision!;
        var late = b.Service.Evaluate(b.Register(), b.Rule, window, 105).Decision!;
        a.Clock!.Now = first.ExpiresAt.AddSeconds(1);
        foreach (var message in b.Store.ExportMutations()) a.Store.Merge(message);
        a.Store.Set("cc.triggered", p.StateKey, late.ExpiresAt, TimeSpan.FromHours(1));
        a.Service.Sweep();
        Assert.False(a.Store.TryGet<DateTimeOffset>("cc.triggered", p.StateKey, out _));
        Assert.Null(a.Service.Evaluate(p, a.Rule, window, 105).Decision);
        Assert.Single(a.Log.Read());
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("expiry")]
    [InlineData("scope")]
    public void InvalidReceivedDecisionCannotCreatePenaltyOrLog(string fault)
    {
        using var source = new Node("a"); using var target = new Node("b", source.Rule);
        var decision = source.Decide();
        var mutation = Assert.Single(source.Store.ExportMutations(), m => m.Domain == CcDecisionService.DecisionDomain);
        if (fault == "origin") mutation = mutation with { NodeId = "b" };
        else
        {
            var invalid = fault == "expiry" ? decision with { ExpiresAt = decision.ExpiresAt.AddHours(1) }
                : decision with { Policy = decision.Policy with { ProtectionScope = "host:other.example" } };
            mutation = mutation with { Value = System.Text.Json.JsonSerializer.SerializeToElement(invalid,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) };
        }
        target.Store.Merge(mutation);
        target.Service.Sweep();
        Assert.Empty(target.Log.Read());
        Assert.Empty(target.Store.GetSnapshots("cc.triggered"));
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "cc-decision-" + Guid.NewGuid().ToString("N"));
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Node : IDisposable
    {
        public AdvancedCcRule Rule;
        public RuntimeStateStore Store;
        public CcBanLog Log = new();
        public CcDecisionService Service;
        public IStatisticService Statistics;
        public Clock? Clock;
        public Node(string id, AdvancedCcRule? rule = null, Clock? clock = null, string? directory = null)
        {
            Rule = rule ?? new() { Id = Guid.NewGuid().ToString("N"), Name = "decision-test", Threshold = 100, Period = 3600, ActionSeconds = 10, Action = CcAction.Block };
            Store = new(new StaticOptionsMonitor<RuntimeStateOptions>(CcReleaseTestKeys.Configure(new() { NodeId = id, StateDirectory = directory ?? Temp() })));
            Statistics = DispatchProxy.Create<IStatisticService, Rules>();
            ((Rules)(object)Statistics).Rule = Rule;
            Clock = clock;
            Service = new(Statistics, Store, Log, clock);
        }
        public CcPolicyDescriptor Register() => Service.Register(Rule, "host:a.example", "192.0.2.55");
        public CcDecision Decide()
        {
            var p = Register();
            var window = CcDecisionService.Window(DateTimeOffset.UtcNow, Rule.Period);
            return Service.Evaluate(p, Rule, window, 105).Decision!;
        }
        public void Dispose() { Service.Dispose(); Store.Dispose(); }
    }
    public class Rules : DispatchProxy
    {
        public AdvancedCcRule Rule = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        { nameof(IStatisticService.GetAdvancedCcRules) => new List<AdvancedCcRule> { Rule }, _ => throw new NotSupportedException() };
    }
}
