using LyWaf.Services.RuntimeState;
using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateStoreTests
{
    [Fact]
    public void RepairPagesCoverEveryEntryIncludingTombstonesWithoutRepeatingPrefix()
    {
        using var scope = new StoreScope("node-a");
        for (var i = 0; i < 257; i++) scope.Store.Set("repair", i.ToString("D4"), i);
        scope.Store.Remove("repair", "0010");
        string? cursor = null;
        var received = new List<RuntimeStateMutation>();
        do
        {
            var page = scope.Store.ExportPage(cursor, 20);
            Assert.InRange(page.Items.Count, 1, 20);
            received.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(257, received.Count);
        Assert.Equal(257, received.Select(x => x.Key).Distinct().Count());
        Assert.True(Assert.Single(received, x => x.Key == "0010").Deleted);
    }

    [Fact]
    public void RepairCursorReachesEndAfterDeletionAndNewEarlierKeysAppearNextCycle()
    {
        using var scope = new StoreScope("node-a");
        scope.Store.Set("repair", "b", 1);
        scope.Store.Set("repair", "c", 1);
        var first = scope.Store.ExportPage(null, 1);
        Assert.NotNull(first.NextCursor);
        scope.Store.Set("repair", "a", 1);
        var second = scope.Store.ExportPage(first.NextCursor, 1);
        Assert.Equal("c", Assert.Single(second.Items).Key);
        Assert.Null(second.NextCursor);
        Assert.Equal("a", Assert.Single(scope.Store.ExportPage(null, 1).Items).Key);
    }

    [Fact]
    public void OversizedMergedCounterIsRejectedWithoutChangingExistingState()
    {
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(new()
        { NodeId = "local", StateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), MaxValueBytes = 1024 }));
        RuntimeStateMutation Mutation(char prefix, long sequence) => new("cc", "key", "peer", sequence,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), false, null,
            new Dictionary<string, long> { [new string(prefix, 450) + "1"] = 1, [new string(prefix, 450) + "2"] = 1 });
        Assert.True(store.Merge(Mutation('a', 1)));
        Assert.Throws<InvalidOperationException>(() => store.Merge(Mutation('b', 2)));
        Assert.Equal(2, Assert.Single(store.GetSnapshots()).CounterValue);
    }

    [Fact]
    public void CounterSumOverflowIsRejectedBeforePublishingState()
    {
        using var scope = new StoreScope("node-a");
        var first = new RuntimeStateMutation("cc", "key", "peer", 1, DateTimeOffset.UtcNow,
            null, false, null, new Dictionary<string, long> { ["a"] = long.MaxValue });
        Assert.True(scope.Store.Merge(first));
        Assert.Throws<InvalidOperationException>(() => scope.Store.Merge(first with
        { Sequence = 2, CounterComponents = new Dictionary<string, long> { ["b"] = 1 } }));
        Assert.Equal(long.MaxValue, Assert.Single(scope.Store.GetSnapshots()).CounterValue);
    }

    [Fact]
    public void LastWriterWinsUsesSequenceThenNodeId()
    {
        using var scope = new StoreScope("node-a");
        var oldValue = Json("old");
        var newValue = Json("new");

        Assert.True(scope.Store.Merge(new("security", "ip", "node-a", 4, DateTimeOffset.UtcNow, null, false, oldValue)));
        Assert.False(scope.Store.Merge(new("security", "ip", "node-z", 3, DateTimeOffset.UtcNow, null, false, newValue)));
        Assert.True(scope.Store.Merge(new("security", "ip", "node-z", 4, DateTimeOffset.UtcNow, null, false, newValue)));

        Assert.True(scope.Store.TryGet<string>("security", "ip", out var value));
        Assert.Equal("new", value);
    }

    [Fact]
    public void CounterMergeKeepsLargestComponentPerNode()
    {
        using var scope = new StoreScope("node-a");
        var start = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal(1, scope.Store.IncrementWindow("cc", "rule:ip", start, TimeSpan.FromMinutes(1)));
        var key = $"rule:ip|{start.ToUnixTimeMilliseconds()}";

        scope.Store.Merge(new("cc", key, "node-b", 1, DateTimeOffset.UtcNow, start.AddMinutes(1), false, null,
            new Dictionary<string, long> { ["node-b"] = 3 }));
        scope.Store.Merge(new("cc", key, "node-b", 2, DateTimeOffset.UtcNow, start.AddMinutes(1), false, null,
            new Dictionary<string, long> { ["node-b"] = 2 }));

        Assert.Equal(4, scope.Store.GetSnapshots("cc").Single().CounterValue);
    }

    [Fact]
    public void RestartWithoutJournalDoesNotLoseNewIncrementsWhenOldSnapshotArrives()
    {
        using var original = new StoreScope("node-a");
        using var restarted = new StoreScope("node-a");
        using var peer = new StoreScope("node-b");
        var start = DateTimeOffset.UtcNow;
        var length = TimeSpan.FromMinutes(5);
        original.Store.IncrementWindow("cc", "rule:ip", start, length, 40);
        var oldSnapshot = Assert.Single(original.Store.ExportMutations());
        peer.Store.Merge(oldSnapshot);
        peer.Store.IncrementWindow("cc", "rule:ip", start, length, 35);

        restarted.Store.IncrementWindow("cc", "rule:ip", start, length, 30);
        var newSnapshot = Assert.Single(restarted.Store.ExportMutations());
        peer.Store.Merge(newSnapshot);
        peer.Store.Merge(oldSnapshot);
        peer.Store.Merge(newSnapshot);
        restarted.Store.Merge(Assert.Single(peer.Store.ExportMutations()));
        restarted.Store.IncrementWindow("cc", "rule:ip", start, length, 5);
        peer.Store.Merge(Assert.Single(restarted.Store.ExportMutations()));

        Assert.Equal(110, peer.Store.GetWindowValue("cc", "rule:ip", start));
        Assert.Equal(110, restarted.Store.GetWindowValue("cc", "rule:ip", start));
        Assert.Equal(3, Assert.Single(peer.Store.ExportMutations()).CounterComponents!.Count);
    }

    [Fact]
    public async Task RecoveredCounterUsesNewEpochWithoutCountingRecoveredValuesTwice()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-epoch-" + Guid.NewGuid().ToString("N"));
        var options = new StaticOptionsMonitor<RuntimeStateOptions>(new() { NodeId = "node-a", StateDirectory = directory });
        var start = DateTimeOffset.UtcNow;
        try
        {
            using (var original = new RuntimeStateStore(options))
            {
                await original.StartAsync(default);
                original.IncrementWindow("cc", "rule:ip", start, TimeSpan.FromMinutes(5), 40);
                await original.StopAsync(default);
            }
            using var recovered = new RuntimeStateStore(options);
            Assert.Equal(40, recovered.GetWindowValue("cc", "rule:ip", start));
            Assert.Equal(45, recovered.IncrementWindow("cc", "rule:ip", start, TimeSpan.FromMinutes(5), 5));
            var snapshot = Assert.Single(recovered.ExportMutations());
            Assert.Equal(2, snapshot.CounterComponents!.Count);
            using var peer = new StoreScope("node-b");
            peer.Store.Merge(snapshot);
            peer.Store.Merge(snapshot);
            Assert.Equal(45, peer.Store.GetWindowValue("cc", "rule:ip", start));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task JournalIsRecoveredWithoutLoadingASecondStateSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new RuntimeStateOptions { NodeId = "node-a", StateDirectory = directory };
            var first = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(options));
            await first.StartAsync(default);
            first.Set("access", "10.0.0.1", "blocked", TimeSpan.FromMinutes(5));
            await WaitUntilAsync(() => first.GetStatus().PersistedMutations == 1);
            await first.StopAsync(default);
            first.Dispose();

            using var second = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(options));
            Assert.True(second.TryGet<string>("access", "10.0.0.1", out var value));
            Assert.Equal("blocked", value);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StopDrainsPendingJournalWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-state-" + Guid.NewGuid().ToString("N"));
        var options = new RuntimeStateOptions { NodeId = "node-a", StateDirectory = directory };
        try
        {
            var first = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(options));
            await first.StartAsync(default);
            for (var i = 0; i < 100; i++) first.Set("shutdown", i.ToString(), i);
            await first.StopAsync(default);
            first.Dispose();

            using var second = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(options));
            Assert.Equal(100, second.GetSnapshots("shutdown").Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExpiredValueAndClaimCanBeReplaced()
    {
        using var scope = new StoreScope("node-a");
        scope.Store.Set("captcha", "ip", "pending", TimeSpan.FromMilliseconds(20));
        Assert.True(scope.Store.TryClaim("plugin-task", "task-1", TimeSpan.FromMilliseconds(20)));
        Assert.False(scope.Store.TryClaim("plugin-task", "task-1", TimeSpan.FromMinutes(1)));

        await Task.Delay(40);

        Assert.False(scope.Store.TryGet<string>("captcha", "ip", out _));
        Assert.True(scope.Store.TryClaim("plugin-task", "task-1", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task AntiEntropyExportRemovesExpiredEntries()
    {
        using var scope = new StoreScope("node-a");
        scope.Store.Set("temporary", "key", "value", TimeSpan.FromMilliseconds(20));
        await Task.Delay(40);

        Assert.Empty(scope.Store.ExportMutations());
        Assert.Equal(0, scope.Store.GetStatus().ActiveEntries);
    }

    [Fact]
    public void TryTakeConsumesValueOnceAndLeavesTombstone()
    {
        using var scope = new StoreScope("node-a");
        scope.Store.Set("token", "one", "secret", TimeSpan.FromMinutes(1));

        Assert.True(scope.Store.TryTake<string>("token", "one", out var value));
        Assert.Equal("secret", value);
        Assert.False(scope.Store.TryTake<string>("token", "one", out _));
    }

    [Fact]
    public void PluginTaskCoordinatorRejectsDuplicateIdempotencyKey()
    {
        using var scope = new StoreScope("node-a");
        var coordinator = new PluginTaskCoordinator(scope.Store);

        Assert.True(coordinator.TryStart("sample", "import-1", TimeSpan.FromMinutes(5), out var started));
        Assert.Equal("running", started.Status);
        Assert.False(coordinator.TryStart("sample", "import-1", TimeSpan.FromMinutes(5), out _));
        Assert.Equal("succeeded", coordinator.Complete("sample", "import-1", true).Status);
        Assert.True(coordinator.TryGet("sample", "import-1", out var completed));
        Assert.Equal("succeeded", completed!.Status);
    }

    private static System.Text.Json.JsonElement Json(string value)
        => System.Text.Json.JsonSerializer.SerializeToElement(value);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class StoreScope : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "lywaf-state-" + Guid.NewGuid().ToString("N"));
        public RuntimeStateStore Store { get; }

        public StoreScope(string nodeId)
        {
            Store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(new()
            {
                NodeId = nodeId,
                StateDirectory = _directory
            }));
        }

        public void Dispose()
        {
            Store.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}

public sealed class RuntimeStateAuthenticationTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "lywaf-runtime-auth-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, true); }
    [Fact]
    public async Task SignedMutationIsAcceptedOnceAndReplayIsRejected()
    {
        var options = new RuntimeStateOptions { Enabled = true, NodeId = "node-b", SharedSecret = "state-secret", StateDirectory = _stateDirectory };
        options.Peers["node-a"] = new() { Endpoint = "http://node-a",
            Receive = new() { AllowedDomains = ["ip.blocked"], AllowUngroupedKeys = true } };
        var auth = new RuntimeStateAuthentication(new StaticOptionsMonitor<RuntimeStateOptions>(options));
        var mutation = new RuntimeStateMutation("ip.blocked", "10.0.0.1", "node-a", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), false, JsonSerializer.SerializeToElement("test"));
        var body = JsonSerializer.SerializeToUtf8Bytes(mutation, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var signed = new HttpRequestMessage(HttpMethod.Post, "http://node-b/") { Content = new ByteArrayContent(body) };
        RuntimeStateAuthentication.Sign(signed, "node-a", options.SharedSecret, body);

        var first = CreateContext(body, signed);
        var (value, error) = await auth.ReadAndVerifyAsync(first, default);
        Assert.NotNull(value);
        Assert.Null(error);

        var replay = CreateContext(body, signed);
        var restarted = new RuntimeStateAuthentication(new StaticOptionsMonitor<RuntimeStateOptions>(options));
        var (_, replayError) = await restarted.ReadAndVerifyAsync(replay, default);
        Assert.Equal("nonce 已使用", replayError);
    }

    private static DefaultHttpContext CreateContext(byte[] body, HttpRequestMessage signed)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        foreach (var header in signed.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
        return context;
    }
}
