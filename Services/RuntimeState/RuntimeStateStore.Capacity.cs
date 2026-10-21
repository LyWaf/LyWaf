using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

public sealed partial class RuntimeStateStore
{
    private readonly object _capacityGate = new();
    private Dictionary<string, long> _memoryCharges = new(StringComparer.Ordinal);
    private long _stateBytes;
    private long _journalQueueBytes;
    private string? _capacityError;
    private volatile bool _journalCapacityFailed;

    internal static bool ValidCapacityOptions(RuntimeStateOptions o) => o.MaxStateEntries > 0 && o.MaxStateBytes > 0
        && o.MaxJournalQueueBytes > 0 && o.MaxJournalDiskBytes > 0 && o.MaxDiskBytes > 0 && o.MaxDiskFiles > 0
        && o.MaxExpiredVersionEntries > 0 && o.MaxExpiredVersionBytes > 0;

    private void CheckWritableCapacity()
    {
        if (_journalCapacityFailed) throw new RuntimeStateCapacityException("journal-read-only");
    }

    private void PersistCcGenerationWithinBudget(RuntimeStateMutation mutation)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mutation, JsonOptions) + "\n");
        using var disk = RuntimeStateDiskBudget.Reserve(_options.CurrentValue, bytes.LongLength,
            File.Exists(CcGenerationPath) ? 0 : 1);
        if ((File.Exists(CcGenerationPath) ? new FileInfo(CcGenerationPath).Length : 0)
            > _options.CurrentValue.MaxJournalDiskBytes - bytes.LongLength)
            throw new RuntimeStateCapacityException("cc-generation-journal");
        // Disk lease first: the ordinary journal uses the same disk -> memory lock order.
        lock (_capacityGate)
        {
            CheckEntryCapacity(mutation);
            using var stream = new FileStream(CcGenerationPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            // Admission was checked before fsync. A concurrent ordinary-journal failure cannot undo this durable release.
            ChargeMemory("entry:" + Identity(mutation.Domain, mutation.Key), mutation);
        }
    }

    private void ChargeMemory(string key, object record, bool reservation = false)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions).LongLength;
        lock (_capacityGate)
        {
            if (reservation) bytes = Math.Max(bytes, _memoryCharges.GetValueOrDefault(key));
            var total = _stateBytes - _memoryCharges.GetValueOrDefault(key) + bytes;
            var count = _memoryCharges.Count + (_memoryCharges.ContainsKey(key) ? 0 : 1);
            CheckMemoryLimit(count, total);
            _memoryCharges[key] = bytes;
            _stateBytes = total;
        }
    }

    private void CheckMemoryLimit(int count, long bytes)
    {
        var options = _options.CurrentValue;
        if (count > options.MaxStateEntries || bytes > options.MaxStateBytes)
        {
            _capacityError = "state-memory";
            throw new RuntimeStateCapacityException("state-memory");
        }
    }

    private void ChargeEntry(RuntimeStateMutation mutation)
    {
        CheckWritableCapacity();
        ChargeMemory("entry:" + Identity(mutation.Domain, mutation.Key), mutation);
    }

    private void CheckEntryCapacity(RuntimeStateMutation mutation)
    {
        CheckWritableCapacity();
        var key = "entry:" + Identity(mutation.Domain, mutation.Key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(mutation, JsonOptions).LongLength;
        lock (_capacityGate)
            CheckMemoryLimit(_memoryCharges.Count + (_memoryCharges.ContainsKey(key) ? 0 : 1),
                _stateBytes - _memoryCharges.GetValueOrDefault(key) + bytes);
    }

    private void ForgetEntry(string identity)
    {
        lock (_capacityGate)
            if (_memoryCharges.Remove("entry:" + identity, out var bytes)) _stateBytes -= bytes;
    }

    private void CheckSnapshotCapacity(Dictionary<string, RuntimeStateMutation> entries, RuntimeStateBootstrapImage image, bool commit)
    {
        lock (_capacityGate)
        {
            var charges = _memoryCharges.Where(p => !p.Key.StartsWith("entry:", StringComparison.Ordinal))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (var entry in entries) charges["entry:" + entry.Key] = JsonSerializer.SerializeToUtf8Bytes(entry.Value, JsonOptions).LongLength;
            charges["bootstrap:" + image.AuthorityNodeId] = JsonSerializer.SerializeToUtf8Bytes(
                new BootstrapFence(image.AuthorityNodeId, image.RequestId, image.Digest), JsonOptions).LongLength;
            var total = charges.Values.Sum();
            CheckMemoryLimit(charges.Count, total);
            if (commit) { _memoryCharges = charges; _stateBytes = total; }
        }
    }

    private bool ChargeJournalQueue(RuntimeStateMutation mutation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(mutation, JsonOptions).LongLength;
        lock (_capacityGate)
        {
            if (bytes > _options.CurrentValue.MaxJournalQueueBytes - _journalQueueBytes)
            {
                _capacityError = "journal-queue";
                return false;
            }
            _journalQueueBytes += bytes;
            return true;
        }
    }

    private void ReleaseJournalQueue(RuntimeStateMutation mutation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(mutation, JsonOptions).LongLength;
        lock (_capacityGate) _journalQueueBytes -= bytes;
    }

    private async ValueTask QueueDurableAsync(RuntimeStateMutation mutation, CancellationToken token)
    {
        EnsureSyncWriterAvailable();
        CheckWritableCapacity();
        if (!ChargeJournalQueue(mutation)) throw new RuntimeStateCapacityException("journal-queue");
        try { await _journalQueue.Writer.WriteAsync(mutation, token); }
        catch (System.Threading.Channels.ChannelClosedException ex)
        {
            ReleaseJournalQueue(mutation);
            if (_journalCapacityFailed) throw new RuntimeStateCapacityException(_capacityError ?? "journal-read-only");
            throw new IOException("Runtime journal cannot accept durable writes", ex);
        }
        catch
        {
            ReleaseJournalQueue(mutation);
            if (_journalCapacityFailed) throw new RuntimeStateCapacityException(_capacityError ?? "journal-read-only");
            throw;
        }
    }
}
