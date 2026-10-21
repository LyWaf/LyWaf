using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LyWaf.Services.RuntimeState;

namespace LyWaf.Services.Statistic;

public sealed record CcPolicyDescriptor(string RuleId, string RuleRevision, string ProtectionScope, string Subject,
    string PolicyKey, CcPolicyGeneration PolicyGeneration)
{
    [JsonIgnore] public string StateKey => PolicyGeneration.StateKey(PolicyKey);
}

public sealed record CcDecision(CcPolicyDescriptor Policy, string DecisionId, DateTimeOffset TriggerWindow,
    string OriginNode, long ObservedCount, CcAction Action, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    long InterceptedRequests = 0, DateTimeOffset? FirstHit = null, DateTimeOffset? LastHit = null)
{
    [JsonIgnore] public string EvidenceKey => $"{Policy.StateKey}|{TriggerWindow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}:{Convert.ToHexString(Encoding.UTF8.GetBytes(OriginNode))}";
}

/// <summary>Local and background CC decisions share one evaluator. Replication remains owned by RuntimeStateReplicator.</summary>
public sealed partial class CcDecisionService : BackgroundService
{
    public const string PolicyDomain = "cc.policy";
    public const string DecisionDomain = "cc.decision";
    private readonly IStatisticService _statistics;
    private readonly IRuntimeStateStore _store;
    private readonly CcBanLog _log;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly string _node;
    private readonly CcCohortState? _cohort;
    private long _evidenceRevision;
    private readonly Dictionary<string, (long Revision, CcDecision[] Evidence)> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Id, DateTimeOffset Until)> _applied = new(StringComparer.Ordinal);

    public CcDecisionService(IStatisticService statistics, IRuntimeStateStore store, CcBanLog log, TimeProvider? clock = null, CcCohortState? cohort = null)
    {
        _statistics = statistics; _store = store; _log = log; _clock = clock ?? TimeProvider.System; _node = store.GetStatus().NodeId; _cohort = cohort;
        if (store is RuntimeStateStore concrete) { concrete.StateChanged += Changed; concrete.MutationPublished += Published; }
        if (_cohort is not null) _cohort.EvidenceChanged += CohortChanged;
    }

    public static DateTimeOffset Window(DateTimeOffset now, int period)
    {
        period = Math.Max(1, period);
        var seconds = now.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(seconds - seconds % period);
    }

    public static string DecisionId(CcPolicyDescriptor policy, DateTimeOffset window)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object[]
        { "lywaf.cc.decision.v1", policy.PolicyKey, policy.RuleRevision, policy.PolicyGeneration.Version,
            policy.PolicyGeneration.ReleaseId, window.ToUnixTimeMilliseconds() })));

    public CcPolicyDescriptor Register(AdvancedCcRule rule, string scope, string ip)
    {
        scope = CcRuleStateKey.ResolveScope(rule, scope, _node);
        var key = CcRuleStateKey.Create(rule, ip, scope);
        var policy = new CcPolicyDescriptor(rule.Id, CcRuleStateKey.Revision(rule), scope, ip, key, _store.GetCcPolicyGeneration(key));
        _cohort?.Track(policy, rule);
        if (!_store.TryGet<CcPolicyDescriptor>(PolicyDomain, policy.StateKey, out var existing) || existing != policy)
            _store.Set(PolicyDomain, policy.StateKey, policy, TimeSpan.FromSeconds(Math.Max(1, rule.Period) + (double)Math.Max(1, rule.ActionSeconds) + 60));
        return policy;
    }

    public (CcDecision? Decision, bool Created) Evaluate(CcPolicyDescriptor policy, AdvancedCcRule rule,
        DateTimeOffset window, long observed)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (!ValidPolicy(policy, rule) || !Current(policy) || window != Window(now, rule.Period)) return (null, false);
            var revision = Volatile.Read(ref _evidenceRevision);
            if (!_cache.TryGetValue(policy.StateKey, out var cached) || cached.Revision != revision || _store is not RuntimeStateStore)
            {
                cached = (revision, ReadEvidence(rule, policy.StateKey).ToArray());
                if (_cache.Count >= 4096) _cache.Clear();
                _cache[policy.StateKey] = cached;
            }
            var evidence = cached.Evidence;
            var active = Canonical(evidence).Where(d => d.ExpiresAt > now).OrderBy(d => d.CreatedAt).FirstOrDefault();
            if (active is not null && LocallyApplied(active)) return (active, false);
            if (rule.RequireCompleteCoverage)
            {
                if (rule.Scope != CcScope.Node && _cohort is not null)
                {
                    var snapshot = _cohort.Read(policy, rule);
                    if (snapshot.Coverage.Status != "Complete") return (null, false);
                    observed = snapshot.Count;
                }
                else if (Coverage(rule, policy).Status != "Complete") return (null, false);
            }
            if (active is not null) { Apply(active); return (active, false); }
            if (evidence.Length != 0) ClearExpiredProjection(policy);
            var id = DecisionId(policy, window);
            // A consumed trigger window never renews its punishment after expiry, even after UI eviction or restart.
            if (observed < rule.Threshold || evidence.Any(d => d.DecisionId == id)) return (null, false);
            var decision = new CcDecision(policy, id, window, _node, observed, rule.Action, now,
                now.AddSeconds(Math.Max(1, rule.ActionSeconds)));
            Save(decision, rule);
            Apply(decision);
            _log.UpsertDecision(decision, rule.Name);
            return (decision, true);
        }
    }

    public void RecordHit(CcDecision decision, string ruleName)
    {
        lock (_gate)
        {
            if (!Current(decision.Policy) || decision.ExpiresAt <= _clock.GetUtcNow()) return;
            var own = decision with { OriginNode = _node, InterceptedRequests = 0, FirstHit = null, LastHit = null };
            if (_store.TryGet<CcDecision>(DecisionDomain, own.EvidenceKey, out var previous) && previous is not null)
                own = previous;
            var now = _clock.GetUtcNow();
            own = own with { InterceptedRequests = checked(own.InterceptedRequests + 1), FirstHit = own.FirstHit ?? now,
                LastHit = now, ExpiresAt = decision.ExpiresAt, CreatedAt = decision.CreatedAt };
            var rule = _statistics.GetAdvancedCcRules().FirstOrDefault(r => r.Id == decision.Policy.RuleId);
            if (rule is null || !ValidPolicy(own.Policy, rule)) return;
            Save(own, rule);
            _log.UpsertDecision(own, ruleName);
        }
    }

    public void Sweep()
    {
        lock (_gate)
        {
            var rules = _statistics.GetAdvancedCcRules().Where(r => r.Enabled).ToArray();
            foreach (var rule in rules)
            {
                var evidence = ReadEvidence(rule).ToArray();
                foreach (var item in evidence) _log.UpsertDecision(item, rule.Name);
                foreach (var group in Canonical(evidence).Where(d => Current(d.Policy)).GroupBy(d => d.Policy.StateKey))
                {
                    var active = group.Where(d => d.ExpiresAt > _clock.GetUtcNow()).OrderBy(d => d.CreatedAt).FirstOrDefault();
                    if (active is not null) Apply(active);
                    else ClearExpiredProjection(group.First().Policy);
                }
                foreach (var (key, policy) in _store.GetValues<CcPolicyDescriptor>(PolicyDomain))
                {
                    if (policy is null || !ValidPolicy(policy, rule) || key != policy.StateKey || !Current(policy)) continue;
                    _cohort?.Track(policy, rule);
                    var window = Window(_clock.GetUtcNow(), rule.Period);
                    var domain = rule.Type switch { CcRuleType.FrequentAttack => "cc.attack", CcRuleType.FrequentError => "cc.error", _ => "cc.access" };
                    Evaluate(policy, rule, window, _store.GetWindowValue(domain, key, window));
                }
            }
        }
    }

    private IEnumerable<CcDecision> ReadEvidence(AdvancedCcRule rule, string? stateKey = null)
    {
        foreach (var (key, source, decision) in ReadRecords(stateKey))
        {
            if (decision?.Policy is not { } policy || !ValidPolicy(policy, rule) || decision.OriginNode is not { Length: > 0 and <= 128 }
                || source != decision.OriginNode || key != decision.EvidenceKey
                || decision.DecisionId != DecisionId(policy, decision.TriggerWindow) || decision.Action != rule.Action
                || decision.ObservedCount < rule.Threshold || decision.InterceptedRequests < 0
                || decision.TriggerWindow != Window(decision.TriggerWindow, rule.Period)
                || decision.CreatedAt > _clock.GetUtcNow() || decision.CreatedAt < decision.TriggerWindow
                || (decision.CreatedAt - decision.TriggerWindow).TotalSeconds >= Math.Max(1, rule.Period)
                || decision.CreatedAt > DateTimeOffset.MaxValue.AddSeconds(-Math.Max(1, rule.ActionSeconds))
                || decision.ExpiresAt != decision.CreatedAt.AddSeconds(Math.Max(1, rule.ActionSeconds))) continue;
            yield return decision;
        }
    }

    private IEnumerable<(string Key, string Source, CcDecision? Decision)> ReadRecords(string? stateKey)
    {
        if (_store is RuntimeStateStore concrete)
        {
            foreach (var record in concrete.ReadCcDecisionHistory(stateKey))
            {
                CcDecision? value;
                try { value = record.Value!.Value.Deserialize<CcDecision>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                catch (JsonException) { continue; }
                yield return (record.Key, record.NodeId, value);
            }
        }
        else
        {
            var sources = _store.GetSnapshots(DecisionDomain).ToDictionary(s => s.Key, s => s.NodeId, StringComparer.Ordinal);
            foreach (var (key, value) in _store.GetValues<CcDecision>(DecisionDomain))
                if ((stateKey is null || key.StartsWith(stateKey + "|", StringComparison.Ordinal))
                    && sources.TryGetValue(key, out var source)) yield return (key, source, value);
        }
    }

    private static IEnumerable<CcDecision> Canonical(IEnumerable<CcDecision> evidence)
        => evidence.GroupBy(d => d.DecisionId).Select(g => g.OrderBy(d => d.CreatedAt).ThenBy(d => d.OriginNode, StringComparer.Ordinal).First()
            with { ObservedCount = g.Max(d => d.ObservedCount) });

    private bool Current(CcPolicyDescriptor policy) => _store.GetCcPolicyGeneration(policy.PolicyKey) == policy.PolicyGeneration;

    private bool ValidPolicy(CcPolicyDescriptor policy, AdvancedCcRule rule)
        => rule.Enabled && CcRequestIdentity.ValidConfiguration(rule) && policy.RuleId == rule.Id && policy.PolicyGeneration is not null
            && policy.ProtectionScope is { Length: > 0 and <= 262 } && policy.Subject is { Length: > 0 and <= 80 }
            && CcRequestIdentity.ValidSubject(rule, policy.Subject)
            && policy.PolicyGeneration.Version >= 0
            && (policy.PolicyGeneration.Version == 0 ? policy.PolicyGeneration.ReleaseId == "" : Guid.TryParseExact(policy.PolicyGeneration.ReleaseId, "N", out _))
            && (rule.Scope == CcScope.Node ? policy.ProtectionScope == "node:" + _node
                : rule.Scope == CcScope.Cluster ? policy.ProtectionScope == "cluster:shared"
                : string.IsNullOrWhiteSpace(rule.ProtectionGroup)
                ? policy.ProtectionScope.StartsWith("host:", StringComparison.Ordinal) || policy.ProtectionScope.StartsWith("route:", StringComparison.Ordinal)
                : policy.ProtectionScope == "group:" + rule.ProtectionGroup.Trim())
            && policy.RuleRevision == CcRuleStateKey.Revision(rule)
            && policy.PolicyKey == CcRuleStateKey.Create(rule, policy.Subject, policy.ProtectionScope);

    private void Save(CcDecision decision, AdvancedCcRule rule)
    {
        // Evidence outlives both the punishment and trigger window; UI capacity never evicts this state.
        var until = decision.TriggerWindow.AddSeconds(Math.Max(1, rule.Period)).AddDays(1);
        if (until < decision.ExpiresAt.AddDays(1)) until = decision.ExpiresAt.AddDays(1);
        var revision = Volatile.Read(ref _evidenceRevision);
        _cache.TryGetValue(decision.Policy.StateKey, out var cached);
        _store.Set(DecisionDomain, decision.EvidenceKey, decision, until - _clock.GetUtcNow());
        // Preserve the hot subject's cache after our own write; a concurrent remote write forces a refresh instead.
        if (cached.Evidence is not null && cached.Revision == revision && Volatile.Read(ref _evidenceRevision) == revision + 1)
            _cache[decision.Policy.StateKey] = (revision + 1, cached.Evidence.Where(d => d.OriginNode != decision.OriginNode
                || d.DecisionId != decision.DecisionId).Append(decision).ToArray());
        else _cache.Remove(decision.Policy.StateKey);
    }

    private void Apply(CcDecision decision)
    {
        if (!Current(decision.Policy) || decision.ExpiresAt <= _clock.GetUtcNow()) return;
        if (LocallyApplied(decision)) return;
        var rule = _statistics.GetAdvancedCcRules().FirstOrDefault(r => r.Id == decision.Policy.RuleId);
        if (rule is null || rule.RequireCompleteCoverage && Coverage(rule, decision.Policy).Status != "Complete") return;
        if (!_store.TryGet<DateTimeOffset>("cc.triggered", decision.Policy.StateKey, out var until) || until != decision.ExpiresAt)
            _store.Set("cc.triggered", decision.Policy.StateKey, decision.ExpiresAt, decision.ExpiresAt - _clock.GetUtcNow());
        if (_applied.Count >= 4096)
            foreach (var key in _applied.Where(p => p.Value.Until <= _clock.GetUtcNow()).Select(p => p.Key).ToArray()) _applied.Remove(key);
        if (_applied.Count < 4096 || _applied.ContainsKey(decision.Policy.StateKey))
            _applied[decision.Policy.StateKey] = (decision.DecisionId, decision.ExpiresAt);
    }

    private bool LocallyApplied(CcDecision decision) => _applied.TryGetValue(decision.Policy.StateKey, out var applied)
        && applied.Id == decision.DecisionId && applied.Until == decision.ExpiresAt && decision.ExpiresAt > _clock.GetUtcNow()
        && _store.TryGet<DateTimeOffset>("cc.triggered", decision.Policy.StateKey, out var until) && until == decision.ExpiresAt;

    private void ClearExpiredProjection(CcPolicyDescriptor policy)
    {
        // A delayed replica of the same logical decision must not renew an already expired punishment.
        if (Current(policy) && _store.TryGet<DateTimeOffset>("cc.triggered", policy.StateKey, out _))
            _store.Remove("cc.triggered", policy.StateKey);
    }

    private void Changed(string domain)
    {
        if (domain is DecisionDomain or CcPolicyGeneration.Domain) Interlocked.Increment(ref _evidenceRevision);
        if (domain is not ("cc.access" or "cc.attack" or "cc.error" or PolicyDomain or DecisionDomain or CcPolicyGeneration.Domain)) return;
        try { _changed.Release(); } catch (SemaphoreFullException) { }
    }
    private void Published(RuntimeStateMutation mutation)
    {
        // Counter increments publish without StateChanged; value writes already raise it once.
        if (mutation.Domain is "cc.access" or "cc.attack" or "cc.error") Changed(mutation.Domain);
    }
    private void CohortChanged() => Changed("cc.access");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var lastError = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                try { Sweep(); }
                catch (Exception ex)
                {
                    if (_clock.GetUtcNow() - lastError > TimeSpan.FromMinutes(1))
                    { NLog.LogManager.GetCurrentClassLogger().Warn("CC background decision failed: {ErrorType}", ex.GetType().Name); lastError = _clock.GetUtcNow(); }
                }
                await _changed.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
                await Task.Delay(50, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        if (_store is RuntimeStateStore concrete) { concrete.StateChanged -= Changed; concrete.MutationPublished -= Published; }
        if (_cohort is not null) _cohort.EvidenceChanged -= CohortChanged;
        base.Dispose();
    }
}
