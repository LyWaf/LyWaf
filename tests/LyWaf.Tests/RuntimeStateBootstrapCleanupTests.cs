using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateBootstrapCleanupTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "bootstrap-cleanup-" + Guid.NewGuid().ToString("N"));
    sealed class Offline : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
    }

    [Theory]
    [InlineData("Rebased")]
    [InlineData("PageDeleted")]
    public async Task DurableRebasePermitsOfflineIdempotentCleanupButPendingDoesNot(string fault)
    {
        var runtime = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "source", StateDirectory = root,
            Peers = new() { ["target"] = new() { Endpoint = "http://localhost", ProtocolVersion = 2,
                DurableSyncEnabled = true, Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true } } }
        });
        var protocol = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, NodeId = "source", ClusterId = "cluster", RequireHttps = false,
            Peers = new() { ["target"] = new() { ActiveKeyId = "key",
                Keys = new() { ["key"] = new string('s', 40) }, AllowedOperations = ["runtime.sync"] } }
        });
        var sender = new RuntimeStateSyncSender(runtime);
        var mutation = new RuntimeStateMutation("test", "local", "source", 1, DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(1));
        var pending = sender.Prepare("cluster", "source", "target", [mutation]);
        var image = new RuntimeStateBootstrapImage("cluster", "target", "source", Guid.NewGuid().ToString("N"),
            pending.Epoch, Guid.NewGuid().ToString("N"), new(["test"], [], [], true),
            [new("test", "large", "target", 1, DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(new string('x', 600000)))],
            DateTimeOffset.UtcNow, "");
        image = image with { Digest = image.ComputeDigest() };
        var pages = new RuntimeStateBootstrapPages(root, "download-page", runtime.CurrentValue);
        var manifest = pages.Capture(image, default);
        var foreignImage = image with { AuthorityNodeId = "other-target", NewEpoch = Guid.NewGuid().ToString("N") };
        foreignImage = foreignImage with { Digest = foreignImage.ComputeDigest() };
        var foreignManifest = pages.Capture(foreignImage, default);
        var files = new RuntimeStateSyncFiles(root, runtime.CurrentValue);
        var offer = files.PathFor("download-offer", "cluster", "source", "target");
        files.Save(offer, new { PendingDigest = pending.Digest, Manifest = manifest });
        using var handler = new Offline();
        using var client = new HttpClient(handler);
        var transport = new RuntimeStateSyncTransport(client, runtime, protocol);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync("target", null, default));
        Assert.Equal(pending.Digest, sender.ReadPending("cluster", "source", "target")!.Digest);
        Assert.Equal(image.Digest, pages.Assemble(manifest).Digest);
        Assert.False(sender.HasCompletedBootstrap(manifest));

        sender.Rebase(pending, image);
        var cursorPath = files.PathFor("out", "cluster", "source", "target");
        var legacy = files.Read<JsonObject>(cursorPath)!;
        legacy.Remove("bootstrapDigest");
        files.Save(cursorPath, legacy);
        Assert.True(new RuntimeStateSyncSender(runtime).HasCompletedBootstrap(manifest));
        legacy["bootstrapDigest"] = image.Digest;
        files.Save(cursorPath, legacy);
        var cursorBytes = File.ReadAllBytes(cursorPath);
        Assert.True(new RuntimeStateSyncSender(runtime).HasCompletedBootstrap(manifest));
        files.Save(offer, new { PendingDigest = pending.Digest, Manifest = (object?)null });
        await Assert.ThrowsAsync<InvalidDataException>(() => transport.SendAsync("target", null, default));
        Assert.True(File.Exists(offer));
        files.Save(offer, new { PendingDigest = pending.Digest, Manifest = manifest });
        Assert.False(sender.HasCompletedBootstrap(manifest with { Header = manifest.Header with { Digest = new string('A', 64) } }));
        transport.BootstrapCacheCleanupFaultPoint = stage => { if (stage == fault) throw new IOException("crash"); };
        await Assert.ThrowsAsync<IOException>(() => transport.SendAsync("target", null, default));
        Assert.True(File.Exists(offer));
        Assert.Null(sender.ReadPending("cluster", "source", "target"));
        var calls = handler.Calls;
        transport = new RuntimeStateSyncTransport(client, runtime, protocol);
        await transport.SendAsync("target", null, default);
        await transport.SendAsync("target", null, default);
        Assert.Equal(calls, handler.Calls);
        Assert.False(File.Exists(offer));
        Assert.Equal(foreignManifest.PageDigests.Length, Directory.GetFiles(Path.Combine(root, "runtime-sync"), "download-page-*.json").Length);
        Assert.Equal(foreignImage.Digest, pages.Assemble(foreignManifest).Digest);
        Assert.Equal(cursorBytes, File.ReadAllBytes(cursorPath));
        Assert.True(sender.HasCompletedBootstrap(manifest));

        var batch = sender.Prepare("cluster", "source", "target", [mutation]);
        sender.Confirm(batch, new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch,
            batch.Sequence, batch.Digest, "Applied", batch.Sequence + 1));
        Assert.True(new RuntimeStateSyncSender(runtime).HasCompletedBootstrap(manifest));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
