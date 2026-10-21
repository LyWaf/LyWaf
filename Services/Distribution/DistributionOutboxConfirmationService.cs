namespace LyWaf.Services.Distribution;

/// <summary>Projects durable confirmations independently of HTTP/UI activity.</summary>
public sealed class DistributionOutboxConfirmationService(
    DistributionControllerService controller,
    ILogger<DistributionOutboxConfirmationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var nextWarning = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await controller.RecoverCompletedOutboxAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (DateTimeOffset.UtcNow >= nextWarning)
                    {
                        nextWarning = DateTimeOffset.UtcNow.AddMinutes(1);
                        logger.LogWarning("Outbox confirmation projection failed ({ErrorType})", ex.GetType().Name);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
