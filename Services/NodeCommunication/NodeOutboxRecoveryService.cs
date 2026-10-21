using LyWaf.Services.Distribution;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

/// <summary>Recovers transport outcomes only; deployment progression remains owned by the controller.</summary>
public sealed class NodeOutboxRecoveryService(
    IDistributionNodeClient client,
    IOptionsMonitor<NodeCommunicationOptions> options,
    ILogger<NodeOutboxRecoveryService> logger) : BackgroundService
{
    internal const int MaximumConcurrentPeers = 4;
    private DateTimeOffset _nextWarningAt;
    private readonly object _warningGate = new();
    private DateTimeOffset _nextMaintenanceAt;
    private readonly object _workersGate = new();
    private readonly Dictionary<(string Cluster, string Peer), Task> _workers = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not perform synchronous disk scanning on the host startup path.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RecoverPendingAsync(stoppingToken, waitForCompletion: false);
                await MaintainOutboxAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Task[] pending;
            lock (_workersGate) pending = _workers.Values.ToArray();
            await Task.WhenAll(pending);
        }
    }

    internal async Task MaintainOutboxAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow < _nextMaintenanceAt) return;
        _nextMaintenanceAt = DateTimeOffset.UtcNow.AddHours(1);
        try
        {
            var config = options.CurrentValue;
            if (config.Enabled && config.Role == "Controller")
                await new NodeCommandOutbox(config.StateDirectory).ArchiveCompletedAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { WarnThrottled(ex); }
    }

    internal async Task RecoverPendingAsync(CancellationToken token, bool waitForCompletion = true)
    {
        token.ThrowIfCancellationRequested();
        NodeCommunicationOptions config;
        IReadOnlyList<NodeOutboxRecord> records;
        try
        {
            config = options.CurrentValue;
            if (!config.Enabled || config.Role != "Controller") return;
            records = client.ReadOutbox();
        }
        catch (Exception ex) { WarnThrottled(ex); return; }
        var peers = records.Where(record => Eligible(record, config))
            .GroupBy(record => (record.Message.ClusterId, record.Message.ReceiverNodeId));
        var started = new List<Task>();
        foreach (var peer in peers)
        {
            token.ThrowIfCancellationRequested();
            lock (_workersGate)
            {
                if (_workers.Count >= MaximumConcurrentPeers || _workers.ContainsKey(peer.Key)) continue;
                // Do not wait for the whole batch: later scans can fill free slots while another peer is slow.
                var task = Task.Run(async () =>
                {
                    try { await RecoverPeerAsync(peer, config, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception ex) { WarnThrottled(ex); }
                    finally { lock (_workersGate) _workers.Remove(peer.Key); }
                }, CancellationToken.None);
                _workers.Add(peer.Key, task);
                started.Add(task);
            }
        }
        if (waitForCompletion)
        {
            await Task.WhenAll(started);
            token.ThrowIfCancellationRequested();
        }
    }

    private async Task RecoverPeerAsync(IEnumerable<NodeOutboxRecord> records, NodeCommunicationOptions config, CancellationToken token)
    {
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            if (!Eligible(record, config)) continue;
            try { await client.RecoverAsync(record.Message.MessageId, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { token.ThrowIfCancellationRequested(); WarnThrottled(ex); }
        }
    }

    private static bool Eligible(NodeOutboxRecord record, NodeCommunicationOptions config) =>
        record.State == "Unknown" && record.Result is null
        && record.Attempts is > 0 and < NodeCommandOutbox.MaximumAttempts
        && record.Message.ExpiresAt > DateTimeOffset.UtcNow && !(record.NextAttemptAt > DateTimeOffset.UtcNow)
        && record.Message.ClusterId == config.ClusterId && record.Message.SenderNodeId == config.NodeId
        && record.Message.Type is "configuration.prepare" or "configuration.commit" or "configuration.rollback";

    private void WarnThrottled(Exception error)
    {
        lock (_warningGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextWarningAt) return;
            _nextWarningAt = now.AddMinutes(1);
        }
        // No remote error text, configuration payload, or per-message log stream.
        logger.LogWarning("Node outbox recovery could not confirm an outcome ({ErrorType}); inspect durable outbox records. Further warnings are limited to one per minute.",
            error.GetType().Name);
    }
}
