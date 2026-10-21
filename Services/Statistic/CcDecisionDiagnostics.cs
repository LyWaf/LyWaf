using LyWaf.Services.RuntimeState;

namespace LyWaf.Services.Statistic;

public sealed record CcDecisionCoverage(string Status, string[] ExpectedNodes, string[] MissingNodes,
    string[] Reasons, string RevisionPolicy = "retire-previous", string CountSemantics = "observed-lower-bound",
    DateTimeOffset? Watermark = null);

public sealed record CcExecutionState(string NodeId, string DecisionId, string Status, DateTimeOffset ObservedAt,
    DateTimeOffset? ExpiresAt, CcPolicyGeneration Generation, string ConfirmationKind = "local-projection",
    bool AllNodesConfirmed = false);

public sealed partial class CcDecisionService
{
    public CcDecisionCoverage Coverage(AdvancedCcRule rule, CcPolicyDescriptor? policy = null)
    {
        if (rule.Scope != CcScope.Node && policy is not null && _cohort is not null)
            return _cohort.Read(policy, rule).Coverage;
        var state = _store.GetStatus();
        var expected = rule.Scope == CcScope.Node ? new[] { _node } : _cohort?.ExpectedNodes(rule)
            ?? (rule.UseManagedMembership ? [] : (rule.ExpectedNodes ?? []).Order(StringComparer.Ordinal).ToArray());
        var reasons = new List<string>();
        if (!CcRequestIdentity.ValidConfiguration(rule)) reasons.Add("invalid-policy");
        if (state.CapacityError is not null || state.DroppedMutations > 0) reasons.Add("runtime-loss-or-capacity");
        // A merged PN counter has no per-source zero watermark, rollout acknowledgement or clock bound.
        // Do not infer any of these from a received contribution (including a contribution of zero).
        if (rule.Scope != CcScope.Node)
        {
            if (expected.Length == 0) reasons.Add("membership-unknown");
            reasons.Add("revision-activation-unconfirmed");
            reasons.Add("window-watermarks-unavailable");
            reasons.Add("window-clock-bound-unavailable");
        }
        return new(reasons.Count == 0 ? "Complete" : reasons.Contains("runtime-loss-or-capacity") || reasons.Contains("invalid-policy")
            ? "Degraded" : "Partial", expected, rule.Scope == CcScope.Node ? [] : expected, reasons.ToArray());
    }

    public CcExecutionState InspectExecution(CcPolicyDescriptor policy, string decisionId)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var generation = _store.GetCcPolicyGeneration(policy.PolicyKey);
            CcExecutionState Result(string status, DateTimeOffset? expiry = null)
                => new(_node, decisionId, status, now, expiry, generation);
            var rule = _statistics.GetAdvancedCcRules().FirstOrDefault(r => r.Id == policy.RuleId);
            if (rule is null || !ValidPolicy(policy, rule)) return Result("RevisionInactive");
            if (generation != policy.PolicyGeneration) return Result("Released");
            var decision = Canonical(ReadEvidence(rule, policy.StateKey)).FirstOrDefault(d => d.DecisionId == decisionId);
            if (decision is not null && LocallyApplied(decision)) return Result("Applied", decision.ExpiresAt);
            if (rule.RequireCompleteCoverage && Coverage(rule, policy).Status != "Complete") return Result("Degraded");
            if (decision is null) return Result("Unknown");
            if (decision.ExpiresAt <= now) return Result("Expired", decision.ExpiresAt);
            if (!_store.TryGet<DateTimeOffset>("cc.triggered", policy.StateKey, out var until) || until != decision.ExpiresAt)
                return Result("Pending", decision.ExpiresAt);
            return Result("Applied", decision.ExpiresAt);
        }
    }

    public CcExecutionAggregate? InspectClusterExecution(CcPolicyDescriptor policy, string decisionId)
    {
        var rule = _statistics.GetAdvancedCcRules().FirstOrDefault(r => r.Id == policy.RuleId);
        return rule is null ? null : _cohort?.Execution(policy, rule, InspectExecution(policy, decisionId));
    }

    public CcExecutionAggregate? InspectRelease(AdvancedCcRule rule, string scope, string subject, CcPolicyGeneration generation)
    {
        var policy = new CcPolicyDescriptor(rule.Id, CcRuleStateKey.Revision(rule), scope, subject,
            CcRuleStateKey.Create(rule, subject, scope), generation);
        _cohort?.Track(policy, rule);
        return _cohort?.Release(policy, rule);
    }
}
