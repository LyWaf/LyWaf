using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateSyncQuotaTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sync-quota-" + Guid.NewGuid().ToString("N"));
    private sealed record Value(int Number);

    [Fact]
    public async Task IndependentStreamsWaitForQuotaLockAndCommitAfterRelease()
    {
        var files = new RuntimeStateSyncFiles(_root);
        var path = files.PathFor("out", "cluster", "a", "b");
        using var held = new FileStream(Path.Combine(_root, "runtime-sync", ".quota.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var saving = Task.Run(() => files.Save(path, new Value(42)));
        try
        {
            await Task.Delay(100);
            Assert.False(saving.IsCompleted);
            Assert.False(File.Exists(path));
        }
        finally { held.Dispose(); }
        await saving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(42, files.Read<Value>(path)!.Number);
    }

    [Fact]
    public async Task ConcurrentPeerSavesDoNotLoseAdmissionOrCorruptFiles()
    {
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            var files = new RuntimeStateSyncFiles(_root);
            var path = files.PathFor("out", "cluster", "a", "peer" + i);
            files.Save(path, new Value(i));
            Assert.Equal(i, files.Read<Value>(path)!.Number);
        })));
        Assert.Equal(24, Directory.GetFiles(Path.Combine(_root, "runtime-sync"), "*.json").Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "runtime-sync"), "*.tmp"));
    }

    [Fact]
    public async Task CancelledQuotaWaitLeavesPreviousCommitUntouched()
    {
        var files = new RuntimeStateSyncFiles(_root);
        var path = files.PathFor("out", "cluster", "a", "b");
        files.Save(path, new Value(1));
        using var held = new FileStream(Path.Combine(_root, "runtime-sync", ".quota.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => files.Save(path, new Value(2), cancellation.Token)));
        Assert.Equal(1, files.Read<Value>(path)!.Number);
    }

    [Fact]
    public async Task ReplacementWaitsForNonDeleteSharingReaderWithoutDeletingOldCommit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var files = new RuntimeStateSyncFiles(_root);
        var path = files.PathFor("out", "cluster", "a", "b");
        files.Save(path, new Value(1));
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var replacement = Task.Run(() => files.Save(path, new Value(2)));
        try
        {
            await Task.Delay(100);
            Assert.False(replacement.IsCompleted);
            Assert.Equal(1, files.Read<Value>(path)!.Number);
        }
        finally { reader.Dispose(); }
        await replacement.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, files.Read<Value>(path)!.Number);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "runtime-sync"), "*.tmp"));
    }

    [Fact]
    public void ReadOnlyDestinationIsNotDeletedOrMadeWritable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var files = new RuntimeStateSyncFiles(_root);
        var path = files.PathFor("out", "cluster", "a", "b");
        files.Save(path, new Value(1));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => files.Save(path, new Value(2)));
            Assert.Equal(1, files.Read<Value>(path)!.Number);
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
