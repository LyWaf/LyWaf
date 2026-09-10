using LyWaf.Services.ManagementTasks;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementJobTests
{
    [Fact]
    public async Task ShutdownHandoffCancelsQueuedWritesAndRejectsNewWork()
    {
        using var jobs = new ManagementJobService();
        var ran = false;
        var queued = jobs.Enqueue("pending-write", "owner", (_, _) => { ran = true; return Task.CompletedTask; });
        jobs.StopAcceptingForShutdown();
        Assert.Equal(ManagementJobState.Cancelled, jobs.Find(queued.Id)!.State);
        Assert.Throws<InvalidOperationException>(() => jobs.Enqueue("new-write", "owner", (_, _) => Task.CompletedTask));
        await jobs.StartAsync(default);
        await jobs.StopAsync(default);
        Assert.False(ran);
    }
    [Fact]
    public async Task QueueIsBoundedAndCancelledWorkDoesNotRun()
    {
        using var jobs = new ManagementJobService();
        var ran = 0;
        var first = jobs.Enqueue("test", "owner", (_, _) => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        Assert.True(jobs.Cancel(first.Id));
        for (var i = 1; i < 64; i++) jobs.Enqueue("test", "owner", (_, _) => Task.CompletedTask);
        Assert.Throws<InvalidOperationException>(() => jobs.Enqueue("overflow", "owner", (_, _) => Task.CompletedTask));
        await jobs.StartAsync(default);
        await WaitFor(jobs, jobs.List()[0].Id);
        Assert.Equal(0, ran);
        await jobs.StopAsync(default);
    }

    [Fact]
    public async Task TimeoutFailureRetryAndBoundedLogsAreObservable()
    {
        using var jobs = new ManagementJobService();
        var timeout = jobs.Enqueue("timeout", "owner", (_, ct) => Task.Delay(10000, ct), TimeSpan.FromMilliseconds(20));
        var attempts = 0;
        var failure = jobs.Enqueue("retry", "owner", (log, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("private secret");
            for (var i = 0; i < 500; i++) log(new string('x', 5000));
            return Task.CompletedTask;
        });
        await jobs.StartAsync(default);
        await WaitFor(jobs, failure.Id);
        Assert.Equal(ManagementJobState.TimedOut, jobs.Find(timeout.Id)!.State);
        Assert.Equal("InvalidOperationException", jobs.Find(failure.Id)!.Error);
        var retry = jobs.Retry(failure.Id, "another-admin")!;
        await WaitFor(jobs, retry.Id);
        var result = jobs.Find(retry.Id)!;
        Assert.Equal(ManagementJobState.Succeeded, result.State);
        Assert.Equal("another-admin", result.Operator);
        Assert.Equal(failure.Id, result.RetryOf);
        Assert.Equal(100, result.Logs.Length);
        Assert.All(result.Logs, line => Assert.True(line.Length < 2100));
        await jobs.StopAsync(default);
    }

    private static async Task WaitFor(ManagementJobService jobs, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (jobs.Find(id)!.FinishedAt == null) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task CancellationAfterCommitDoesNotTurnSuccessIntoRetryableCancellation()
    {
        using var jobs = new ManagementJobService();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = jobs.Enqueue("commit", "owner", async (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            committed.SetResult();
            await release.Task;
        });
        await jobs.StartAsync(default);
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(jobs.Cancel(job.Id));
            release.SetResult();
            await WaitFor(jobs, job.Id);
            var result = jobs.Find(job.Id)!;
            Assert.Equal(ManagementJobState.Succeeded, result.State);
            Assert.False(result.CanRetry);
            Assert.Null(jobs.Retry(job.Id, "owner"));
            Assert.False(jobs.Cancel(job.Id));
        }
        finally { release.TrySetResult(); await jobs.StopAsync(default); }
    }
}
