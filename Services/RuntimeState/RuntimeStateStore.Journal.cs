using System.Text;
using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

public sealed partial class RuntimeStateStore
{
    // Test seam runs after durable staging and before the atomic commit.
    internal Action? JournalBeforeReplace { get; set; }
    // Producers can enqueue out of version order. Keep retired versions until this consumer stops,
    // so a delayed pre-expiry mutation cannot resurrect a record omitted by compaction.
    private readonly Dictionary<string, RuntimeStateMutation> _journalExpiredVersions = new(StringComparer.Ordinal);
    internal int RetiredJournalVersionCount => _journalExpiredVersions.Count;

    private static long? ReadJournalSequence(string line)
    {
        if (!line.StartsWith("{\"journalSequence\":", StringComparison.Ordinal)) return null;
        using var document = JsonDocument.Parse(line);
        if (!document.RootElement.GetProperty("journalSequence").TryGetInt64(out var sequence) || sequence < 0)
            throw new JsonException("Invalid journal sequence checkpoint.");
        return sequence;
    }

    private async Task RunJournalAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(StateDirectory);
        var path = JournalPath;
        StreamWriter? writer = null;
        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        long nextCompaction = Math.Max(1, _options.CurrentValue.MaxJournalBytes);
        try
        {
            await CompactIfNeededAsync(stoppingToken);
            try
            {
                await foreach (var mutation in _journalQueue.Reader.ReadAllAsync(stoppingToken))
                {
                    await AppendAsync(mutation);
                    await CompactIfNeededAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            while (_journalQueue.Reader.TryRead(out var pending)) await AppendAsync(pending);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            while (_journalQueue.Reader.TryRead(out var pending)) await AppendAsync(pending);
        }
        catch (RuntimeStateCapacityException ex)
        {
            _capacityError = ex.Budget;
            _journalCapacityFailed = true;
            _journalQueue.Writer.TryComplete(ex);
            FailDurableWrites(ex);
            while (_journalQueue.Reader.TryRead(out var pending))
            {
                ReleaseJournalQueue(pending);
                Interlocked.Decrement(ref _queueDepth);
                Interlocked.Increment(ref _dropped);
            }
            Logger.Error(ex, "Runtime journal entered capacity read-only mode; existing state and fences retained");
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync();
        }

        async Task AppendAsync(RuntimeStateMutation mutation)
        {
            try
            {
            var identity = Identity(mutation.Domain, mutation.Key);
            var supersedesRetired = false;
            if (_journalExpiredVersions.TryGetValue(identity, out var retired))
            {
                if (CompareVersion(mutation, retired) <= 0)
                {
                    Interlocked.Increment(ref _persisted);
                    await CompleteDurableWriteAsync(mutation, writer);
                    return;
                }
                supersedesRetired = true;
            }
            var line = JsonSerializer.Serialize(mutation, JsonOptions);
            var added = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (bytes > _options.CurrentValue.MaxJournalDiskBytes - added)
            {
                if (writer is not null) { await writer.DisposeAsync(); writer = null; }
                if (File.Exists(path)) await CompactJournalAsync(path, CancellationToken.None);
                bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (bytes > _options.CurrentValue.MaxJournalDiskBytes - added)
                    throw new RuntimeStateCapacityException("journal-disk");
            }
            using var budget = RuntimeStateDiskBudget.Reserve(_options.CurrentValue, added, File.Exists(path) ? 0 : 1);
            writer ??= new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous), new UTF8Encoding(false));
            // Once dequeued, finish the whole record even during shutdown.
            await writer.WriteLineAsync(line);
            bytes += Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(writer.NewLine);
            await writer.FlushAsync();
            Interlocked.Increment(ref _persisted);
            await CompleteDurableWriteAsync(mutation, writer);
            if (supersedesRetired) _journalExpiredVersions.Remove(identity);
            }
            finally
            {
                Interlocked.Decrement(ref _queueDepth);
                ReleaseJournalQueue(mutation);
            }
        }

