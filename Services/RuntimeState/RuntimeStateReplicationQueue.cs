using System.Text.Json;
using System.Threading.Channels;

namespace LyWaf.Services.RuntimeState;

/// <summary>
/// One budget shared by ingress and all peer queues. Bytes mean serialized mutation payload bytes,
/// not CLR heap size. Dequeued items remain charged until the consumer releases its batch.
/// </summary>
internal sealed class RuntimeStateReplicationQueuePool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly int _normalEntries;
    private readonly long _normalBytes;
    private readonly int _maxMutationBytes;
    private readonly Budget _budget;
    private long _contentionBuffered;
    private long _coalesced, _capacityDropped, _oversizeDropped, _closedDropped;

    internal RuntimeStateReplicationQueuePool(int maxEntries, long maxBytes, int reservedHighEntries,
        long reservedHighBytes, int maxMutationBytes)
    {
        if (maxEntries < 2 || maxBytes < 2 || reservedHighEntries < 1 || reservedHighEntries >= maxEntries ||
            reservedHighBytes < 1 || reservedHighBytes >= maxBytes || maxMutationBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _normalEntries = maxEntries - reservedHighEntries;
        _normalBytes = maxBytes - reservedHighBytes;
        _maxMutationBytes = maxMutationBytes;
        _budget = new(maxEntries, maxBytes, _normalEntries, _normalBytes);
    }

    internal Queue CreateQueue(int maxEntries, long maxBytes)
    {
        if (maxEntries < 2 || maxBytes < 2) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        return new Queue(this, maxEntries, maxBytes);
    }

    internal Snapshot GetSnapshot()
    {
        lock (_gate) return new(_budget.Entries, _budget.Bytes, _budget.OrdinaryEntries, _budget.OrdinaryBytes,
            Interlocked.Read(ref _coalesced), Interlocked.Read(ref _capacityDropped),
            Interlocked.Read(ref _oversizeDropped), 0,
            Interlocked.Read(ref _closedDropped), Interlocked.Read(ref _contentionBuffered));
    }

    internal sealed record Snapshot(int Entries, long Bytes, int OrdinaryEntries, long OrdinaryBytes,
        long Coalesced, long CapacityDropped, long OversizeDropped, long ContentionDropped, long ClosedDropped,
        long ContentionBuffered)
    {
        // Preserve overload-drop semantics; shutdown/removal discards are reported separately.
        public long Dropped => CapacityDropped + OversizeDropped + ContentionDropped;
    }

    private static bool IsHigh(RuntimeStateMutation mutation) => mutation.Domain is
        "cc.access" or "cc.attack" or "cc.error" or "cc.triggered" or "cc.ban.logged";

    private static int CompareVersion(RuntimeStateMutation left, RuntimeStateMutation right)
    {
        var order = left.Sequence.CompareTo(right.Sequence);
        return order != 0 ? order : string.CompareOrdinal(left.NodeId, right.NodeId);
    }

    private static RuntimeStateMutation Merge(RuntimeStateMutation current, RuntimeStateMutation incoming)
    {
        var latest = CompareVersion(incoming, current) > 0 ? incoming : current;
        if (current.CounterComponents is null && incoming.CounterComponents is null) return latest;
        var components = new Dictionary<string, long>(current.CounterComponents ?? new Dictionary<string, long>(), StringComparer.Ordinal);
        foreach (var pair in incoming.CounterComponents ?? new Dictionary<string, long>())
            components[pair.Key] = Math.Max(components.GetValueOrDefault(pair.Key), pair.Value);
        return latest with { CounterComponents = components };
    }

    // Counting through a bounded sink avoids allocating an unbounded serialization byte array on producers.
    private int Measure(RuntimeStateMutation mutation)
    {
        using var stream = new CountingStream(_maxMutationBytes);
        JsonSerializer.Serialize(stream, mutation, JsonOptions);
        return stream.Count;
    }

    internal sealed class Queue
    {
        private readonly RuntimeStateReplicationQueuePool _pool;
        private readonly int _maxEntries;
        private readonly long _maxBytes;
        private readonly LinkedList<Item> _high = new(), _ordinary = new();
        // Origin is part of the key: merging origins would destroy per-peer forwarded-source authorization.
        private readonly Dictionary<(string Domain, string Key, string Origin), LinkedListNode<Item>> _keys = new();
        private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false });
        private readonly List<Item> _leased = [];
        private Item? _peeked;
        private readonly Budget _budget;
        private readonly Channel<Item> _stagedHigh, _stagedOrdinary;
        private volatile bool _completed;

        internal Queue(RuntimeStateReplicationQueuePool pool, int maxEntries, long maxBytes)
        {
            _pool = pool;
            _maxEntries = maxEntries;
            _maxBytes = maxBytes;
            _budget = new(maxEntries, maxBytes, maxEntries / 2, maxBytes / 2);
            _stagedHigh = CreateStaging(maxEntries);
            _stagedOrdinary = CreateStaging(maxEntries);
            Reader = new QueueReader(this);
        }

        private static Channel<Item> CreateStaging(int capacity) => Channel.CreateBounded<Item>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });

        internal ChannelReader<RuntimeStateMutation> Reader { get; }

        internal bool TryWrite(RuntimeStateMutation mutation, bool background = false)
        {
            int size;
            try { size = _pool.Measure(mutation); }
            catch (PayloadTooLargeException)
            {
                Interlocked.Increment(ref _pool._oversizeDropped);
                return false;
            }
            // Request producers never wait. Background fan-out/repair may wait for this memory-only gate.
            if (background) Monitor.Enter(_pool._gate);
            else if (!Monitor.TryEnter(_pool._gate))
            {
                return Stage(mutation, size);
            }
            try
            {
                if (_completed) { Interlocked.Increment(ref _pool._closedDropped); return false; }
                if (background) DrainStaged();
                var high = IsHigh(mutation);
                var key = (mutation.Domain, mutation.Key, mutation.NodeId);
                if (high && _keys.TryGetValue(key, out var pending))
                {
                    var merged = Merge(pending.Value.Mutation, mutation);
                    try { size = _pool.Measure(merged); }
                    catch (PayloadTooLargeException)
                    { Interlocked.Increment(ref _pool._oversizeDropped); return false; }
                    var delta = size - pending.Value.Bytes;
                    if (delta > 0 && !Reserve(0, delta, high)) { Interlocked.Increment(ref _pool._capacityDropped); return false; }
                    if (delta < 0) Release(0, -delta, high);
                    pending.Value = new(merged, size, high);
                    Interlocked.Increment(ref _pool._coalesced);
                    return true;
                }
                if (!Reserve(1, size, high)) { Interlocked.Increment(ref _pool._capacityDropped); return false; }
                var item = new Item(mutation, size, high);
                var node = (high ? _high : _ordinary).AddLast(item);
                if (high) _keys.Add(key, node);
                _signal.Writer.TryWrite(true);
                return true;
            }
            finally { Monitor.Exit(_pool._gate); }
        }

        private bool Reserve(int entries, long bytes, bool high)
        {
            if (!_pool._budget.TryReserve(entries, bytes, high)) return false;
            if (_budget.TryReserve(entries, bytes, high)) return true;
            _pool._budget.Release(entries, bytes, high);
            return false;
        }

        private void Release(int entries, long bytes, bool high)
        {
            _budget.Release(entries, bytes, high);
            _pool._budget.Release(entries, bytes, high);
        }

        private bool Stage(RuntimeStateMutation mutation, int size)
        {
            if (_completed) { Interlocked.Increment(ref _pool._closedDropped); return false; }
            var high = IsHigh(mutation);
            if (!Reserve(1, size, high)) { Interlocked.Increment(ref _pool._capacityDropped); return false; }
            var writer = (high ? _stagedHigh : _stagedOrdinary).Writer;
            if (!writer.TryWrite(new(mutation, size, high)))
            {
                Release(1, size, high);
                Interlocked.Increment(ref _pool._closedDropped);
                return false;
            }
            Interlocked.Increment(ref _pool._contentionBuffered);
            _signal.Writer.TryWrite(true);
            return true;
        }

        // Staged items are already charged. Transfer without releasing/reacquiring their reservation.
        private void DrainStaged()
        {
            Drain(_stagedHigh.Reader);
            Drain(_stagedOrdinary.Reader);
            void Drain(ChannelReader<Item> reader)
            {
                // Bound work even if producers keep staging while this consumer holds the gate.
                for (var i = 0; i < _maxEntries && reader.TryRead(out var item); i++)
                {
                    var key = (item.Mutation.Domain, item.Mutation.Key, item.Mutation.NodeId);
                    if (item.High && _keys.TryGetValue(key, out var pending))
                    {
                        var merged = Merge(pending.Value.Mutation, item.Mutation);
                        try
                        {
                            var size = _pool.Measure(merged);
                            var delta = size - pending.Value.Bytes - item.Bytes;
                            if (delta <= 0 || Reserve(0, delta, true))
                            {
                                Release(1, Math.Max(0, -delta), true);
                                pending.Value = new(merged, size, true);
                                Interlocked.Increment(ref _pool._coalesced);
                                continue;
                            }
                        }
                        catch (PayloadTooLargeException) { }
                        // Both were accepted: if they cannot be merged, keep both rather than lose either.
                    }
                    var node = (item.High ? _high : _ordinary).AddLast(item);
                    if (item.High) _keys[key] = node;
                }
            }
        }

        private Item? TakePending()
        {
            var list = _high.Count != 0 ? _high : _ordinary;
            if (list.First is not { } node) return null;
            list.RemoveFirst();
            var key = (node.Value.Mutation.Domain, node.Value.Mutation.Key, node.Value.Mutation.NodeId);
            if (node.Value.High && _keys.TryGetValue(key, out var indexed) && ReferenceEquals(indexed, node)) _keys.Remove(key);
            return node.Value;
        }

        /// <summary>Call in finally after fan-out/send, including failed sends and rejected authorization.</summary>
        internal void ReleaseReadBatch()
        {
            lock (_pool._gate)
            {
                foreach (var item in _leased) Release(1, item.Bytes, item.High);
                _leased.Clear();
            }
        }

        /// <summary>Discard pending data on cancellation/removal; active sends retain their lease until finally.</summary>
        internal void Complete()
        {
            lock (_pool._gate)
            {
                _completed = true;
                _stagedHigh.Writer.TryComplete();
                _stagedOrdinary.Writer.TryComplete();
                foreach (var reader in new[] { _stagedHigh.Reader, _stagedOrdinary.Reader })
                    while (reader.TryRead(out var staged))
                    {
                        Release(1, staged.Bytes, staged.High);
                        Interlocked.Increment(ref _pool._closedDropped);
                    }
                while (TakePending() is { } item)
                {
                    Release(1, item.Bytes, item.High);
                    Interlocked.Increment(ref _pool._closedDropped);
                }
                if (_peeked is { } peeked)
                {
                    Release(1, peeked.Bytes, peeked.High);
                    _peeked = null;
                    Interlocked.Increment(ref _pool._closedDropped);
                }
                _signal.Writer.TryComplete();
            }
        }

        private sealed record Item(RuntimeStateMutation Mutation, int Bytes, bool High);

        private sealed class QueueReader(Queue queue) : ChannelReader<RuntimeStateMutation>
        {
            public override bool TryRead(out RuntimeStateMutation item)
            {
                lock (queue._pool._gate)
                {
                    queue.DrainStaged();
                    var next = queue._peeked ?? queue.TakePending();
                    queue._peeked = null;
                    if (next is null) { item = null!; return false; }
                    queue._leased.Add(next);
                    item = next.Mutation;
                    return true;
                }
            }

            // RuntimeStateBatch peeks then serializes before reading. Pin the exact value across producer updates.
            public override bool TryPeek(out RuntimeStateMutation item)
            {
                lock (queue._pool._gate)
                {
                    queue.DrainStaged();
                    queue._peeked ??= queue.TakePending();
                    item = queue._peeked?.Mutation!;
                    return queue._peeked is not null;
                }
            }

            public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (queue._pool._gate)
                    {
                        queue.DrainStaged();
                        if (queue._peeked is not null || queue._high.Count != 0 || queue._ordinary.Count != 0) return true;
                        if (queue._completed) return false;
                    }
                    try { await queue._signal.Reader.ReadAsync(cancellationToken); }
                    catch (ChannelClosedException) { return false; }
                }
            }
        }
    }

    // Lock-free admission reservations include staged, pending, pinned and in-flight items.
    // Partial reservations are conservative and rolled back on failure; no counter exceeds its ceiling.
    private sealed class Budget(int maxEntries, long maxBytes, int normalEntries, long normalBytes)
    {
        private long _entries, _bytes, _ordinaryEntries, _ordinaryBytes;
        internal int Entries => (int)Interlocked.Read(ref _entries);
        internal long Bytes => Interlocked.Read(ref _bytes);
        internal int OrdinaryEntries => (int)Interlocked.Read(ref _ordinaryEntries);
        internal long OrdinaryBytes => Interlocked.Read(ref _ordinaryBytes);

        internal bool TryReserve(int entries, long bytes, bool high)
        {
            // Reserve the ordinary allowance first: speculative ordinary admission must not occupy CC reserve.
            if (!high)
            {
                if (!Add(ref _ordinaryEntries, entries, normalEntries)) return false;
                if (!Add(ref _ordinaryBytes, bytes, normalBytes))
                { Interlocked.Add(ref _ordinaryEntries, -entries); return false; }
            }
            if (Add(ref _entries, entries, maxEntries))
            {
                if (Add(ref _bytes, bytes, maxBytes)) return true;
                Interlocked.Add(ref _entries, -entries);
            }
            if (!high) ReleaseOrdinary(entries, bytes);
            return false;
        }

        internal void Release(int entries, long bytes, bool high)
        {
            Interlocked.Add(ref _bytes, -bytes);
            Interlocked.Add(ref _entries, -entries);
            if (!high) ReleaseOrdinary(entries, bytes);
        }

        private void ReleaseOrdinary(int entries, long bytes)
        {
            Interlocked.Add(ref _ordinaryBytes, -bytes);
            Interlocked.Add(ref _ordinaryEntries, -entries);
        }

        private static bool Add(ref long target, long amount, long limit)
        {
            if (amount == 0) return true;
            var current = Interlocked.Read(ref target);
            while (amount <= limit - current)
            {
                var observed = Interlocked.CompareExchange(ref target, current + amount, current);
                if (observed == current) return true;
                current = observed;
            }
            return false;
        }
    }

    private sealed class PayloadTooLargeException : IOException;

    private sealed class CountingStream(int limit) : Stream
    {
        internal int Count { get; private set; }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - Count) throw new PayloadTooLargeException();
            Count += buffer.Length;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
