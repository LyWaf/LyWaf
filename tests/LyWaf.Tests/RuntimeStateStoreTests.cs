using LyWaf.Services.RuntimeState;
using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateStoreTests
{
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

public sealed class RuntimeStateAuthenticationTests
{
    [Fact]
    public async Task SignedMutationIsAcceptedOnceAndReplayIsRejected()
    {
        var options = new RuntimeStateOptions { Enabled = true, NodeId = "node-b", SharedSecret = "state-secret" };
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
        var (_, replayError) = await auth.ReadAndVerifyAsync(replay, default);
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
