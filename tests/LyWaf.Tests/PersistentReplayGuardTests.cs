using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class PersistentReplayGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lywaf-replay-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ClaimsAreDurableBoundedAndExpireAfterEntireSignatureWindow()
    {
        Assert.Equal(ReplayClaim.Accepted, PersistentReplayGuard.Claim(_directory, "first", 1000, 2));
        Assert.Single(Directory.GetFiles(_directory, "*.nonce"));
        Assert.Equal(ReplayClaim.Replay, PersistentReplayGuard.Claim(_directory, "first", 1601, 2));
        Assert.Equal(ReplayClaim.Accepted, PersistentReplayGuard.Claim(_directory, "second", 1000, 2));
        Assert.Equal(ReplayClaim.CapacityExceeded, PersistentReplayGuard.Claim(_directory, "third", 1000, 2));
        Assert.Equal(ReplayClaim.Accepted, PersistentReplayGuard.Claim(_directory, "third", 1602, 2));
        Assert.Single(Directory.GetFiles(_directory, "*.nonce"));
    }

    [Fact]
    public async Task ConcurrentClaimsCannotExecuteTheSameCommandTwice()
    {
        Directory.CreateDirectory(_directory);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(
            () => PersistentReplayGuard.Claim(_directory, "shared-command", 1000))));
        Assert.Single(results, result => result == ReplayClaim.Accepted);
        Assert.All(results, result => Assert.Contains(result, new[] { ReplayClaim.Accepted, ReplayClaim.Replay, ReplayClaim.Unavailable }));
        Assert.Equal(ReplayClaim.Replay, PersistentReplayGuard.Claim(_directory, "shared-command", 1000));
    }

    [Fact]
    public async Task LockContentionWaitsWithoutClaimingUntilReleased()
    {
        Directory.CreateDirectory(_directory);
        var gate = new FileStream(Path.Combine(_directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var conflict = Assert.Throws<IOException>(() => new FileStream(Path.Combine(_directory, ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        if (OperatingSystem.IsWindows()) Assert.Contains(conflict.HResult & 0xffff, new[] { 32, 33 });
        var pending = PersistentReplayGuard.ClaimAsync(_directory, "waiting", 1000);
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        Assert.Empty(Directory.GetFiles(_directory, "*.nonce"));
        gate.Dispose();
        Assert.Equal(ReplayClaim.Accepted, await pending);
        Assert.Equal(ReplayClaim.Replay, await PersistentReplayGuard.ClaimAsync(_directory, "waiting", 1000));
    }

    [Fact]
    public async Task LockTimeoutAndCancellationFailClosed()
    {
        Directory.CreateDirectory(_directory);
        using var gate = new FileStream(Path.Combine(_directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(ReplayClaim.Unavailable, await PersistentReplayGuard.ClaimAsync(_directory, "timeout", 1000,
            lockTimeout: TimeSpan.FromMilliseconds(40)));
        using var cancellation = new CancellationTokenSource();
        var pending = PersistentReplayGuard.ClaimAsync(_directory, "cancel", 1000, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.GetFiles(_directory, "*.nonce"));
    }

    [Fact]
    public async Task ConcurrentAsyncClaimsPreserveEveryDistinctNonceAndRejectDuplicates()
    {
        var distinct = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(
            () => PersistentReplayGuard.ClaimAsync(_directory, "distinct-" + i, 1000))));
        Assert.All(distinct, result => Assert.Equal(ReplayClaim.Accepted, result));
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(
            () => PersistentReplayGuard.ClaimAsync(_directory, "shared", 1000))));
        Assert.Single(duplicates, result => result == ReplayClaim.Accepted);
        Assert.Equal(31, duplicates.Count(result => result == ReplayClaim.Replay));
    }

    [Fact]
    public void CorruptOrUnwritableStorageFailsClosed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "corrupt.nonce"), "bad");
        Assert.Equal(ReplayClaim.Unavailable, PersistentReplayGuard.Claim(_directory, "next", 1000));
        Assert.Equal(ReplayClaim.Unavailable, PersistentReplayGuard.Claim(Path.Combine(_directory, "corrupt.nonce"), "next", 1000));
    }

    [Fact]
    public async Task PendingFsyncDoesNotHoldDirectoryLockOrAcceptItsCommandEarly()
    {
        using var staged = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var first = Task.Run(() => PersistentReplayGuard.ClaimAsync(_directory, "first", 1000,
            beforeFlush: () => { staged.Set(); release.Wait(TimeSpan.FromSeconds(10)); }));
        try
        {
            Assert.True(staged.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(first.IsCompleted);
            Assert.Equal(ReplayClaim.Replay, await PersistentReplayGuard.ClaimAsync(_directory, "first", 1000));
            Assert.Equal(ReplayClaim.Accepted, await PersistentReplayGuard.ClaimAsync(_directory, "second", 1000));
            Assert.False(first.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(ReplayClaim.Accepted, await first);
        Assert.Equal(ReplayClaim.Replay, await PersistentReplayGuard.ClaimAsync(_directory, "first", 1000));
    }

    [Fact]
    public async Task FsyncFailureDoesNotReturnAcceptedOrLeakHandle()
    {
        Assert.Equal(ReplayClaim.Unavailable, await PersistentReplayGuard.ClaimAsync(_directory, "failed", 1000,
            beforeFlush: () => throw new IOException("injected fsync failure")));
        Assert.Equal(ReplayClaim.Replay, await PersistentReplayGuard.ClaimAsync(_directory, "failed", 1000));
        Assert.Equal(ReplayClaim.Accepted, await PersistentReplayGuard.ClaimAsync(_directory, "other", 1000));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
