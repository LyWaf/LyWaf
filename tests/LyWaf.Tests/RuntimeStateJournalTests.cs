using System.Text.Json;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "journal-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_directory, "runtime-state.jsonl");
    private RuntimeStateStore Create() => new(new StaticOptionsMonitor<RuntimeStateOptions>(new()
    { NodeId = "test", StateDirectory = _directory, MaxJournalBytes = 1024, QueueCapacity = 10000 }));

    [Fact]
    public async Task RepeatedUpdatesCompactOnlineAndRecoverLatest()
    {
        using (var store = Create())
        {
            await store.StartAsync(default);
            for (var i = 0; i < 500; i++) store.Set("test", "key", i);
            await WaitAsync(() => store.GetStatus().PersistedMutations == 500);
            await store.StopAsync(default);
            Assert.Equal(0, store.GetStatus().DroppedMutations);
        }
        Assert.True(new FileInfo(Journal).Length < 4096);
        using var recovered = Create();
        Assert.True(recovered.TryGet<int>("test", "key", out var value));
        Assert.Equal(499, value);
    }

    [Fact]
    public async Task OversizedRecoveryCompactsTombstonesAndDropsExpiredLatest()
    {
        Directory.CreateDirectory(_directory);
        var records = Enumerable.Range(1, 50).Select(i => Mutation("value", i)).ToList();
        records.Add(Mutation("deleted", 51));
        records.Add(Mutation("deleted", 52) with { Deleted = true, Value = null, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        records.Add(Mutation("expired", 53));
        records.Add(Mutation("expired", 54) with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        await File.WriteAllLinesAsync(Journal, records.Select(Serialize));
        Assert.True(new FileInfo(Journal).Length > 1024);
        using (var store = Create())
        {
            Assert.True(store.TryGet<int>("test", "value", out var value));
            Assert.Equal(50, value);
            Assert.True(await store.CompactJournalAsync(Journal, default));
        }
        using var recovered = Create();
        Assert.False(recovered.TryGet<int>("test", "expired", out _));
        Assert.False(recovered.TryGet<int>("test", "deleted", out _));
        Assert.True(Assert.Single(recovered.ExportMutations(), x => x.Key == "deleted").Deleted);
        Assert.False(recovered.Merge(Mutation("deleted", 51)));
        Assert.Equal(3, File.ReadAllLines(Journal).Length);
        recovered.Set("test", "new", 1);
        Assert.True(Assert.Single(recovered.ExportMutations(), x => x.Key == "new").Sequence > 54);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledCommitPreservesOriginalBytes(bool cancel)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllLinesAsync(Journal, Enumerable.Range(1, 30).Select(i => Serialize(Mutation("value", i))));
        var original = await File.ReadAllBytesAsync(Journal);
        using var store = Create();
        using var cts = new CancellationTokenSource();
        store.JournalBeforeReplace = () =>
        {
            if (cancel) cts.Cancel();
            else throw new IOException("Forced commit failure");
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompactJournalAsync(Journal, cts.Token));
        else Assert.False(await store.CompactJournalAsync(Journal, cts.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(Journal));
        Assert.Empty(Directory.GetFiles(_directory, "*.compact-*"));
    }

    [Fact]
    public async Task DelayedOlderQueueRecordCannotResurrectCompactedExpiry()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllLinesAsync(Journal, new[] { Serialize(Mutation("key", 20) with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }) });
        using (var store = Create())
        {
            Assert.True(await store.CompactJournalAsync(Journal, default));
            Assert.Empty(store.ExportMutations());
            Assert.True(store.Merge(Mutation("key", 19)));
            await store.StartAsync(default);
            await store.StopAsync(default);
        }
        using var recovered = Create();
        Assert.False(recovered.TryGet<int>("test", "key", out _));
    }

    [Fact]
    public async Task FailedOnlineCompactionContinuesAppendingAndRecovers()
    {
        using (var store = Create())
        {
            var attempts = 0;
            store.JournalBeforeReplace = () => { Interlocked.Increment(ref attempts); throw new IOException("Forced failure"); };
            await store.StartAsync(default);
            for (var i = 0; i < 100; i++) store.Set("test", "value", i);
            await WaitAsync(() => store.GetStatus().PersistedMutations == 100);
            await store.StopAsync(default);
            Assert.True(attempts > 0);
        }
        Assert.Equal(100, File.ReadAllLines(Journal).Length);
        using var recovered = Create();
        Assert.True(recovered.TryGet<int>("test", "value", out var value));
        Assert.Equal(99, value);
    }

    [Fact]
    public async Task CorruptRecordAbortsRewriteButRecoveryContinuesPastIt()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllLinesAsync(Journal, new[] { Serialize(Mutation("first", 1)), "{broken", Serialize(Mutation("last", 2)) });
        var original = await File.ReadAllBytesAsync(Journal);
        using var store = Create();
        Assert.Equal(2, store.GetSnapshots().Count);
        Assert.False(await store.CompactJournalAsync(Journal, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(Journal));
    }

    [Fact]
    public async Task ProducersContinueWhileCompactionIsPausedAndQueuedWritesSurvive()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using (var store = Create())
        {
            store.JournalBeforeReplace = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
            await store.StartAsync(default);
            try
            {
                for (var i = 0; i < 20; i++) store.Set("test", "initial", i);
                Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
                await Task.Run(() => Parallel.For(0, 100, i => store.Set("test", i.ToString(), i))).WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { release.Set(); }
            await WaitAsync(() => store.GetStatus().PersistedMutations == 120);
            await store.StopAsync(default);
        }
        using var recovered = Create();
        Assert.Equal(101, recovered.GetSnapshots().Count);
    }

    private static RuntimeStateMutation Mutation(string key, int sequence) => new("test", key, "test", sequence,
        DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(sequence));
    private static string Serialize(RuntimeStateMutation mutation) => JsonSerializer.Serialize(mutation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
