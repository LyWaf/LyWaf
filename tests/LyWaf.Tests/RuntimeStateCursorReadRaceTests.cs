using System.Diagnostics;
using System.Text.Json;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateCursorReadRaceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "cursor-read-race-" + Guid.NewGuid().ToString("N"));
    public sealed record Cursor(int Sequence);

    [Fact]
    public async Task FirstCheckpointPublicationUnblocksReaderWithoutWaitingForWriterLock()
    {
        var files = new RuntimeStateSyncFiles(root);
        var path = files.PathFor("out", "cluster", "source", "target");
        using var writer = files.Lock(path);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.ReadOpenConflict = _ => waiting.TrySetResult();
        var reading = Task.Run(() => files.LockWhileMissing(path));
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            files.Save(path, new Cursor(1));
            using var acquired = await reading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(acquired);
            Assert.Equal(1, files.Read<Cursor>(path)!.Sequence);
        }
        finally { writer.Dispose(); using var acquired = await reading; }
    }

    [Fact]
    public async Task SharingConflictDuringOpenRetriesThenReadsWholeReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var files = new RuntimeStateSyncFiles(root);
        var path = files.PathFor("out", "cluster", "source", "target");
        files.Save(path, new Cursor(1));
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var conflict = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        files.ReadOpenConflict = error =>
        {
            Assert.Contains(error.HResult & 0xffff, new[] { 32, 33 });
            conflict.TrySetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
        };
        var reading = Task.Run(() => files.Read<Cursor>(path));
        try
        {
            var first = await Task.WhenAny(conflict.Task, reading).WaitAsync(TimeSpan.FromSeconds(10));
            if (first == reading) await reading;
            Assert.Same(conflict.Task, first);
            held.Dispose();
            files.Save(path, new Cursor(2));
        }
        finally { held.Dispose(); resume.Set(); }
        Assert.Equal(2, (await reading)!.Sequence);
        Assert.Equal(2, files.Read<Cursor>(path)!.Sequence);
    }

    [Fact]
    public void PersistentSharingConflictFailsBoundedlyWithoutReturningMissingCursor()
    {
        if (!OperatingSystem.IsWindows()) return;
        var files = new RuntimeStateSyncFiles(root);
        var path = files.PathFor("out", "cluster", "source", "target");
        files.Save(path, new Cursor(1));
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var attempts = 0;
        files.ReadOpenConflict = _ => attempts++;
        var watch = Stopwatch.StartNew();
        Assert.Throws<IOException>(() => files.Read<Cursor>(path));
        Assert.True(attempts > 0);
        Assert.InRange(watch.Elapsed.TotalSeconds, 4.5, 10);
    }

    [Fact]
    public void DisappearanceAfterExistenceCheckRetriesInsteadOfReturningNull()
    {
        if (!OperatingSystem.IsWindows()) return;
        var files = new RuntimeStateSyncFiles(root);
        var path = files.PathFor("out", "cluster", "source", "target");
        files.Save(path, new Cursor(7));
        files.BeforeReadOpen = () => File.Move(path, path + ".moving");
        var conflicts = 0;
        files.ReadOpenConflict = error =>
        {
            Assert.IsType<FileNotFoundException>(error);
            conflicts++;
            File.Move(path + ".moving", path);
        };
        Assert.Equal(7, files.Read<Cursor>(path)!.Sequence);
        Assert.Equal(1, conflicts);
    }

    [Fact]
    public void CorruptPayloadIsNotRetriedOrTreatedAsAbsent()
    {
        var files = new RuntimeStateSyncFiles(root);
        var path = files.PathFor("out", "cluster", "source", "target");
        File.WriteAllText(path, "{\"value\":{\"sequence\":2},\"digest\":\"wrong\"}");
        var attempts = 0;
        files.ReadOpenConflict = _ => attempts++;
        Assert.Throws<InvalidDataException>(() => files.Read<Cursor>(path));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task RealSenderPollingConcurrentWithAtomicAckReplacement()
    {
        var options = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "source", StateDirectory = root,
            Peers = new() { ["target"] = new() { Send = new() { AllowedDomains = ["test"], AllowUngroupedKeys = true } } }
        });
        var sender = new RuntimeStateSyncSender(options);
        using var stop = new CancellationTokenSource();
        var reads = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (sender.ReadPending("cluster", "source", "target") is { } pending)
                    RuntimeStateSyncProtocol.Validate(pending);
                Interlocked.Increment(ref reads);
                Thread.Yield();
            }
        });
        try
        {
            for (var sequence = 1; sequence <= 200; sequence++)
            {
                var mutation = new RuntimeStateMutation("test", "key", "source", sequence, DateTimeOffset.UtcNow,
                    null, false, JsonSerializer.SerializeToElement(sequence));
                var batch = sender.Prepare("cluster", "source", "target", [mutation]);
                sender.Confirm(batch, new(batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId,
                    batch.Epoch, batch.Sequence, batch.Digest));
            }
        }
        finally { stop.Cancel(); await reader; }
        Assert.True(reads > 0);
        Assert.Null(sender.ReadPending("cluster", "source", "target"));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
