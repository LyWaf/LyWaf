using System.Text.Json;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateReplicationQueueTests
{
    private static RuntimeStateReplicationQueuePool Pool(int entries = 20, long bytes = 100_000, int mutationBytes = 10_000)
        => new(entries, bytes, entries / 2, bytes / 2, mutationBytes);
    private static RuntimeStateMutation Mutation(string domain, string key, long sequence = 1, string value = "x", string origin = "source")
        => new(domain, key, origin, sequence, DateTimeOffset.UnixEpoch, null, false, JsonSerializer.SerializeToElement(value));

    [Fact]
    public void OrdinarySaturationCannotConsumeHighPriorityReserve()
    {
        var pool = Pool();
        var queue = pool.CreateQueue(20, 100_000);
        for (var i = 0; i < 10; i++) Assert.True(queue.TryWrite(Mutation("ordinary", i.ToString())));
        for (var i = 10; i < 100; i++) Assert.False(queue.TryWrite(Mutation("ordinary", i.ToString())));
        Assert.True(queue.TryWrite(Mutation("cc.access", "count")));
        Assert.True(queue.TryWrite(Mutation("cc.triggered", "penalty")));
        Assert.True(queue.Reader.TryRead(out var first));
        Assert.Equal("cc.access", first.Domain);
        Assert.True(queue.Reader.TryRead(out var second));
        Assert.Equal("cc.triggered", second.Domain);
        Assert.Equal(12, pool.GetSnapshot().Entries);
        queue.ReleaseReadBatch();
        Assert.Equal(10, pool.GetSnapshot().Entries);
        Assert.Equal(90, pool.GetSnapshot().CapacityDropped);
    }

    [Fact]
    public void ByteReserveProtectsCcEvenBeforeOrdinaryEntryLimitIsReached()
    {
        var pool = Pool(entries: 100, bytes: 3000);
        var queue = pool.CreateQueue(100, 3000);
        var accepted = 0;
        for (var i = 0; i < 20; i++)
            if (queue.TryWrite(Mutation("ordinary", i.ToString(), value: new string('x', 500)))) accepted++;
        Assert.InRange(accepted, 1, 3);
        Assert.True(pool.GetSnapshot().OrdinaryBytes <= 1500);
        Assert.True(queue.TryWrite(Mutation("cc.access", "counter")));
        Assert.True(queue.TryWrite(Mutation("cc.triggered", "penalty")));
        Assert.True(pool.GetSnapshot().Bytes <= 3000);
        queue.Complete();
        Assert.Equal(0, pool.GetSnapshot().Bytes);
    }

    [Fact]
    public void ReplacementRejectedBySharedBudgetKeepsPreviouslyAcceptedItem()
    {
        var pool = Pool(entries: 20, bytes: 1000);
        var queue = pool.CreateQueue(20, 1000);
        Assert.True(queue.TryWrite(Mutation("cc.triggered", "key", 1)));
        var snapshot = pool.GetSnapshot();
        Assert.False(queue.TryWrite(Mutation("cc.triggered", "key", 2, new string('x', 1000))));
        Assert.Equal(snapshot.Bytes, pool.GetSnapshot().Bytes);
        Assert.Equal(1, pool.GetSnapshot().CapacityDropped);
        Assert.True(queue.Reader.TryRead(out var item));
        Assert.Equal(1, item.Sequence);
        queue.ReleaseReadBatch();
        Assert.Equal(0, pool.GetSnapshot().Bytes);
    }

    [Fact]
    public void AllPeerQueuesShareByteAndEntryBudgetIncludingActiveSends()
    {
        var pool = Pool(entries: 8, bytes: 3000);
        var queues = Enumerable.Range(0, 20).Select(_ => pool.CreateQueue(8, 3000)).ToArray();
        var accepted = 0;
        foreach (var queue in queues)
            if (queue.TryWrite(Mutation("cc.triggered", "key", value: new string('x', 300)))) accepted++;
        Assert.InRange(accepted, 1, 7);
        Assert.True(pool.GetSnapshot().Bytes <= 3000);
        foreach (var queue in queues) queue.Reader.TryRead(out _);
        Assert.Equal(accepted, pool.GetSnapshot().Entries);
        Assert.False(queues[^1].TryWrite(Mutation("cc.triggered", "new", value: new string('x', 300))));
        foreach (var queue in queues) queue.ReleaseReadBatch();
        Assert.Equal(0, pool.GetSnapshot().Bytes);
        Assert.Equal(0, pool.GetSnapshot().Entries);
    }

    [Fact]
    public void RepeatedCountersCoalesceComponentMaximaNotLastArrivalOrSum()
    {
        var pool = Pool();
        var queue = pool.CreateQueue(20, 100_000);
        var latest = Mutation("cc.access", "window", 20) with
        { Value = null, CounterComponents = new Dictionary<string, long> { ["a"] = 12 } };
        Assert.True(queue.TryWrite(latest));
        Assert.True(queue.TryWrite(latest with
        { Sequence = 19, CounterComponents = new Dictionary<string, long> { ["a"] = 10, ["b"] = 7 } }));
        for (var i = 0; i < 1000; i++) Assert.True(queue.TryWrite(latest));
        Assert.Equal(1, pool.GetSnapshot().Entries);
        Assert.Equal(1001, pool.GetSnapshot().Coalesced);
        Assert.Equal(0, pool.GetSnapshot().Dropped);
        Assert.True(queue.Reader.TryRead(out var mutation));
        Assert.Equal(20, mutation.Sequence);
        Assert.Equal(19, mutation.CounterComponents!.Values.Sum());
        queue.ReleaseReadBatch();
        Assert.Equal(0, pool.GetSnapshot().Bytes);
    }

    [Fact]
    public void PenaltyTombstoneWinsAndDifferentOriginsNeverLoseAuthorizationIdentity()
    {
        var pool = Pool();
        var queue = pool.CreateQueue(20, 100_000);
        var original = Mutation("cc.triggered", "key", 1);
        Assert.True(queue.TryWrite(original));
        Assert.True(queue.TryWrite(original with { Sequence = 3, Deleted = true, Value = null }));
        Assert.True(queue.TryWrite(original with { Sequence = 2 }));
        Assert.True(queue.TryWrite(original with { NodeId = "another-source", Sequence = 100 }));
        Assert.Equal(2, pool.GetSnapshot().Entries);
        Assert.True(queue.Reader.TryRead(out var mutation));
        Assert.True(mutation.Deleted);
        Assert.Equal("source", mutation.NodeId);
        Assert.True(queue.Reader.TryRead(out mutation));
        Assert.Equal("another-source", mutation.NodeId);
        queue.ReleaseReadBatch();
    }

    [Fact]
    public void OversizedReplacementKeepsOldValueAndItsBudget()
    {
        var pool = Pool(mutationBytes: 1000);
        var queue = pool.CreateQueue(20, 100_000);
        Assert.True(queue.TryWrite(Mutation("cc.triggered", "key")));
        var bytes = pool.GetSnapshot().Bytes;
        Assert.False(queue.TryWrite(Mutation("cc.triggered", "key", 2, new string('x', 2000))));
        Assert.Equal(bytes, pool.GetSnapshot().Bytes);
        Assert.Equal(1, pool.GetSnapshot().OversizeDropped);
        Assert.Equal(0, pool.GetSnapshot().Coalesced);
        Assert.True(queue.Reader.TryRead(out var mutation));
        Assert.Equal(1, mutation.Sequence);
        queue.ReleaseReadBatch();
    }

    [Fact]
    public void ReplacingLargerAndSmallerValuesChargesOnlyDelta()
    {
        var pool = Pool();
        var queue = pool.CreateQueue(20, 100_000);
        var small = Mutation("cc.triggered", "key");
        queue.TryWrite(small);
        var initial = pool.GetSnapshot().Bytes;
        Assert.True(queue.TryWrite(small with { Sequence = 2, Value = JsonSerializer.SerializeToElement(new string('x', 1000)) }));
        Assert.True(pool.GetSnapshot().Bytes > initial);
        Assert.True(queue.TryWrite(small with { Sequence = 3 }));
        Assert.Equal(initial, pool.GetSnapshot().Bytes);
        queue.Complete();
        Assert.Equal(0, pool.GetSnapshot().Bytes);
    }

    [Fact]
    public async Task BatchPeekIsStableAcrossConcurrentUpdateAndPriorityArrival()
    {
        var pool = Pool();
        var queue = pool.CreateQueue(20, 100_000);
        queue.TryWrite(Mutation("cc.access", "key", 1));
        Assert.True(queue.Reader.TryPeek(out var peeked));
        await WhileGateHeld(pool, () => Assert.True(queue.TryWrite(Mutation("cc.access", "key", 2))));
        Assert.True(queue.Reader.TryRead(out var read));
        Assert.Same(peeked, read);
        Assert.Equal(1, read.Sequence);
        Assert.True(queue.Reader.TryRead(out read));
        Assert.Equal(2, read.Sequence);
        queue.ReleaseReadBatch();
        Assert.Equal(0, pool.GetSnapshot().Entries);
    }

    [Fact]
    public async Task ParallelSaturationNeverExceedsGlobalBudgetAndReclaimsEverything()
    {
        var pool = Pool(entries: 100, bytes: 20_000);
        var queues = Enumerable.Range(0, 8).Select(_ => pool.CreateQueue(20, 5000)).ToArray();
        await Task.Run(() => Parallel.For(0, 20_000, i =>
        {
            queues[i % queues.Length].TryWrite(Mutation(i % 2 == 0 ? "cc.triggered" : "ordinary", (i % 100).ToString(), i));
        })).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = pool.GetSnapshot();
        Assert.InRange(snapshot.Entries, 1, 100);
        Assert.InRange(snapshot.Bytes, 1, 20_000);
        Assert.InRange(snapshot.OrdinaryEntries, 0, 50);
        Assert.InRange(snapshot.OrdinaryBytes, 0, 10_000);
        Assert.True(snapshot.Dropped > 0);
        foreach (var queue in queues) queue.Complete();
        Assert.Equal(0, pool.GetSnapshot().Entries);
        Assert.Equal(0, pool.GetSnapshot().Bytes);
    }

    [Fact]
    public async Task RequestProducerDoesNotWaitForBusyAccountingGate()
    {
        var pool = Pool();
        var queue = pool.CreateQueue(20, 100_000);
        var gate = typeof(RuntimeStateReplicationQueuePool)
            .GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pool)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (gate) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(await Task.Run(() => queue.TryWrite(Mutation("cc.triggered", "key"))).WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { release.Set(); await holder; }
        Assert.Equal(0, pool.GetSnapshot().ContentionDropped);
        Assert.Equal(1, pool.GetSnapshot().ContentionBuffered);
        Assert.Equal(1, pool.GetSnapshot().Entries);
        Assert.True(queue.Reader.TryRead(out var accepted));
        Assert.Equal("key", accepted.Key);
        queue.ReleaseReadBatch();
        Assert.Equal(0, pool.GetSnapshot().Entries);
    }

    [Fact]
    public async Task ContendedStagingSharesAllBudgetsAndKeepsCcReserve()
    {
        var pool = Pool(entries: 100, bytes: 20_000);
        var queues = Enumerable.Range(0, 20).Select(_ => pool.CreateQueue(20, 5000)).ToArray();
        await WhileGateHeld(pool, () =>
        {
            Parallel.For(0, 2000, i => queues[i % queues.Length].TryWrite(Mutation("ordinary", i.ToString(), value: new string('x', 100))));
            Assert.True(queues[0].TryWrite(Mutation("cc.triggered", "reserved")));
        });
        var snapshot = pool.GetSnapshot();
        Assert.InRange(snapshot.Entries, 1, 100);
        Assert.InRange(snapshot.Bytes, 1, 20_000);
        Assert.InRange(snapshot.OrdinaryEntries, 0, 50);
        Assert.InRange(snapshot.OrdinaryBytes, 0, 10_000);
        Assert.Equal(0, snapshot.ContentionDropped);
        Assert.True(snapshot.ContentionBuffered > 0);
        Assert.True(queues[0].Reader.TryRead(out var first));
        Assert.Equal("cc.triggered", first.Domain);
        queues[0].ReleaseReadBatch();
        foreach (var queue in queues) queue.Complete();
        Assert.Equal(0, pool.GetSnapshot().Entries);
        Assert.Equal(0, pool.GetSnapshot().Bytes);
    }

    [Fact]
    public async Task DeferredCoalescingPreservesCounterMaximaOriginsAndAbsoluteExpiry()
    {
        var pool = Pool();
        var queue = pool.CreateQueue(20, 100_000);
        var mutation = Mutation("cc.access", "key", 20) with
        { Value = null, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1), CounterComponents = new Dictionary<string, long> { ["a"] = 12 } };
        await WhileGateHeld(pool, () =>
        {
            Assert.True(queue.TryWrite(mutation));
            Assert.True(queue.TryWrite(mutation with { Sequence = 19, CounterComponents = new Dictionary<string, long> { ["a"] = 10, ["b"] = 7 } }));
            Assert.True(queue.TryWrite(mutation with { NodeId = "other" }));
        });
        Assert.Equal(3, pool.GetSnapshot().Entries);
        Assert.True(queue.Reader.TryRead(out var first));
        Assert.Equal(19, first.CounterComponents!.Values.Sum());
        Assert.Equal(mutation.ExpiresAt, first.ExpiresAt);
        Assert.Equal(20, first.Sequence);
        Assert.Equal("source", first.NodeId);
        Assert.True(queue.Reader.TryRead(out var second));
        Assert.Equal("other", second.NodeId);
        Assert.Equal(1, pool.GetSnapshot().Coalesced);
        queue.ReleaseReadBatch();
        Assert.Equal(0, pool.GetSnapshot().Bytes);
    }

    [Fact]
    public async Task ClosingDuringConcurrentAdmissionNeverLeaksReservations()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var pool = Pool();
            var queue = pool.CreateQueue(20, 100_000);
            await Task.WhenAll(Task.Run(() => Parallel.For(0, 500, i => queue.TryWrite(Mutation("cc.access", i.ToString())))),
                Task.Run(queue.Complete));
            queue.Complete();
            Assert.False(queue.Reader.TryRead(out _));
            Assert.Equal(0, pool.GetSnapshot().Entries);
            Assert.Equal(0, pool.GetSnapshot().Bytes);
        }
    }

    private static async Task WhileGateHeld(RuntimeStateReplicationQueuePool pool, Action action)
    {
        var gate = typeof(RuntimeStateReplicationQueuePool)
            .GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pool)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (gate) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); } });
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); action(); }
        finally { release.Set(); await holder; }
    }

    [Fact]
    public async Task CompletionAndCancellationWakeWaitersWithoutBudgetLeaks()
    {
        var pool = Pool();
        var queue = pool.CreateQueue(20, 100_000);
        var waiting = queue.Reader.WaitToReadAsync().AsTask();
        Assert.False(waiting.IsCompleted);
        queue.Complete();
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(queue.TryWrite(Mutation("cc.triggered", "closed")));
        Assert.Equal(1, pool.GetSnapshot().ClosedDropped);
        var another = pool.CreateQueue(20, 100_000);
        using var stop = new CancellationTokenSource();
        var canceled = another.Reader.WaitToReadAsync(stop.Token).AsTask();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
    }
}
