using LyWaf.Services.Distribution;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

/// <summary>Local retention only; never executes or replays a configuration command.</summary>
public sealed class NodeConfigurationMaintenanceService(
    DistributionNodeService node,
    NodeCommandInbox inbox,
    IOptionsMonitor<DistributionOptions> distribution,
    IOptionsMonitor<NodeCommunicationOptions> communication,
    ILogger<NodeConfigurationMaintenanceService> logger) : BackgroundService
{
    private DateTimeOffset _nextInboxAt;
    private DateTimeOffset _nextWarningAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await MaintainAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task MaintainAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var legacy = distribution.CurrentValue;
            var protocol = communication.CurrentValue;
            if ((legacy.Enabled && legacy.Role == "Node") || (protocol.Enabled && protocol.Role == "Node"))
                await node.CleanupExpiredCandidatesAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { Warn(error); }

        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow < _nextInboxAt) return;
        _nextInboxAt = DateTimeOffset.UtcNow.AddHours(1);
        try
        {
            if (communication.CurrentValue is { Enabled: true, Role: "Node" })
                await inbox.RetireCompletedAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { Warn(error); }
    }

    private void Warn(Exception error)
    {
        if (DateTimeOffset.UtcNow < _nextWarningAt) return;
        _nextWarningAt = DateTimeOffset.UtcNow.AddMinutes(1);
        logger.LogWarning("Node configuration maintenance deferred ({ErrorType})", error.GetType().Name);
    }
}
