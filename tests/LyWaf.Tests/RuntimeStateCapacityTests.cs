using System.Text.Json;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateCapacityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runtime-capacity-" + Guid.NewGuid().ToString("N"));
    private StaticOptionsMonitor<RuntimeStateOptions> Options(string node = "target") => new(new()
    {
        Enabled = true, NodeId = node, StateDirectory = Path.Combine(_root, node),
        Peers = new() { [node == "target" ? "source" : "target"] = new()
        {
            DurableSyncEnabled = true, ProtocolVersion = 2,
            Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true },
            Receive = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true }
        } }
    });
    private static RuntimeStateMutation Mutation(string key, long sequence = 1, bool expired = false) => new("test", key, "source",
        sequence, DateTimeOffset.UtcNow, expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : null, false, JsonSerializer.SerializeToElement("value"));
    private static RuntimeStateSyncBatch Batch(params RuntimeStateMutation[] mutations)
    {
        var batch = new RuntimeStateSyncBatch("cluster", "source", "target", Guid.NewGuid().ToString("N"), 1, mutations, "");
        return batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };
    }

    [Fact]
    public async Task ConcurrentPermanentKeysRespectCountAndSerializedBytes()
    {
        var options = Options();
        options.CurrentValue.MaxStateEntries = 32;
        options.CurrentValue.MaxStateBytes = 16 * 1024;
        using var store = new RuntimeStateStore(options);
        var accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 1000).Select(i => Task.Run(() =>
        {
            try { store.Set("test", "permanent-" + i, new string('x', 100)); Interlocked.Increment(ref accepted); }
            catch (RuntimeStateCapacityException) { }
        })));
        Assert.Equal(32, accepted);
        Assert.Equal(32, store.GetStatus().RetainedEntries);
        Assert.InRange(store.GetStatus().StateBytes, 1, options.CurrentValue.MaxStateBytes);
        Assert.Equal("state-memory", store.GetStatus().CapacityError);
        Assert.Throws<RuntimeStateCapacityException>(() => store.TryClaim("test", "another", TimeSpan.FromMinutes(1)));
        Assert.Throws<RuntimeStateCapacityException>(() => store.IncrementWindow("test", "counter", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
        Assert.Throws<RuntimeStateCapacityException>(() => store.Merge(Mutation("remote")));
        var key = store.GetSnapshots()[0].Key;
        store.Set("test", key, "replacement");
        Assert.Equal(32, store.GetStatus().RetainedEntries);
    }

    [Fact]
    public void OversizedReplacementLeavesPriorValueAndChargeUnchanged()
    {
        var options = Options();
        options.CurrentValue.MaxStateBytes = 500;
        using var store = new RuntimeStateStore(options);
        store.Set("test", "kept", "before");
        var bytes = store.GetStatus().StateBytes;
        Assert.Throws<RuntimeStateCapacityException>(() => store.Set("test", "kept", new string('x', 1000)));
        Assert.True(store.TryGet<string>("test", "kept", out var value));
        Assert.Equal("before", value);
        Assert.Equal(bytes, store.GetStatus().StateBytes);
    }

    [Fact]
    public async Task QueueByteSaturationCannotCreateDurableAck()
    {
        var options = Options();
        options.CurrentValue.MaxJournalQueueBytes = 1;
        using var store = new RuntimeStateStore(options);
        store.Set("test", "local", "value");
        Assert.Equal(1, store.GetStatus().DroppedMutations);
        Assert.Equal(0, store.GetStatus().JournalQueueBytes);
        var receiver = new RuntimeStateSyncReceiver(options, store);
        await Assert.ThrowsAsync<RuntimeStateCapacityException>(() => receiver.AcceptAsync(Batch(Mutation("remote")), default));
        Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
        Assert.Equal(0, store.GetStatus().QueueDepth);
    }

    [Fact]
    public async Task JournalHardLimitStopsGrowthAndDoesNotConfirmSyncFence()
    {
        var options = Options();
        options.CurrentValue.MaxJournalDiskBytes = 500;
        using var store = new RuntimeStateStore(options);
        await store.StartAsync(default);
        var receiver = new RuntimeStateSyncReceiver(options, store);
        await Assert.ThrowsAnyAsync<IOException>(() => receiver.AcceptAsync(Batch(Mutation("one"), Mutation("two"), Mutation("three")), default)
            .WaitAsync(TimeSpan.FromSeconds(10)));
        await store.StopAsync(default);
        Assert.False(store.SyncJournalHealthy);
        Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
        Assert.InRange(new FileInfo(Path.Combine(options.CurrentValue.StateDirectory, "runtime-state.jsonl")).Length, 1, 500);
        Assert.Throws<RuntimeStateCapacityException>(() => store.Set("test", "after-full", "denied"));
        Assert.Equal(0, store.GetStatus().JournalQueueBytes);
        options.CurrentValue.MaxJournalDiskBytes = 10000;
        using var recovered = new RuntimeStateStore(options);
        Assert.Null(recovered.GetDurableSyncCheckpoint("cluster", "source", "target"));
    }

    [Fact]
    public async Task ExpiredVersionBudgetRefusesCompactionWithoutDroppingFences()
    {
        var options = Options();
        options.CurrentValue.MaxExpiredVersionEntries = 2;
        using var store = new RuntimeStateStore(options);
        var path = Path.Combine(options.CurrentValue.StateDirectory, "runtime-state.jsonl");
        File.WriteAllLines(path, new[] { Mutation("old0", 10, true), Mutation("old1", 11, true) }
            .Select(m => JsonSerializer.Serialize(m, RuntimeStateSyncProtocol.Json)));
        Assert.True(await store.CompactJournalAsync(path, default));
        Assert.Equal(2, store.RetiredJournalVersionCount);
        File.AppendAllText(path, JsonSerializer.Serialize(Mutation("old2", 12, true), RuntimeStateSyncProtocol.Json) + "\n");
        var before = File.ReadAllBytes(path);
        Assert.False(await store.CompactJournalAsync(path, default));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(2, store.RetiredJournalVersionCount);
        Assert.Equal("expired-versions", store.GetStatus().CapacityError);
        store.Merge(Mutation("old0", 1));
        await store.StartAsync(default);
        await store.StopAsync(default);
        using var recovered = new RuntimeStateStore(options);
        Assert.False(recovered.TryGet<string>("test", "old0", out _));
    }

    [Fact]
    public async Task HighCardinalityExpiryCyclesRemainBoundedAndEventuallyRefuseWrites()
    {
        var options = Options();
        options.CurrentValue.MaxExpiredVersionEntries = 8;
        options.CurrentValue.MaxJournalBytes = 256;
        options.CurrentValue.MaxJournalDiskBytes = 4096;
        using var store = new RuntimeStateStore(options);
        await store.StartAsync(default);
        for (var i = 0; i < 1000; i++)
        {
            try { store.Set("test", "expired-cycle-" + i, "value", TimeSpan.FromSeconds(-1)); }
            catch (RuntimeStateCapacityException) { break; }
            store.ExportMutations();
        }
        await store.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(15));
        await store.StopAsync(default);
        Assert.True(store.GetStatus().PersistenceReadOnly);
        Assert.InRange(store.RetiredJournalVersionCount, 0, 8);
        Assert.InRange(new FileInfo(Path.Combine(options.CurrentValue.StateDirectory, "runtime-state.jsonl")).Length, 1, 4096);
        Assert.Throws<RuntimeStateCapacityException>(() => store.Set("test", "must-refuse", "new"));
        Assert.Equal(0, store.GetStatus().JournalQueueBytes);
    }

    [Fact]
    public async Task ExpiredVersionByteBudgetPreservesOriginalJournal()
    {
        var options = Options();
        options.CurrentValue.MaxExpiredVersionBytes = 1;
        using var store = new RuntimeStateStore(options);
        var path = Path.Combine(options.CurrentValue.StateDirectory, "runtime-state.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(Mutation("expired", 1, true), RuntimeStateSyncProtocol.Json) + "\n");
        var original = File.ReadAllBytes(path);
        Assert.False(await store.CompactJournalAsync(path, default));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0, store.RetiredJournalVersionCount);
    }

    [Fact]
    public void CrashTemporaryCountsAgainstDirectoryBudgetAndPreservesPending()
    {
        var options = Options("source");
        options.CurrentValue.MaxDiskBytes = 8192;
        var sender = new RuntimeStateSyncSender(options);
        var batch = sender.Prepare("cluster", "source", "target", [Mutation("key")]);
        var root = options.CurrentValue.StateDirectory;
        var used = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        var orphan = Path.Combine(root, "runtime-state.jsonl.compact-crashed");
        File.WriteAllBytes(orphan, new byte[(int)(8192 - used - 32)]);
        Assert.Throws<RuntimeStateCapacityException>(() => sender.Confirm(batch,
            new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence, batch.Digest)));
        Assert.Equal(batch.Digest, sender.ReadPending("cluster", "source", "target")!.Digest);
        Assert.True(File.Exists(orphan));
        Assert.True(new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) <= 8192);
    }

    [Fact]
    public async Task ConcurrentDiskAdmissionsCannotOvercommitSharedDirectory()
    {
        var options = Options().CurrentValue;
        options.MaxDiskBytes = 8192;
        var files = new RuntimeStateSyncFiles(options.StateDirectory, options);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            try { files.Save(files.PathFor("test", "cluster", "source", "peer" + i), new { Payload = new string('x', 1000) }); }
            catch (RuntimeStateCapacityException) { }
        })));
        var actual = new DirectoryInfo(options.StateDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        Assert.InRange(actual, 1, 8192);
        Assert.InRange(Directory.GetFiles(Path.Combine(options.StateDirectory, "runtime-sync"), "*.json").Length, 1, 7);
    }

    [Fact]
    public void CallerCannotInflateStoredCounterAfterAdmission()
    {
        var options = Options();
        using var store = new RuntimeStateStore(options);
        var components = new Dictionary<string, long> { ["source"] = 1 };
        store.Merge(Mutation("counter") with { Value = null, CounterComponents = components });
        var charged = store.GetStatus().StateBytes;
        components[new string('x', 10000)] = 500;
        Assert.Equal(charged, store.GetStatus().StateBytes);
        Assert.Single(Assert.Single(store.ExportMutations()).CounterComponents!);
    }

    [Fact]
    public async Task CheckpointMetadataMustFitBeforeAnyDurableAck()
    {
        var options = Options();
        options.CurrentValue.MaxStateEntries = 1;
        using var store = new RuntimeStateStore(options);
        await store.StartAsync(default);
        try
        {
            var receiver = new RuntimeStateSyncReceiver(options, store);
            await Assert.ThrowsAsync<RuntimeStateCapacityException>(() => receiver.AcceptAsync(Batch(Mutation("only-entry")), default));
            Assert.Null(store.GetDurableSyncCheckpoint("cluster", "source", "target"));
            Assert.Equal(1, store.GetStatus().RetainedEntries);
        }
        finally { await store.StopAsync(default); }
        using var recovered = new RuntimeStateStore(options);
        Assert.Null(recovered.GetDurableSyncCheckpoint("cluster", "source", "target"));
    }

    [Fact]
    public async Task FailedEpochResetDoesNotUnderchargeOrReplaceExistingFence()
    {
        var options = Options();
        using var store = new RuntimeStateStore(options);
        await store.StartAsync(default);
        try
        {
            var initial = Batch() with { Sequence = 100000 };
            initial = initial with { Digest = RuntimeStateSyncProtocol.ComputeDigest(initial) };
            await store.MergeDurablyAsync(initial, default);
            var charged = store.GetStatus().StateBytes;
            options.CurrentValue.MaxJournalQueueBytes = 1;
            var reset = initial with { Epoch = Guid.NewGuid().ToString("N"), Sequence = 0 };
            reset = reset with { Digest = RuntimeStateSyncProtocol.ComputeDigest(reset) };
            await Assert.ThrowsAsync<RuntimeStateCapacityException>(() => store.MergeDurablyAsync(reset, default));
            Assert.True(store.GetStatus().StateBytes >= charged);
            Assert.Equal(initial.Epoch, store.GetDurableSyncCheckpoint("cluster", "source", "target")!.Epoch);
            Assert.Equal(initial.Sequence, store.GetDurableSyncCheckpoint("cluster", "source", "target")!.Sequence);
        }
        finally { await store.StopAsync(default); }
    }

    [Fact]
    public void RecoveredReleaseFenceConsumesBudgetAndIsNeverEvictedForAdmission()
    {
        var options = Options();
        options.CurrentValue.MaxStateEntries = 1;
        Directory.CreateDirectory(options.CurrentValue.StateDirectory);
        var generation = new CcPolicyGeneration(1, Guid.NewGuid().ToString("N"));
        var fence = new RuntimeStateMutation(CcPolicyGeneration.Domain, "v2:policy", "source", 1, DateTimeOffset.UtcNow,
            null, false, JsonSerializer.SerializeToElement(generation, RuntimeStateSyncProtocol.Json));
        var path = Path.Combine(options.CurrentValue.StateDirectory, "cc-policy-generations.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(fence, RuntimeStateSyncProtocol.Json) + "\n");
        var original = File.ReadAllBytes(path);
        using var store = new RuntimeStateStore(options);
        Assert.Throws<RuntimeStateCapacityException>(() => store.Set("test", "new-key", "denied"));
        Assert.Equal(generation, store.GetCcPolicyGeneration("v2:policy"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(1, store.GetStatus().RetainedEntries);
    }

    [Fact]
    public void CcPermanentFenceCannotBypassEntryBudget()
    {
        var options = Options();
        options.CurrentValue.Enabled = false;
        options.CurrentValue.MaxStateEntries = 1;
        using var store = new RuntimeStateStore(options);
        var first = store.AdvanceCcPolicyGeneration("v2:first");
        var path = Path.Combine(options.CurrentValue.StateDirectory, "cc-policy-generations.jsonl");
        var original = File.ReadAllBytes(path);
        Assert.Throws<RuntimeStateCapacityException>(() => store.AdvanceCcPolicyGeneration("v2:second"));
        Assert.Equal(first, store.GetCcPolicyGeneration("v2:first"));
        Assert.Equal(CcPolicyGeneration.Initial, store.GetCcPolicyGeneration("v2:second"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(1, store.GetStatus().RetainedEntries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CcJournalCapacityRejectionDoesNotAdvanceFence(bool totalDirectory)
    {
        var options = Options();
        options.CurrentValue.Enabled = false;
        using var store = new RuntimeStateStore(options);
        var first = store.AdvanceCcPolicyGeneration("v2:policy");
        var path = Path.Combine(options.CurrentValue.StateDirectory, "cc-policy-generations.jsonl");
        var original = File.ReadAllBytes(path);
        var charged = store.GetStatus().StateBytes;
        if (totalDirectory)
            options.CurrentValue.MaxDiskBytes = new DirectoryInfo(options.CurrentValue.StateDirectory)
                .EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) + 1;
        else options.CurrentValue.MaxJournalDiskBytes = original.LongLength;
        Assert.Throws<RuntimeStateCapacityException>(() => store.AdvanceCcPolicyGeneration("v2:policy"));
        Assert.Equal(first, store.GetCcPolicyGeneration("v2:policy"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(charged, store.GetStatus().StateBytes);
    }

    [Fact]
    public void RecoveryOverMemoryBudgetFailsClosedWithoutEditingJournal()
    {
        var options = Options();
        options.CurrentValue.MaxStateEntries = 1;
        Directory.CreateDirectory(options.CurrentValue.StateDirectory);
        var path = Path.Combine(options.CurrentValue.StateDirectory, "runtime-state.jsonl");
        File.WriteAllLines(path, new[] { Mutation("first"), Mutation("second") }.Select(m => JsonSerializer.Serialize(m, RuntimeStateSyncProtocol.Json)));
        var original = File.ReadAllBytes(path);
        Assert.Throws<RuntimeStateCapacityException>(() => new RuntimeStateStore(options));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
