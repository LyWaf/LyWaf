using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Statistic;

public sealed record CcWindowEvidence(string NodeId, string BootId, string PolicyKey, CcPolicyGeneration Generation,
    string Revision, bool Enabled, DateTimeOffset ActiveSince, DateTimeOffset WindowStart,
    DateTimeOffset Watermark, long LocalCount, bool LossFree);
public sealed record CcExecutionAggregate(string Status, string[] ExpectedNodes, string[] ConfirmedNodes,
    string[] PendingNodes, IReadOnlyDictionary<string, string> Boots, bool AllNodesConfirmed,
    string ConfirmationKind = "boot-bound-local-projection");

/// <summary>Short-lived authenticated observations, never populated by runtime.merge.</summary>
public sealed class CcCohortState(IRuntimeStateStore store, IOptionsMonitor<RuntimeStateOptions> runtime,
    NodeIdentityService identity, TimeProvider? clock = null, IOptionsMonitor<NodeCommunicationOptions>? communication = null,
    NodeHubMembership? membership = null)
{
    private readonly object _gate = new();
    private static readonly System.Diagnostics.ActivitySource Diagnostics = new("LyWaf.Cc.Query");
    internal event Action? EvidenceChanged;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly string _nodeId = store.GetStatus().NodeId;
    private readonly Dictionary<string, (string Revision, DateTimeOffset Since)> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, string Node), Sample> _samples = new();
    private readonly Dictionary<string, string> _boots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CcPolicyDescriptor Policy, AdvancedCcRule Rule, DateTimeOffset Until)> _tracked = new();
    private DateTimeOffset _nextPrune;
    private sealed record Sample(CcPeerReply Reply, DateTimeOffset Received, string Endpoint, double ClockBoundMs, string? KeyFingerprint);

    public void ObserveRules(IEnumerable<AdvancedCcRule> rules)
    {
        lock (_gate)
        {
            var enabled = rules.Where(r => r.Enabled).ToArray();
            foreach (var id in _revisions.Keys.Except(enabled.Select(r => r.Id)).ToArray()) _revisions.Remove(id);
            foreach (var rule in enabled)
            {
                var revision = CcRuleStateKey.Revision(rule);
                if (!_revisions.TryGetValue(rule.Id, out var old) || old.Revision != revision)
                    _revisions[rule.Id] = (revision, _clock.GetUtcNow());
            }
        }
    }

    public void Track(CcPolicyDescriptor policy, AdvancedCcRule rule)
    {
        if (rule.Scope == CcScope.Node || !rule.UseManagedMembership && rule.ExpectedNodes.Length == 0) return;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (now >= _nextPrune)
            {
                foreach (var key in _tracked.Where(p => p.Value.Until < now).Select(p => p.Key).ToArray())
                { _tracked.Remove(key); foreach (var s in _samples.Keys.Where(s => s.Key == key).ToArray()) _samples.Remove(s); }
                _nextPrune = now.AddSeconds(1);
            }
            if (_tracked.Count >= 256 && !_tracked.ContainsKey(policy.StateKey)) return;
            _tracked[policy.StateKey] = (policy, rule, _clock.GetUtcNow().AddSeconds(Math.Max(rule.Period, rule.ActionSeconds) + 60));
        }
    }

    internal (CcPolicyDescriptor Policy, AdvancedCcRule Rule)[] Tracked()
    {
        lock (_gate) return _tracked.Values.Where(t => t.Until >= _clock.GetUtcNow() && t.Rule.Enabled
            && _revisions.TryGetValue(t.Rule.Id, out var active) && active.Revision == t.Policy.RuleRevision
            && store.GetCcPolicyGeneration(t.Policy.PolicyKey) == t.Policy.PolicyGeneration)
            .Select(t => (t.Policy, t.Rule)).ToArray();
    }

    public CcWindowEvidence Capture(CcPolicyDescriptor policy, AdvancedCcRule rule, DateTimeOffset window)
    {
        var local = identity.Capture();
        var revision = CcRuleStateKey.Revision(rule);
        DateTimeOffset since;
        lock (_gate) since = _revisions.TryGetValue(rule.Id, out var active) && active.Revision == revision
            ? active.Since : _clock.GetUtcNow();
        var watermark = _clock.GetUtcNow();
        var domain = rule.Type switch { CcRuleType.FrequentAttack => "cc.attack", CcRuleType.FrequentError => "cc.error", _ => "cc.access" };
        var components = (store as RuntimeStateStore)?.GetCcWindowComponents(domain, policy.StateKey, window);
        long count = 0;
        foreach (var part in components ?? new Dictionary<string, long>())
        {
            var source = part.Key;
            if (source.StartsWith('['))
            {
                try { source = JsonSerializer.Deserialize<string[]>(source)?[0] ?? ""; }
                catch (JsonException) { continue; }
            }
            if (source == local.NodeId) count = checked(count + part.Value);
        }
        return new(local.NodeId, local.BootId, policy.PolicyKey, store.GetCcPolicyGeneration(policy.PolicyKey), revision,
            rule.Enabled && revision == policy.RuleRevision, since > local.StartedAt ? since : local.StartedAt,
            window, watermark, count, store is RuntimeStateStore concrete && concrete.IsCcObservationLossFree());
    }

    internal void Accept(CcPolicyDescriptor policy, string peer, string endpoint, CcPeerReply reply,
        DateTimeOffset sent, DateTimeOffset received, double elapsedMs)
    {
        var evidence = reply.Evidence;
        var midpoint = sent + TimeSpan.FromTicks((received - sent).Ticks / 2);
        var bound = Math.Abs((evidence.Watermark - midpoint).TotalMilliseconds) + elapsedMs / 2;
        if (reply.ReceivedAt is { } serverReceived && reply.ProcessingMilliseconds is { } processing)
        {
            // Causal offset interval: server-send - client-receive <= offset <= server-receive - client-send.
            // Remote processing is measured, not mistaken for network delay. Neither bound is relaxed.
            var lower = (evidence.Watermark - received).TotalMilliseconds;
            var upper = (serverReceived - sent).TotalMilliseconds;
            var serverWall = (evidence.Watermark - serverReceived).TotalMilliseconds;
            bound = !double.IsFinite(processing) || processing < 0 || processing > elapsedMs + 1
                || Math.Abs(serverWall - processing) > 50 || lower > upper + 1
                ? double.PositiveInfinity : Math.Max(Math.Abs(lower), Math.Abs(upper));
        }
        if (elapsedMs < 0 || Math.Abs((received - sent).TotalMilliseconds - elapsedMs) > 50) bound = double.PositiveInfinity;
        using var observation = Diagnostics.StartActivity("cc.cohort.observation");
        observation?.SetTag("peer", peer);
        observation?.SetTag("clock.bound.ms", bound);
        observation?.SetTag("roundtrip.ms", elapsedMs);
        observation?.SetTag("watermark.age.ms", (received - evidence.Watermark).TotalMilliseconds);
        observation?.SetTag("window.start", evidence.WindowStart.ToUnixTimeMilliseconds());
        observation?.SetTag("received", received.ToUnixTimeMilliseconds());
        string? fingerprint = null;
        if (communication is not null)
        {
            var signer = Signer(peer, reply);
            if (signer is null || !communication.CurrentValue.Peers.TryGetValue(signer, out var auth)
                || !auth.Keys.TryGetValue(reply.KeyId, out var secret) || CcPeerQueryClient.Sign(reply, secret) != reply.Signature)
            { Invalidate(peer); return; }
            fingerprint = Fingerprint(secret);
        }
        else if (reply.ViaController is not null || reply.ControllerBootId is not null) { Invalidate(peer); return; }
        lock (_gate)
        {
            if (_boots.TryGetValue(peer, out var boot) && boot != evidence.BootId)
            {
                foreach (var key in _samples.Keys.Where(k => k.Node == peer).ToArray()) _samples.Remove(key);
                _boots[peer] = evidence.BootId;
                return; // A changed boot must establish fresh coverage on a subsequent probe.
            }
            _boots[peer] = evidence.BootId;
            var keyForSample = (policy.StateKey, peer);
            if (_samples.TryGetValue(keyForSample, out var old) && old.Received > received) return;
            if (_samples.Count >= 8192 && !_samples.ContainsKey(keyForSample)) return;
            _samples[keyForSample] = new(reply, received, endpoint, bound, fingerprint);
        }
        EvidenceChanged?.Invoke();
    }

    internal void Invalidate(string peer)
    { lock (_gate) foreach (var key in _samples.Keys.Where(k => k.Node == peer).ToArray()) _samples.Remove(key); }

    public (CcDecisionCoverage Coverage, long Count) Read(CcPolicyDescriptor policy, AdvancedCcRule rule)
    {
        var now = _clock.GetUtcNow();
        var window = CcDecisionService.Window(now, rule.Period);
        var expected = ExpectedNodes(rule);
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        DateTimeOffset? watermark = null;
        long count = 0;
        if (expected.Length == 0 || !expected.Contains(_nodeId)) reasons.Add("membership-unknown");
        lock (_gate)
        {
            foreach (var node in expected)
            {
                CcWindowEvidence? evidence = null;
                if (node == _nodeId) evidence = Capture(policy, rule, window);
                else if (Fresh(policy.StateKey, node, rule.Period, out var sample)) evidence = sample!.Reply.Evidence;
                if (evidence is null) { missing.Add(node); reasons.Add("source-unavailable-or-clock-bound"); continue; }
                if (!evidence.Enabled || evidence.Revision != policy.RuleRevision || evidence.Generation != policy.PolicyGeneration)
                { missing.Add(node); reasons.Add("revision-activation-unconfirmed"); continue; }
                if (evidence.WindowStart != window || evidence.ActiveSince > window || !evidence.LossFree)
                { missing.Add(node); reasons.Add(evidence.LossFree ? "window-incomplete" : "runtime-loss-or-capacity"); continue; }
                count = checked(count + evidence.LocalCount);
                watermark = watermark is null || evidence.Watermark < watermark ? evidence.Watermark : watermark;
            }
        }
        return (new(reasons.Count == 0 ? "Complete" : reasons.Contains("runtime-loss-or-capacity") ? "Degraded" : "Partial",
            expected, missing.ToArray(), reasons.ToArray(), Watermark: watermark), count);
    }

    private bool Fresh(string key, string peer, int period, out Sample? sample)
    {
        sample = null;
        var config = runtime.CurrentValue;
        if (!_samples.TryGetValue((key, peer), out var found)) return false;
        var signer = Signer(peer, found.Reply);
        if (!config.Enabled || signer is null || !config.Peers.TryGetValue(signer, out var target) || !target.Enabled || target.ProtocolVersion != 2
            || found.Endpoint != target.Endpoint) return false;
        var operation = found.Reply.ViaController is null ? CcExecutionQueryService.Operation : CcExecutionRelayService.Operation;
        if (communication is not null && (!communication.CurrentValue.Enabled
            || !communication.CurrentValue.Peers.TryGetValue(signer, out var auth) || !auth.Enabled
            || !NodeAdmissionPolicy.Allows(auth, operation)
            || !PluginNodeRequests.Allows(auth, operation)
            || !auth.Keys.TryGetValue(found.Reply.KeyId, out var secret) || Fingerprint(secret) != found.KeyFingerprint)) return false;
        var tolerance = Math.Min(500, Math.Max(1, period) * 250d);
        var age = (_clock.GetUtcNow() - found.Received).TotalMilliseconds;
        if (age < 0 || age > tolerance || found.ClockBoundMs > tolerance) return false;
        sample = found;
        return true;
    }

    private string? Signer(string source, CcPeerReply reply)
    {
        var config = communication?.CurrentValue;
        if (reply.ViaController is null)
            return reply.ControllerBootId is null && !(config?.Mode == "Node" && source != config.ControllerNodeId) ? source : null;
        var hub = reply.ViaController;
        return config is { Enabled: true, Mode: "Node", Role: "Node" } && config.ControllerNodeId == hub
            && source != hub && reply.Evidence.NodeId == source && reply.Execution.NodeId == source
            && reply.ReceivedAt is null && reply.ProcessingMilliseconds is null
            && config.Peers.TryGetValue(hub, out var peer) && peer.Role == "Controller"
            && membership is not null && membership.IsKnown(config.ClusterId, hub, source)
            && reply.ControllerBootId is not null && reply.ControllerBootId == membership.BootId(config.ClusterId, hub) ? hub : null;
    }

    // Proofs are immutable and verified once on admission; every read still checks current key and permission.
    private static string Fingerprint(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public CcExecutionAggregate Execution(CcPolicyDescriptor policy, AdvancedCcRule rule, CcExecutionState local)
    {
        var confirmed = new List<string>(); var boots = new Dictionary<string, string>();
        var expected = rule.Scope == CcScope.Node ? new[] { local.NodeId } : ExpectedNodes(rule);
        lock (_gate)
        {
            foreach (var node in expected)
            {
                var execution = node == local.NodeId ? local : Fresh(policy.StateKey, node, rule.Period, out var sample)
                    ? sample!.Reply.Execution : null;
                var boot = node == local.NodeId ? identity.Capture().BootId : _boots.GetValueOrDefault(node);
                if (boot is not null) boots[node] = boot;
                if (execution is { Status: "Applied" } && execution.DecisionId == local.DecisionId
                    && execution.Generation == policy.PolicyGeneration && execution.ExpiresAt > _clock.GetUtcNow()) confirmed.Add(node);
            }
        }
        var pending = expected.Except(confirmed).ToArray();
        var all = expected.Length > 0 && expected.Contains(local.NodeId) && pending.Length == 0;
        return new(all ? "confirmed" : "pending", expected, confirmed.ToArray(), pending, boots, all);
    }

    public string[] ExpectedNodes(AdvancedCcRule rule)
    {
        if (!rule.UseManagedMembership) return rule.ExpectedNodes.Order(StringComparer.Ordinal).ToArray();
        var config = communication?.CurrentValue;
        if (config is null || !config.Enabled || !runtime.CurrentValue.Enabled || config.NodeId != _nodeId) return [];
        var nodes = config.Mode == "Controller" && config.Role == "Controller" ? NodeHubMembership.ConfiguredCcNodes(config)
            : membership?.ExpectedCcNodes(config)?.ToArray();
        // Never truncate a topology: omitted members must not turn partial coverage into Complete.
        return nodes is { Length: > 0 and <= 1025 } ? nodes : [];
    }

    public CcExecutionAggregate Release(CcPolicyDescriptor policy, AdvancedCcRule rule)
    {
        var local = identity.Capture();
        var expected = rule.Scope == CcScope.Node ? new[] { local.NodeId } : ExpectedNodes(rule);
        var confirmed = new List<string>(); var boots = new Dictionary<string, string>();
        lock (_gate)
        {
            foreach (var node in expected)
            {
                var generation = node == local.NodeId ? store.GetCcPolicyGeneration(policy.PolicyKey)
                    : Fresh(policy.StateKey, node, rule.Period, out var sample) ? sample!.Reply.Evidence.Generation : null;
                var boot = node == local.NodeId ? local.BootId : _boots.GetValueOrDefault(node);
                if (boot is not null) boots[node] = boot;
                if (policy.PolicyGeneration.Version > 0 && generation == policy.PolicyGeneration) confirmed.Add(node);
            }
        }
        var pending = expected.Except(confirmed).ToArray();
        var all = expected.Length > 0 && expected.Contains(local.NodeId) && pending.Length == 0;
        return new(all ? "confirmed" : "pending", expected, confirmed.ToArray(), pending, boots, all, "boot-bound-release-fence");
    }
}
