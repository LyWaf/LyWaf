using System.Collections.Concurrent;
using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

public sealed partial class RuntimeStateStore
{
    private const string BootstrapSnapshotDomain = "runtime.sync.snapshot";
    private const string BootstrapFenceDomain = "runtime.sync.bootstrap-fence";
    private readonly ConcurrentDictionary<string, BootstrapFence> _bootstrapFences = new(StringComparer.Ordinal);
    private sealed record BootstrapFence(string Authority, string RequestId, string Digest);
    internal Action? BootstrapBeforePublish { get; set; }

    internal bool IsCurrentForSync(RuntimeStateMutation mutation)
    {
        if (_bootstrapFences.IsEmpty) return true;
        return _entries.TryGetValue(Identity(mutation.Domain, mutation.Key), out var current)
            && RuntimeStateBootstrapImage.Hash(current) == RuntimeStateBootstrapImage.Hash(mutation);
    }

    internal RuntimeStateBootstrapImage CaptureBootstrap(string cluster, string recipient, string requestId,
        string oldEpoch, RuntimeStateBootstrapScope scope)
    {
        lock (_ccGenerationGate)
        {
            CleanupExpiredCore();
            var items = _entries.Values.Where(scope.Contains).ToArray();
            if (items.Any(m => RuntimeStateReplicationAuthorization.AuthorizeSend(_options.CurrentValue, recipient, m) is not null))
                throw new UnauthorizedAccessException("Bootstrap scope contains unauthorized sources");
            var image = new RuntimeStateBootstrapImage(cluster, NodeId, recipient, requestId, oldEpoch,
                Guid.NewGuid().ToString("N"), scope, items, DateTimeOffset.UtcNow, "");
            image = image with { Digest = image.ComputeDigest() };
            image.Validate();
            return image;
        }
    }

    internal Task InstallBootstrapAsync(RuntimeStateBootstrapImage image, CancellationToken token) => Task.Run(() =>
    {
        image.Validate();
        lock (_ccGenerationGate)
        {
            var options = _options.CurrentValue;
            if (image.RecipientNodeId != NodeId || !options.Peers.TryGetValue(image.AuthorityNodeId, out var peer)
                || !peer.Enabled || !peer.BootstrapAuthority
                || RuntimeStateBootstrapScope.From(options, peer.Receive).Digest() != image.Scope.Digest()
                || RuntimeStateReplicationAuthorization.AuthorizeReceive(options, image.AuthorityNodeId, image.Items, true) is not null)
                throw new UnauthorizedAccessException("Bootstrap authority or scope is not granted");
            if (_bootstrapFences.TryGetValue(image.AuthorityNodeId, out var installed) && installed.RequestId == image.RequestId)
            {
                if (installed.Digest != image.Digest) throw new InvalidDataException("Bootstrap retry content conflict");
                return;
            }
            foreach (var mutation in image.Items)
            {
                ValidateMutation(mutation);
                if (mutation.Domain == CcPolicyGeneration.Domain) CcReleaseAuthorization.Verify(mutation, options);
            }
            var prospective = new Dictionary<string, RuntimeStateMutation>(_entries, StringComparer.Ordinal);
            ApplyBootstrapEntries(prospective, image);
            CheckSnapshotCapacity(prospective, image, commit: false);
            var marker = new RuntimeStateMutation(BootstrapSnapshotDomain, image.RequestId, NodeId, 0,
                DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(image, JsonOptions));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _durableWrites[marker] = completion;
            var queued = false;
            Interlocked.Increment(ref _queueDepth);
            try
            {
                QueueDurableAsync(marker, token).AsTask().GetAwaiter().GetResult();
                queued = true;
                // Once submitted, finish the local transaction even if the HTTP caller disconnects.
                completion.Task.GetAwaiter().GetResult();
                BootstrapBeforePublish?.Invoke();
                try { ApplyBootstrap(image); }
                catch (RuntimeStateCapacityException ex)
                {
                    _journalCapacityFailed = true;
                    _capacityError = ex.Budget;
                    throw;
                }
                _counterComponentId = JsonSerializer.Serialize(new[] { NodeId, Guid.NewGuid().ToString("N") });
            }
            finally
            {
                if (!queued) Interlocked.Decrement(ref _queueDepth);
                _durableWrites.TryRemove(marker, out _);
            }
        }
        foreach (var domain in image.Scope.Domains) StateChanged?.Invoke(domain);
    }, token);

