using System.Net;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateBootstrapTests : IDisposable
{
    private const string Secret = "bootstrap-authenticated-http-test-32-bytes";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootstrap-" + Guid.NewGuid().ToString("N"));
    private StaticOptionsMonitor<RuntimeStateOptions> Runtime(string id) => new(new()
    {
        Enabled = true, NodeId = id, StateDirectory = Path.Combine(_root, id),
        Peers = new() { [id == "source" ? "target" : "source"] = new()
        {
            Endpoint = "http://localhost", ProtocolVersion = 2, DurableSyncEnabled = true,
            BootstrapAuthority = id == "source", AllowBootstrapExport = id == "target",
            Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true, TrustedForwardedSources = ["source", "target"] },
            Receive = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true, TrustedForwardedSources = ["source", "target"] }
        } }
    });
    private StaticOptionsMonitor<NodeCommunicationOptions> Protocol(string id) => new(new()
    {
        Enabled = true, RequireHttps = false, NodeId = id, ClusterId = "cluster", StateDirectory = Path.Combine(_root, id, "protocol"),
        Peers = new() { [id == "source" ? "target" : "source"] = new()
        { ActiveKeyId = "key", Keys = new() { ["key"] = Secret }, AllowedOperations = [RuntimeStateSyncProtocol.Operation] } }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task HttpBootstrapAfterOfflineTombstoneExpirySurvivesCrashAndRejectsOldEpoch(int fault)
    {
        var sourceOptions = Runtime("source");
        var targetOptions = Runtime("target");
        var source = new RuntimeStateStore(sourceOptions);
        await source.StartAsync(default);
        source.Set("test", "deleted", "stale");
        source.Set("private", "keep", "local");
        var stale = Assert.Single(source.ExportMutations(), m => m.Domain == "test");
        var sender = new RuntimeStateSyncSender(sourceOptions);
        var initial = sender.Prepare("cluster", "source", "target", [stale]);
        using (var targetBefore = new RuntimeStateStore(targetOptions))
        {
            await targetBefore.StartAsync(default);
            var oldReceiver = new RuntimeStateSyncReceiver(targetOptions, targetBefore)
            { UtcNow = () => DateTimeOffset.UtcNow.AddHours(-26) };
            sender.Confirm(initial, await oldReceiver.AcceptAsync(initial, default));
            targetBefore.Merge(stale with { Sequence = stale.Sequence + 1, Deleted = true, Value = null,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            targetBefore.Set("test", "live", "authoritative");
            if (fault == 3)
                for (var i = 0; i < 10005; i++) targetBefore.Set("test", "large-" + i, new string('x', 1000));
            await targetBefore.StopAsync(default);
            Assert.True(await targetBefore.CompactJournalAsync(Path.Combine(targetOptions.CurrentValue.StateDirectory, "runtime-state.jsonl"), default));
        }
        var pending = sender.Prepare("cluster", "source", "target", [stale]);
        using var target = new RuntimeStateStore(targetOptions);
        Assert.DoesNotContain(target.ExportMutations(), m => m.Key == "deleted");
        await target.StartAsync(default);
        var receiver = new RuntimeStateSyncReceiver(targetOptions, target);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(targetOptions);
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(Protocol("target"));
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton(receiver);
        await using var app = builder.Build();
        app.MapNodeCommunication(7030);
        await app.StartAsync();
        sourceOptions.CurrentValue.Peers["target"].Endpoint = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        try
        {
            var requestedPages = new System.Collections.Concurrent.ConcurrentBag<int>();
            if (fault != 0)
            {
                if (fault == 1) source.BootstrapBeforePublish = () => throw new IOException("crash after snapshot fsync");
                using var client = new HttpClient(new HostHandler(fault == 2, fault == 3 ? 2 : null, requestedPages.Add, fault == 4));
                var attempt = new RuntimeStateSyncTransport(client, sourceOptions, Protocol("source"), source);
                await Assert.ThrowsAnyAsync<Exception>(() => attempt.SendAsync("target", null, default));
                if (fault == 4)
                {
                    Assert.True(source.TryGet<string>("test", "deleted", out _));
                    Assert.False(source.TryGet<string>("test", "live", out _));
                }
                if (fault == 3)
                {
                    Assert.True(source.TryGet<string>("test", "deleted", out _));
                    Assert.False(source.TryGet<string>("test", "large-0", out _));
                    Assert.Equal(new[] { 0, 1, 2 }, requestedPages.Order().ToArray());
                }
                Assert.NotNull(sender.ReadPending("cluster", "source", "target"));
                await source.StopAsync(default);
                source.Dispose();
                source = new RuntimeStateStore(sourceOptions);
                await source.StartAsync(default);
            }
            using (var replicator = new RuntimeStateReplicator(source, sourceOptions, new HostHandler(onPage: requestedPages.Add), Protocol("source")))
            {
                await replicator.StartAsync(default);
                try
                {
                    await WaitAsync(() => sender.ReadPending("cluster", "source", "target") is null);
                    Assert.False(source.TryGet<string>("test", "deleted", out _));
                    Assert.True(source.TryGet<string>("test", "live", out var live));
                    Assert.Equal("authoritative", live);
                    Assert.True(source.TryGet<string>("private", "keep", out _));
                    if (fault == 3)
                    {
                        Assert.Equal(10005, source.ExportMutations().Count(m => m.Key.StartsWith("large-", StringComparison.Ordinal)));
                        Assert.Equal(1, requestedPages.Count(p => p == 0));
                        Assert.Equal(1, requestedPages.Count(p => p == 1));
                        Assert.Equal(2, requestedPages.Count(p => p == 2));
                        Assert.True(requestedPages.Max() > 32);
                    }
                    source.Set("test", "fresh", "new-epoch");
                    await WaitAsync(() => target.TryGet<string>("test", "fresh", out _));
                    await WaitAsync(() => sender.ReadPending("cluster", "source", "target") is null);
                    Assert.False(target.TryGet<string>("test", "deleted", out _));
                }
                finally { await replicator.StopAsync(default); }
            }
            // Drain HTTP requests before the direct retired-epoch fault injection.
            await app.StopAsync();
            Assert.Equal("SnapshotRequired", (await receiver.AcceptAsync(pending, default)).Status);
            Assert.False(target.TryGet<string>("test", "deleted", out _));
            var next = sender.Prepare("cluster", "source", "target", [Assert.Single(source.ExportMutations(), m => m.Key == "fresh")]);
            Assert.NotEqual(initial.Epoch, next.Epoch);
            await source.StopAsync(default);
            Assert.True(await source.CompactJournalAsync(Path.Combine(sourceOptions.CurrentValue.StateDirectory, "runtime-state.jsonl"), default));
            using var recovered = new RuntimeStateStore(sourceOptions);
            Assert.False(recovered.TryGet<string>("test", "deleted", out _));
            Assert.True(recovered.TryGet<string>("test", "live", out _));
            Assert.False(recovered.IsCurrentForSync(stale));
        }
        finally
        {
            await app.StopAsync();
            await target.StopAsync(default);
            await source.StopAsync(default);
            source.Dispose();
        }
    }

    [Fact]
    public async Task BootstrapOfferConflictsAndExpirationReturnProtocolConflict()
    {
        var options = Runtime("target");
        using var target = new RuntimeStateStore(options);
        await target.StartAsync(default);
        try
        {
            var receiver = new RuntimeStateSyncReceiver(options, target);
            var scope = RuntimeStateBootstrapScope.From(options.CurrentValue, options.CurrentValue.Peers["source"].Send);
            RuntimeStateSyncBatch Sign(RuntimeStateSyncBatch batch) => batch with { Digest = RuntimeStateSyncProtocol.ComputeDigest(batch) };
            var fetch = Sign(new("cluster", "source", "target", Guid.NewGuid().ToString("N"), 1, [], "",
                new("fetch", Guid.NewGuid().ToString("N"), scope.Digest())));
            var offered = await receiver.AcceptAsync(fetch, default);
            Assert.Equal("SnapshotOffered", offered.Status);
            var pageRequest = Sign(fetch with { Bootstrap = fetch.Bootstrap! with
                { Phase = "page", ImageDigest = offered.Manifest!.Header.Digest, PageIndex = 0 } });
            var first = await receiver.AcceptAsync(pageRequest, default);
            var duplicate = await receiver.AcceptAsync(pageRequest, default);
            Assert.Equal("SnapshotPage", first.Status);
            Assert.Equal(first.Page!.Data, duplicate.Page!.Data);
            Assert.Equal(first.Page.SnapshotId, duplicate.Page.SnapshotId);
            var sendPolicy = options.CurrentValue.Peers["source"].Send;
            var trusted = sendPolicy.TrustedForwardedSources;
            sendPolicy.TrustedForwardedSources = [];
            Assert.Equal("Conflict", (await receiver.AcceptAsync(pageRequest, default)).Status);
            sendPolicy.TrustedForwardedSources = trusted;
            var outOfRange = Sign(pageRequest with { Bootstrap = pageRequest.Bootstrap! with { PageIndex = int.MaxValue } });
            Assert.Equal("Conflict", (await receiver.AcceptAsync(outOfRange, default)).Status);
            var wrongEpoch = Sign(fetch with { Epoch = Guid.NewGuid().ToString("N") });
            Assert.Equal("Conflict", (await receiver.AcceptAsync(wrongEpoch, default)).Status);
            var wrongCommit = Sign(fetch with { Bootstrap = fetch.Bootstrap! with { Phase = "commit", ImageDigest = "incorrect" } });
            Assert.Equal("Conflict", (await receiver.AcceptAsync(wrongCommit, default)).Status);
            receiver.UtcNow = () => DateTimeOffset.UtcNow.AddMinutes(6);
            Assert.Equal("Conflict", (await receiver.AcceptAsync(fetch, default)).Status);
        }
        finally { await target.StopAsync(default); }
    }

    [Fact]
    public async Task SnapshotInstallationSerializesConcurrentWritesAndNeverPublishesBeforeFlush()
    {
        var sourceOptions = Runtime("source");
        using var source = new RuntimeStateStore(sourceOptions);
        using var target = new RuntimeStateStore(Runtime("target"));
        source.Set("test", "deleted", "old");
        target.Set("test", "live", "new");
        var scope = RuntimeStateBootstrapScope.From(sourceOptions.CurrentValue, sourceOptions.CurrentValue.Peers["target"].Receive);
        var image = target.CaptureBootstrap("cluster", "source", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), scope);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        source.JournalBeforeDurableFlush = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        await source.StartAsync(default);
        var install = source.InstallBootstrapAsync(image, default);
        Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
        var concurrent = Task.Run(() => source.Set("test", "concurrent", "kept"));
        try
        {
            await Task.Delay(50);
            Assert.False(install.IsCompleted);
            Assert.False(concurrent.IsCompleted);
            Assert.True(source.TryGet<string>("test", "deleted", out _));
            Assert.False(source.TryGet<string>("test", "live", out _));
        }
        finally { release.Set(); }
        await Task.WhenAll(install, concurrent).WaitAsync(TimeSpan.FromSeconds(10));
        await source.InstallBootstrapAsync(image, default);
        Assert.True(source.TryGet<string>("test", "concurrent", out _));
        Assert.False(source.TryGet<string>("test", "deleted", out _));
        await source.StopAsync(default);
    }

    [Fact]
    public async Task BootstrapRequiresExplicitAuthorityAndExactScope()
    {
        var options = Runtime("source");
        using var source = new RuntimeStateStore(options);
        using var target = new RuntimeStateStore(Runtime("target"));
        var scope = RuntimeStateBootstrapScope.From(options.CurrentValue, options.CurrentValue.Peers["target"].Receive);
        var image = target.CaptureBootstrap("cluster", "source", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), scope);
        options.CurrentValue.Peers["target"].BootstrapAuthority = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => source.InstallBootstrapAsync(image, default));
        options.CurrentValue.Peers["target"].BootstrapAuthority = true;
        var wrong = image with { Scope = scope with { Domains = ["private"] } };
        wrong = wrong with { Digest = wrong.ComputeDigest() };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => source.InstallBootstrapAsync(wrong, default));
        foreach (var domain in new[] { "runtime.sync.snapshot", "runtime.sync.bootstrap-fence" })
        {
            Assert.Throws<ArgumentException>(() => source.Set(domain, "x", image));
            Assert.Throws<ArgumentException>(() => source.Merge(new(domain, "x", "target", 0, DateTimeOffset.UtcNow, null, false,
                JsonSerializer.SerializeToElement(image, RuntimeStateSyncProtocol.Json))));
        }
    }

    private sealed class HostHandler(bool loseCommit = false, int? losePage = null, Action<int>? onPage = null, bool corruptPage = false) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            request.Headers.Host = "localhost:7030";
            var message = JsonSerializer.Deserialize<NodeMessage>(await request.Content!.ReadAsByteArrayAsync(token), RuntimeStateSyncProtocol.Json)!;
            var batch = message.Payload.Deserialize<RuntimeStateSyncBatch>(RuntimeStateSyncProtocol.Json)!;
            if (batch.Bootstrap is { Phase: "page", PageIndex: { } index }) onPage?.Invoke(index);
            var response = await base.SendAsync(request, token);
            if (corruptPage && batch.Bootstrap?.Phase == "page")
            {
                var ack = JsonSerializer.Deserialize<RuntimeStateSyncAck>(await response.Content.ReadAsByteArrayAsync(token), RuntimeStateSyncProtocol.Json)!;
                ack.Page!.Data[0] ^= 1;
                response.Content.Dispose();
                response.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(ack, RuntimeStateSyncProtocol.Json));
            }
            if (batch.Bootstrap is { Phase: "page", PageIndex: { } page } && losePage == page)
            {
                response.Dispose();
                throw new HttpRequestException("lost bootstrap page response");
            }
            if (loseCommit && batch.Bootstrap?.Phase == "commit")
            {
                response.Dispose();
                throw new HttpRequestException("lost bootstrap commit ACK");
            }
            return response;
        }
    }
    private static async Task WaitAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(done());
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
