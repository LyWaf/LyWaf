namespace LyWaf.Services.Lifecycle;

/// <summary>Cancellation of an idle service wait is a normal wake-up, not an exception path.</summary>
internal static class ShutdownWait
{
    internal static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        await Task.Delay(delay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return !stoppingToken.IsCancellationRequested;
    }
}