    private void ApplyBootstrap(RuntimeStateBootstrapImage image)
    {
        var next = new Dictionary<string, RuntimeStateMutation>(_entries, StringComparer.Ordinal);
        ApplyBootstrapEntries(next, image);
        CheckSnapshotCapacity(next, image, commit: true);
        foreach (var entry in image.Items) InterlockedExtensions.Max(ref _sequence, entry.Sequence);
        Volatile.Write(ref _entries, new ConcurrentDictionary<string, RuntimeStateMutation>(next, StringComparer.Ordinal));
        _bootstrapFences[image.AuthorityNodeId] = new(image.AuthorityNodeId, image.RequestId, image.Digest);
    }

    private void ApplyBootstrapEntries(Dictionary<string, RuntimeStateMutation> entries, RuntimeStateBootstrapImage image)
    {
        foreach (var key in entries.Where(p => image.Scope.Contains(p.Value) && p.Value.Domain != CcPolicyGeneration.Domain).Select(p => p.Key).ToArray())
            entries.Remove(key);
        foreach (var mutation in image.Items)
        {
            ValidateMutation(mutation);
            var key = Identity(mutation.Domain, mutation.Key);
            entries.TryGetValue(key, out var prior);
            entries[key] = mutation.Domain == CcPolicyGeneration.Domain ? MergeEntries(prior, mutation) : mutation;
        }
    }

    private RuntimeStateBootstrapImage? ReadBootstrapImage(string line)
    {
        using var json = JsonDocument.Parse(line);
        if (!json.RootElement.TryGetProperty("domain", out var domain) || domain.GetString() != BootstrapSnapshotDomain) return null;
        var mutation = json.RootElement.Deserialize<RuntimeStateMutation>(JsonOptions)!;
        var image = mutation.Value?.Deserialize<RuntimeStateBootstrapImage>(JsonOptions) ?? throw new JsonException("Missing bootstrap image");
        image.Validate();
        if (image.RecipientNodeId != NodeId || mutation.NodeId != NodeId || mutation.Key != image.RequestId
            || mutation.Sequence != 0 || mutation.Deleted || mutation.ExpiresAt is not null || mutation.CounterComponents is not null)
            throw new JsonException("Invalid bootstrap journal identity");
        return image;
    }

    private BootstrapFence? ReadBootstrapFence(string line)
    {
        using var json = JsonDocument.Parse(line);
        if (!json.RootElement.TryGetProperty("domain", out var domain) || domain.GetString() != BootstrapFenceDomain) return null;
        var mutation = json.RootElement.Deserialize<RuntimeStateMutation>(JsonOptions)!;
        var fence = mutation.Value?.Deserialize<BootstrapFence>(JsonOptions) ?? throw new JsonException("Missing bootstrap fence");
        if (mutation.NodeId != NodeId || mutation.Key != fence.Authority || mutation.Sequence != 0 || mutation.Deleted
            || !Guid.TryParseExact(fence.RequestId, "N", out _) || fence.Digest.Length != 64)
            throw new JsonException("Invalid bootstrap fence");
        return fence;
    }

    private bool RecoverBootstrapRecord(string line)
    {
        if (ReadBootstrapImage(line) is { } image) { ApplyBootstrap(image); return true; }
        if (ReadBootstrapFence(line) is not { } fence) return false;
        ChargeMemory("bootstrap:" + fence.Authority, fence);
        _bootstrapFences[fence.Authority] = fence;
        return true;
    }

    private string SerializeBootstrapFence(BootstrapFence fence) => JsonSerializer.Serialize(new RuntimeStateMutation(
        BootstrapFenceDomain, fence.Authority, NodeId, 0, DateTimeOffset.UtcNow, null, false,
        JsonSerializer.SerializeToElement(fence, JsonOptions)), JsonOptions);
}
