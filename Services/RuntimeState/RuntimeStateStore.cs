using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Services.RuntimeState;

/// <summary>
/// 进程内运行状态主存储。变更先同步进入内存，再通过有界队列异步追加到日志；启动恢复逐行解析，不整文件加载。
/// </summary>
public sealed partial class RuntimeStateStore : BackgroundService, IRuntimeStateStore
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IOptionsMonitor<RuntimeStateOptions> _options;
    private ConcurrentDictionary<string, RuntimeStateMutation> _entries = new(StringComparer.Ordinal);
    private readonly Channel<RuntimeStateMutation> _journalQueue;
    private string _counterComponentId;
    private long _sequence;
    private long _persisted;
    private long _dropped;
    private int _queueDepth;
    private long _enqueueCount;

    /// <summary>本节点产生、需要复制给其他节点的状态变更。</summary>
    internal event Action<RuntimeStateMutation>? MutationPublished;
    /// <summary>任意来源的有效状态合并完成后触发，供本地业务缓存失效。</summary>
    internal event Action<string>? StateChanged;

    public RuntimeStateStore(IOptionsMonitor<RuntimeStateOptions> options)
    {
        _options = options;
        if (!ValidCapacityOptions(options.CurrentValue)) throw new ArgumentException("Invalid runtime capacity limits", nameof(options));
        using (RuntimeStateDiskBudget.Reserve(options.CurrentValue, 0)) { }
        foreach (var journal in new[] { JournalPath, CcGenerationPath })
            if (File.Exists(journal) && new FileInfo(journal).Length > options.CurrentValue.MaxJournalDiskBytes)
                throw new RuntimeStateCapacityException("journal-disk-recovery");
        // A fresh component prevents post-crash increments from being hidden by a peer's pre-crash maximum.
        _counterComponentId = JsonSerializer.Serialize(new[] { NodeId, Guid.NewGuid().ToString("N") });
        var capacity = Math.Clamp(options.CurrentValue.QueueCapacity, 100, 1_000_000);
        _journalQueue = Channel.CreateBounded<RuntimeStateMutation>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        RecoverCcGenerations();
        Recover();
    }

    public void Set<T>(string domain, string key, T value, TimeSpan? timeToLive = null)
    { lock (_ccGenerationGate) SetCore(domain, key, value, timeToLive); }
    private void SetCore<T>(string domain, string key, T value, TimeSpan? timeToLive)
    {
        RejectGenericGenerationWrite(domain);
        ValidateIdentity(domain, key);
        var json = JsonSerializer.SerializeToElement(value, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json.GetRawText()) > _options.CurrentValue.MaxValueBytes)
            throw new InvalidOperationException($"运行状态值超过 {_options.CurrentValue.MaxValueBytes} 字节限制。");
        Publish(new(domain, key, NodeId, NextSequence(), DateTimeOffset.UtcNow,
            timeToLive.HasValue ? DateTimeOffset.UtcNow.Add(timeToLive.Value) : null, false, json));
    }

    public bool TryGet<T>(string domain, string key, out T? value)
    {
        value = default;
        if (!_entries.TryGetValue(Identity(domain, key), out var entry) || !IsActive(entry) || entry.Value is null)
            return false;
        try
        {
            value = entry.Value.Value.Deserialize<T>(JsonOptions);
            return value is not null;
        }
        catch (JsonException) { return false; }
    }

    public bool TryTake<T>(string domain, string key, out T? value)
    { lock (_ccGenerationGate) return TryTakeCore(domain, key, out value); }
    private bool TryTakeCore<T>(string domain, string key, out T? value)
    {
        RejectGenericGenerationWrite(domain);
        value = default;
        var identity = Identity(domain, key);
        while (true)
        {
            if (!_entries.TryGetValue(identity, out var current) || !IsActive(current) || current.Value is null)
                return false;
            T? parsed;
            try { parsed = current.Value.Value.Deserialize<T>(JsonOptions); }
            catch (JsonException) { return false; }
            if (parsed is null) return false;
            var tombstone = new RuntimeStateMutation(domain, key, NodeId, NextSequence(), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(24), true, null);
            ChargeEntry(tombstone);
            if (!_entries.TryUpdate(identity, tombstone, current)) continue;
            value = parsed;
            Enqueue(tombstone);
            MutationPublished?.Invoke(tombstone);
            StateChanged?.Invoke(domain);
            return true;
        }
    }

    public bool Remove(string domain, string key)
    { lock (_ccGenerationGate) return RemoveCore(domain, key); }
    private bool RemoveCore(string domain, string key)
    {
        RejectGenericGenerationWrite(domain);
        ValidateIdentity(domain, key);
        var identity = Identity(domain, key);
        var existed = _entries.TryGetValue(identity, out var current) && IsActive(current);
        Publish(new(domain, key, NodeId, NextSequence(), DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(24), true, null));
        return existed;
    }

    public long IncrementWindow(string domain, string key, DateTimeOffset windowStart, TimeSpan windowLength, long amount = 1)
    { lock (_ccGenerationGate) return IncrementWindowCore(domain, key, windowStart, windowLength, amount); }
    private long IncrementWindowCore(string domain, string key, DateTimeOffset windowStart, TimeSpan windowLength, long amount)
    {
        RejectGenericGenerationWrite(domain);
        if (!IsCurrentCcState(domain, key)) return 0;
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        ValidateIdentity(domain, key);
        var windowKey = $"{key}|{windowStart.ToUnixTimeMilliseconds()}";
        var identity = Identity(domain, windowKey);
        RuntimeStateMutation updated;
        while (true)
        {
            _entries.TryGetValue(identity, out var current);
            var components = current is not null && IsActive(current) && current.CounterComponents is not null
                ? new Dictionary<string, long>(current.CounterComponents, StringComparer.Ordinal)
                : new Dictionary<string, long>(StringComparer.Ordinal);
            components[_counterComponentId] = checked(components.GetValueOrDefault(_counterComponentId) + amount);
            updated = new(domain, windowKey, NodeId, NextSequence(), DateTimeOffset.UtcNow,
                windowStart.Add(windowLength), false, null, components);
            ValidateCounterComponents(components);
            ChargeEntry(updated);
            if (current is null ? _entries.TryAdd(identity, updated) : _entries.TryUpdate(identity, updated, current)) break;
        }
        Enqueue(updated);
        MutationPublished?.Invoke(updated);
        return updated.CounterComponents!.Values.Sum();
    }

    public long GetWindowValue(string domain, string key, DateTimeOffset windowStart)
    {
        var windowKey = $"{key}|{windowStart.ToUnixTimeMilliseconds()}";
        return _entries.TryGetValue(Identity(domain, windowKey), out var entry) && IsActive(entry)
            ? entry.CounterComponents?.Values.Sum() ?? 0 : 0;
    }

    public bool TryClaim(string domain, string key, TimeSpan timeToLive)
    { lock (_ccGenerationGate) return TryClaimCore(domain, key, timeToLive); }
    private bool TryClaimCore(string domain, string key, TimeSpan timeToLive)
    {
        RejectGenericGenerationWrite(domain);
        if (!IsCurrentCcState(domain, key)) return false;
        ValidateIdentity(domain, key);
        var identity = Identity(domain, key);
        while (true)
        {
            _entries.TryGetValue(identity, out var current);
            if (current is not null && IsActive(current)) return false;
            var value = JsonSerializer.SerializeToElement(true);
            var claim = new RuntimeStateMutation(domain, key, NodeId, NextSequence(), DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.Add(timeToLive), false, value);
            ChargeEntry(claim);
            if (current is null ? _entries.TryAdd(identity, claim) : _entries.TryUpdate(identity, claim, current))
            {
                Enqueue(claim);
                MutationPublished?.Invoke(claim);
                return true;
            }
        }
    }

    public bool Merge(RuntimeStateMutation mutation, bool replicate = false)
    { lock (_ccGenerationGate) return MergeCore(mutation, replicate); }
    private bool MergeCore(RuntimeStateMutation mutation, bool replicate)
    {
        mutation = SnapshotMutation(mutation);
        ValidateMutation(mutation);
        if (mutation.Domain == CcPolicyGeneration.Domain)
        {
            CcReleaseAuthorization.Verify(mutation, _options.CurrentValue);
            return MergeCcGeneration(mutation, replicate);
        }
        if (IsObsoleteCcState(mutation)) return false;
        var identity = Identity(mutation.Domain, mutation.Key);
        while (true)
        {
            _entries.TryGetValue(identity, out var current);
            var merged = MergeEntries(current, mutation);
            if (ReferenceEquals(merged, current)) return false;
            ChargeEntry(merged);
            if (current is null ? _entries.TryAdd(identity, merged) : _entries.TryUpdate(identity, merged, current))
            {
                InterlockedExtensions.Max(ref _sequence, mutation.Sequence);
                Enqueue(merged);
                if (replicate) MutationPublished?.Invoke(merged);
                StateChanged?.Invoke(merged.Domain);
                return true;
            }
        }
    }

    public IReadOnlyList<RuntimeStateSnapshot> GetSnapshots(string? domain = null)
        => _entries.Values.Where(IsActive)
            .Where(x => domain is null || string.Equals(x.Domain, domain, StringComparison.Ordinal))
            .OrderBy(x => x.Domain, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new RuntimeStateSnapshot(x.Domain, x.Key, x.NodeId, x.Sequence, x.ChangedAt,
                x.ExpiresAt, x.Deleted, x.CounterComponents?.Values.Sum())).ToArray();

    public IReadOnlyList<(string Key, T Value)> GetValues<T>(string domain)
    {
        var result = new List<(string Key, T Value)>();
        foreach (var entry in _entries.Values.Where(x => x.Domain == domain && IsActive(x) && x.Value is not null))
        {
            try
            {
                var value = entry.Value!.Value.Deserialize<T>(JsonOptions);
                if (value is not null) result.Add((entry.Key, value));
            }
            catch (JsonException) { }
        }
        return result;
    }

    public RuntimeStateStatus GetStatus()
    { lock (_capacityGate) return new(NodeId, _entries.Values.Count(IsActive),
        Interlocked.Read(ref _sequence), Interlocked.Read(ref _persisted), Interlocked.Read(ref _dropped),
        Volatile.Read(ref _queueDepth), _stateBytes, _memoryCharges.Count, _journalQueueBytes, _capacityError, _journalCapacityFailed); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await RunJournalAsync(stoppingToken); }
        finally
        {
            Interlocked.Exchange(ref _journalStopping, 1);
            _journalQueue.Writer.TryComplete();
            FailDurableWrites(new IOException("Runtime journal stopped before durable confirmation"));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _journalStopping, 1);
        _journalQueue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    private void Publish(RuntimeStateMutation mutation)
    {
        ChargeEntry(mutation);
        _entries[Identity(mutation.Domain, mutation.Key)] = mutation;
        Enqueue(mutation);
        MutationPublished?.Invoke(mutation);
        StateChanged?.Invoke(mutation.Domain);
    }

    private void Enqueue(RuntimeStateMutation mutation)
    {
        CleanupExpiredPeriodically();
        if (!ChargeJournalQueue(mutation)) { Interlocked.Increment(ref _dropped); return; }
        Interlocked.Increment(ref _queueDepth);
        if (_journalQueue.Writer.TryWrite(mutation)) return;
        ReleaseJournalQueue(mutation);
        Interlocked.Decrement(ref _queueDepth);
        Interlocked.Increment(ref _dropped);
    }

    private void Recover()
    {
        var path = JournalPath;
        if (!File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.SequentialScan);
            if (stream.Length > _options.CurrentValue.MaxJournalDiskBytes)
                throw new RuntimeStateCapacityException("journal-disk-recovery");
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    if (ReadJournalSequence(line) is { } sequence)
                    {
                        InterlockedExtensions.Max(ref _sequence, sequence);
                        continue;
                    }
                    if (RecoverBootstrapRecord(line)) continue;
                    if (ReadSyncCheckpoint(line) is { } checkpoint)
                    {
                        RecoverSyncCheckpoint(checkpoint);
                        continue;
                    }
                    var mutation = JsonSerializer.Deserialize<RuntimeStateMutation>(line, JsonOptions);
                    if (mutation is not null) MergeRecovered(mutation);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                {
                    _syncJournalCorrupt = true;
                    Logger.Warn(ex, "忽略损坏的运行状态日志记录");
                }
            }
        }
        catch (RuntimeStateCapacityException) { throw; }
        catch (IOException ex) { _syncJournalCorrupt = true; Logger.Warn(ex, "恢复运行状态日志失败: {Path}", path); }
    }

    /// <summary>导出仍需反熵传播的值和删除墓碑；仅供复制后台服务使用。</summary>
    internal IReadOnlyList<RuntimeStateMutation> ExportMutations()
    {
        CleanupExpired();
        return _entries.Values.Where(x => !x.ExpiresAt.HasValue || x.ExpiresAt > DateTimeOffset.UtcNow).ToArray();
    }

    internal (IReadOnlyList<RuntimeStateMutation> Items, string? NextCursor) ExportPage(string? after, int pageSize)
    {
        if (pageSize is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(pageSize));
        CleanupExpired();
        // Keep only the earliest page plus one lookahead, without sorting/materializing the entire store.
        var candidates = new SortedDictionary<string, RuntimeStateMutation>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            if (after is not null && string.CompareOrdinal(entry.Key, after) <= 0) continue;
            candidates[entry.Key] = entry.Value;
            if (candidates.Count > pageSize + 1) candidates.Remove(candidates.Last().Key);
        }
        var items = candidates.Take(pageSize).ToArray();
        return (items.Select(x => x.Value).ToArray(), candidates.Count > pageSize ? items[^1].Key : null);
    }

    private void CleanupExpiredPeriodically()
    {
        if ((Interlocked.Increment(ref _enqueueCount) & 1023) == 0) CleanupExpired();
    }

    private void CleanupExpired()
    { lock (_ccGenerationGate) CleanupExpiredCore(); }
    private void CleanupExpiredCore()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _entries.Where(x => x.Value.ExpiresAt.HasValue && x.Value.ExpiresAt <= now))
            if (_entries.TryRemove(entry)) ForgetEntry(entry.Key);
    }

    private void MergeRecovered(RuntimeStateMutation mutation)
    {
        ValidateMutation(mutation);
        var identity = Identity(mutation.Domain, mutation.Key);
        while (true)
        {
            _entries.TryGetValue(identity, out var current);
            var merged = MergeEntries(current, mutation);
            if (ReferenceEquals(merged, current)) return;
            ChargeEntry(merged);
            if (current is null ? _entries.TryAdd(identity, merged) : _entries.TryUpdate(identity, merged, current))
            {
                InterlockedExtensions.Max(ref _sequence, mutation.Sequence);
                return;
            }
        }
    }

    private RuntimeStateMutation MergeEntries(RuntimeStateMutation? current, RuntimeStateMutation incoming)
    {
        if (current is null) return incoming;
        if (incoming.Domain == CcPolicyGeneration.Domain)
            return ReadGeneration(incoming).CompareTo(ReadGeneration(current)) > 0 ? incoming : current;
        if (current.CounterComponents is not null || incoming.CounterComponents is not null)
        {
            var components = new Dictionary<string, long>(current.CounterComponents ?? new Dictionary<string, long>(), StringComparer.Ordinal);
            var changed = false;
            foreach (var pair in incoming.CounterComponents ?? new Dictionary<string, long>())
            {
                if (pair.Value > components.GetValueOrDefault(pair.Key)) { components[pair.Key] = pair.Value; changed = true; }
            }
            if (!changed && CompareVersion(incoming, current) <= 0) return current;
            var latest = CompareVersion(incoming, current) > 0 ? incoming : current;
            ValidateCounterComponents(components);
            return latest with { CounterComponents = components };
        }
        return CompareVersion(incoming, current) > 0 ? incoming : current;
    }

    internal void ValidateForSync(RuntimeStateMutation mutation) => ValidateMutation(mutation);

    private void ValidateMutation(RuntimeStateMutation mutation)
    {
        ValidateIdentity(mutation.Domain, mutation.Key);
        if (mutation.Domain == CcPolicyGeneration.Domain) ValidateGenerationMutation(mutation);
        if (mutation.Sequence < 0 || string.IsNullOrWhiteSpace(mutation.NodeId))
            throw new InvalidOperationException("运行状态版本无效。");
        if (mutation.Value is { } value && Encoding.UTF8.GetByteCount(value.GetRawText()) > _options.CurrentValue.MaxValueBytes)
            throw new InvalidOperationException("运行状态值超过限制。");
        if (mutation.CounterComponents is { } components) ValidateCounterComponents(components);
    }

    private void ValidateCounterComponents(IReadOnlyDictionary<string, long> components)
    {
        if (components.Any(x => x.Value < 0 || string.IsNullOrWhiteSpace(x.Key) || x.Key.Length > 512) ||
            JsonSerializer.SerializeToUtf8Bytes(components, JsonOptions).Length > _options.CurrentValue.MaxValueBytes)
            throw new InvalidOperationException("Runtime counter components exceed the allowed size or contain invalid values.");
        long total = 0;
        foreach (var value in components.Values)
        {
            if (value > long.MaxValue - total) throw new InvalidOperationException("Runtime counter total exceeds Int64 capacity.");
            total += value;
        }
    }

    private static int CompareVersion(RuntimeStateMutation left, RuntimeStateMutation right)
    {
        var sequence = left.Sequence.CompareTo(right.Sequence);
        return sequence != 0 ? sequence : string.CompareOrdinal(left.NodeId, right.NodeId);
    }

    private static RuntimeStateMutation SnapshotMutation(RuntimeStateMutation mutation) => mutation with
    {
        Value = mutation.Value?.Clone(),
        CounterComponents = mutation.CounterComponents is null ? null
            : new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(new Dictionary<string, long>(mutation.CounterComponents, StringComparer.Ordinal))
    };

    private bool IsActive(RuntimeStateMutation mutation)
        => !mutation.Deleted && (!mutation.ExpiresAt.HasValue || mutation.ExpiresAt > DateTimeOffset.UtcNow)
            && IsCurrentCcState(mutation.Domain, mutation.Key);
    private static string Identity(string domain, string key) => domain + "\n" + key;
    private static void ValidateIdentity(string domain, string key)
    {
        if (domain is SyncCheckpointDomain or BootstrapSnapshotDomain or BootstrapFenceDomain)
            throw new ArgumentException("Runtime sync checkpoint domain is reserved", nameof(domain));
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 128 || domain.Contains('\n'))
            throw new ArgumentException("状态域无效。", nameof(domain));
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024 || key.Contains('\n'))
            throw new ArgumentException("状态键无效。", nameof(key));
    }

    private string NodeId => string.IsNullOrWhiteSpace(_options.CurrentValue.NodeId)
        ? Environment.MachineName : _options.CurrentValue.NodeId.Trim();
    private string StateDirectory => Path.GetFullPath(_options.CurrentValue.StateDirectory);
    private string JournalPath => Path.Combine(StateDirectory, "runtime-state.jsonl");
    private long NextSequence() => Interlocked.Increment(ref _sequence);
}

internal static class InterlockedExtensions
{
    public static void Max(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