        async Task CompactIfNeededAsync(CancellationToken token)
        {
            if (bytes < nextCompaction) return;
            if (writer is not null)
            {
                await writer.DisposeAsync();
                writer = null;
            }
            await CompactJournalAsync(path, token);
            bytes = new FileInfo(path).Length;
            var threshold = Math.Max(1, _options.CurrentValue.MaxJournalBytes);
            // A large live set or failed replacement must not cause a full scan per append.
            nextCompaction = Math.Max(threshold, bytes > long.MaxValue - threshold ? long.MaxValue : bytes + threshold);
        }
    }

    // Only the journal consumer calls this in production. Pending producers keep using TryWrite.
    internal async Task<bool> CompactJournalAsync(string path, CancellationToken token)
    {
        var temporary = path + ".compact-" + Guid.NewGuid().ToString("N");
        try
        {
            var entries = new Dictionary<string, RuntimeStateMutation>(StringComparer.Ordinal);
            var syncCheckpoints = new Dictionary<string, RuntimeStateSyncAck>(StringComparer.Ordinal);
            var bootstrapFences = new Dictionary<string, BootstrapFence>(StringComparer.Ordinal);
            long scratchBytes = 0;
            void ChargeScratch(object? previous, object replacement)
            {
                scratchBytes += JsonSerializer.SerializeToUtf8Bytes(replacement, JsonOptions).LongLength
                    - (previous is null ? 0 : JsonSerializer.SerializeToUtf8Bytes(previous, JsonOptions).LongLength);
                var count = (long)entries.Count + syncCheckpoints.Count + bootstrapFences.Count + (previous is null ? 1 : 0);
                if (count > (long)_options.CurrentValue.MaxStateEntries + _options.CurrentValue.MaxExpiredVersionEntries
                    || scratchBytes > _options.CurrentValue.MaxStateBytes + _options.CurrentValue.MaxExpiredVersionBytes)
                    throw new RuntimeStateCapacityException("journal-compaction-memory");
            }
            long sequence = 0;
            using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)))
            {
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    token.ThrowIfCancellationRequested();
                    if (line.Length == 0) continue;
                    if (ReadJournalSequence(line) is { } checkpoint)
                    {
                        sequence = Math.Max(sequence, checkpoint);
                        continue;
                    }
                    if (ReadBootstrapImage(line) is { } image)
                    {
                        ApplyBootstrapEntries(entries, image);
                        foreach (var entry in image.Items) sequence = Math.Max(sequence, entry.Sequence);
                        bootstrapFences[image.AuthorityNodeId] = new(image.AuthorityNodeId, image.RequestId, image.Digest);
                        scratchBytes = CheckCompactionMemory(entries, syncCheckpoints, bootstrapFences);
                        continue;
                    }
                    if (ReadBootstrapFence(line) is { } bootstrapFence)
                    {
                        ChargeScratch(bootstrapFences.GetValueOrDefault(bootstrapFence.Authority), bootstrapFence);
                        bootstrapFences[bootstrapFence.Authority] = bootstrapFence;
                        continue;
                    }
                    if (ReadSyncCheckpoint(line) is { } sync)
                    {
                        ChargeScratch(syncCheckpoints.GetValueOrDefault(SyncStreamKey(sync.ClusterId, sync.SenderNodeId, sync.ReceiverNodeId)), sync);
                        syncCheckpoints[SyncStreamKey(sync.ClusterId, sync.SenderNodeId, sync.ReceiverNodeId)] = sync;
                        continue;
                    }
                    // Unlike recovery, compaction aborts on damage instead of committing a lossy rewrite.
                    var mutation = JsonSerializer.Deserialize<RuntimeStateMutation>(line, JsonOptions)
                        ?? throw new JsonException("Null journal record.");
                    ValidateMutation(mutation);
                    sequence = Math.Max(sequence, mutation.Sequence);
                    var key = Identity(mutation.Domain, mutation.Key);
                    entries.TryGetValue(key, out var current);
                    var merged = MergeEntries(current, mutation);
                    ChargeScratch(current, merged);
                    entries[key] = merged;
                }
            }
            CheckCompactionMemory(entries, syncCheckpoints, bootstrapFences);
            var retired = new Dictionary<string, RuntimeStateMutation>(_journalExpiredVersions, StringComparer.Ordinal);
            foreach (var pair in entries)
                if (pair.Value.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                    retired[pair.Key] = pair.Value with { Value = null, CounterComponents = null };
            if (retired.Count > _options.CurrentValue.MaxExpiredVersionEntries
                || retired.Values.Sum(m => JsonSerializer.SerializeToUtf8Bytes(m, JsonOptions).LongLength) > _options.CurrentValue.MaxExpiredVersionBytes)
                throw new RuntimeStateCapacityException("expired-versions");
            var now = DateTimeOffset.UtcNow;
            var lines = new List<string> { JsonSerializer.Serialize(new { journalSequence = sequence }) };
            lines.AddRange(entries.Values.Where(m => m.ExpiresAt is null || m.ExpiresAt > now).Select(m => JsonSerializer.Serialize(m, JsonOptions)));
            lines.AddRange(syncCheckpoints.Values.Select(SerializeSyncCheckpoint));
            lines.AddRange(bootstrapFences.Values.Select(SerializeBootstrapFence));
            var outputBytes = lines.Sum(line => (long)Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine));
            if (outputBytes > _options.CurrentValue.MaxJournalDiskBytes) throw new RuntimeStateCapacityException("journal-live-set");
            using var budget = RuntimeStateDiskBudget.Reserve(_options.CurrentValue, outputBytes, 1, token);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await using var output = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
                foreach (var line in lines)
                {
                    token.ThrowIfCancellationRequested();
                    await output.WriteLineAsync(line.AsMemory(), token);
                }
                await output.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            JournalBeforeReplace?.Invoke();
            token.ThrowIfCancellationRequested();
            // Same-directory replacement: never delete the destination first or fall back to copying.
            File.Replace(temporary, path, destinationBackupFileName: null);
            _journalExpiredVersions.Clear();
            foreach (var pair in retired) _journalExpiredVersions.Add(pair.Key, pair.Value);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            if (ex is RuntimeStateCapacityException capacity) _capacityError = capacity.Budget;
            Logger.Warn(ex, "Runtime journal compaction failed; original journal retained: {Path}", path);
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Logger.Warn(ex, "Could not remove journal staging file: {Path}", temporary); }
        }
    }

    private long CheckCompactionMemory(Dictionary<string, RuntimeStateMutation> entries,
        Dictionary<string, RuntimeStateSyncAck> checkpoints, Dictionary<string, BootstrapFence> fences)
    {
        var options = _options.CurrentValue;
        if ((long)entries.Count + checkpoints.Count + fences.Count > (long)options.MaxStateEntries + options.MaxExpiredVersionEntries)
            throw new RuntimeStateCapacityException("journal-compaction-memory");
        // Count serialized bytes including expired records while reducing the historical journal.
        var bytes = entries.Values.Sum(m => JsonSerializer.SerializeToUtf8Bytes(m, JsonOptions).LongLength)
            + checkpoints.Values.Sum(m => JsonSerializer.SerializeToUtf8Bytes(m, JsonOptions).LongLength)
            + fences.Values.Sum(m => JsonSerializer.SerializeToUtf8Bytes(m, JsonOptions).LongLength);
        if (bytes > options.MaxStateBytes + options.MaxExpiredVersionBytes)
            throw new RuntimeStateCapacityException("journal-compaction-memory");
        return bytes;
    }
}
