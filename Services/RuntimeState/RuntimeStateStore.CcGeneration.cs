using System.Text;
using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

public sealed partial class RuntimeStateStore
{
    private readonly object _ccGenerationGate = new();
    private string CcGenerationPath => Path.Combine(StateDirectory, "cc-policy-generations.jsonl");

    public CcPolicyGeneration GetCcPolicyGeneration(string policyKey)
        => _entries.TryGetValue(Identity(CcPolicyGeneration.Domain, policyKey), out var entry)
            ? ReadGeneration(entry) : CcPolicyGeneration.Initial;

    public CcPolicyGeneration AdvanceCcPolicyGeneration(string policyKey, CcPolicyGeneration? expected = null)
        => AdvanceCcGeneration(policyKey, expected, Guid.NewGuid().ToString("N"));

    private CcPolicyGeneration AdvanceCcGeneration(string policyKey, CcPolicyGeneration? expected, string releaseId)
    {
        ValidateIdentity(CcPolicyGeneration.Domain, policyKey);
        lock (_ccGenerationGate)
        {
            var current = GetCcPolicyGeneration(policyKey);
            if (expected is not null && expected != current) return current;
            if (_options.CurrentValue.Enabled && string.IsNullOrWhiteSpace(_options.CurrentValue.CcReleasePrivateKeyPem))
                throw new InvalidOperationException("Replicated CC release requires an authorized signing key.");
            var next = new CcPolicyGeneration(checked(current.Version + 1), releaseId);
            var mutation = new RuntimeStateMutation(CcPolicyGeneration.Domain, policyKey, NodeId, NextSequence(),
                DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(next, JsonOptions));
            ValidateMutation(mutation);
            mutation = CcReleaseAuthorization.Sign(mutation, _options.CurrentValue);
            MergeCcGeneration(mutation, replicate: true);
            return next;
        }
    }

    public RuntimeStateMutation IssueCcCaptchaRelease(string policyKey, CcPolicyGeneration expected, string releaseId)
    {
        if (!Guid.TryParseExact(releaseId, "N", out _) || expected.Version < 0 || expected.Version == long.MaxValue)
            throw new ArgumentException("Invalid captcha release generation.");
        lock (_ccGenerationGate)
        {
            var current = GetCcPolicyGeneration(policyKey);
            var requested = new CcPolicyGeneration(expected.Version + 1, releaseId);
            if (current != expected && current != requested)
                throw new CcCaptchaGenerationConflictException();
            if (current == expected) AdvanceCcGeneration(policyKey, expected, releaseId);
            return _entries[Identity(CcPolicyGeneration.Domain, policyKey)];
        }
    }

    private bool MergeCcGeneration(RuntimeStateMutation mutation, bool replicate)
    {
        lock (_ccGenerationGate)
        {
            if (ReadGeneration(mutation).CompareTo(GetCcPolicyGeneration(mutation.Key)) <= 0) return false;
            // Releases must survive queue saturation and restart. Fail closed on storage errors.
            PersistCcGenerationWithinBudget(mutation);
            _entries[Identity(mutation.Domain, mutation.Key)] = mutation;
            InterlockedExtensions.Max(ref _sequence, mutation.Sequence);
            if (replicate) MutationPublished?.Invoke(mutation);
            StateChanged?.Invoke(mutation.Domain);
            return true;
        }
    }

    private void RecoverCcGenerations()
    {
        if (!File.Exists(CcGenerationPath)) return;
        // Do not silently discard fences on corruption or the ordinary journal's recovery-size limit.
        foreach (var line in File.ReadLines(CcGenerationPath, Encoding.UTF8))
        {
            var mutation = JsonSerializer.Deserialize<RuntimeStateMutation>(line, JsonOptions)
                ?? throw new InvalidOperationException("Invalid CC policy generation journal.");
            if (mutation.Domain != CcPolicyGeneration.Domain)
                throw new InvalidOperationException("Unexpected domain in CC policy generation journal.");
            MergeRecovered(mutation);
        }
    }

    private static CcPolicyGeneration ReadGeneration(RuntimeStateMutation mutation)
        => mutation.Value!.Value.Deserialize<CcPolicyGeneration>(JsonOptions)!;

    private static void ValidateGenerationMutation(RuntimeStateMutation mutation)
    {
        if (mutation.Deleted || mutation.ExpiresAt is not null || mutation.CounterComponents is not null
            || mutation.Value is null || !CcPolicyGeneration.TryParseKey(mutation.Key, out var policyKey, out var keyGeneration)
            || policyKey != mutation.Key || keyGeneration != CcPolicyGeneration.Initial)
            throw new InvalidOperationException("CC policy generation must be a permanent scoped release fence.");
        CcPolicyGeneration? generation;
        try { generation = mutation.Value.Value.Deserialize<CcPolicyGeneration>(JsonOptions); }
        catch (JsonException ex) { throw new InvalidOperationException("Invalid CC policy generation.", ex); }
        if (generation is null || generation.Version <= 0 || !Guid.TryParseExact(generation.ReleaseId, "N", out _))
            throw new InvalidOperationException("Invalid CC policy generation.");
    }

    private static void RejectGenericGenerationWrite(string domain)
    {
        if (domain == CcPolicyGeneration.Domain)
            throw new InvalidOperationException("Use AdvanceCcPolicyGeneration to change a CC release fence.");
    }

    private bool IsCurrentCcState(string domain, string key)
        => !CcPolicyGeneration.IsStateDomain(domain)
            || !CcPolicyGeneration.TryParseKey(key, out var policyKey, out var generation)
            || generation == GetCcPolicyGeneration(policyKey);

    private bool IsObsoleteCcState(RuntimeStateMutation mutation)
        => CcPolicyGeneration.IsStateDomain(mutation.Domain)
            && CcPolicyGeneration.TryParseKey(mutation.Key, out var policyKey, out var generation)
            && generation.CompareTo(GetCcPolicyGeneration(policyKey)) < 0;
}

public sealed class CcCaptchaGenerationConflictException : InvalidOperationException;
