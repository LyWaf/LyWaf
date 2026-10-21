using System.Text.Json;
using System.Threading.Channels;
using LyWaf.Services.Distribution;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed class NodeHandshakeMonitor(
    IOptionsMonitor<NodeCommunicationOptions> communication,
    IOptionsMonitor<DistributionOptions> distribution,
    INodeHandshakePeerProbe probe,
    NodeRegistrationObserver observer,
    NodeHandshakeRecoveryService recovery,
    ILogger<NodeHandshakeMonitor> logger) : BackgroundService
{
    internal TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan HeartbeatBudget { get; init; } = TimeSpan.FromSeconds(2);
    internal TimeSpan RecoveryBudget { get; init; } = TimeSpan.FromSeconds(40);
    internal int RecoveryCapacity { get; init; } = 256;
    private int _nextPeer;
    private readonly SemaphoreSlim _run = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await _run.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var work = new RecoveryQueue(this, lifetime.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RefreshHeartbeatsAsync(work, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning("Node handshake monitor failed: {ErrorType}", ex.GetType().Name); }
                try { await Task.Delay(HeartbeatInterval, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            lifetime.Cancel();
            work.Complete();
            try { await work.Completion; }
            finally { _run.Release(); }
        }
    }

    // One-shot refresh for callers without a running host; it owns and drains its workers too.
    internal async Task RefreshAsync(CancellationToken token)
    {
        await _run.WaitAsync(token);
        try { await RefreshOnceAsync(token); }
        finally { _run.Release(); }
    }

    private async Task RefreshOnceAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var work = new RecoveryQueue(this, lifetime.Token);
        try
        {
            await RefreshHeartbeatsAsync(work, token);
            work.Complete();
            await work.Completion;
        }
        finally
        {
            lifetime.Cancel();
            work.Complete();
            await work.Completion;
        }
    }

    private bool Eligible(string id)
    {
        var config = communication.CurrentValue;
        return config.Enabled && config.Role == "Controller" && distribution.CurrentValue.Enabled
            && distribution.CurrentValue.Nodes.TryGetValue(id, out var target) && target.Enabled && target.ProtocolVersion == 2
            && config.Peers.TryGetValue(id, out var peer)
            && NodeAdmissionPolicy.Allows(peer, NodeHandshakeService.Operation)
            && peer.AllowedOperations.Contains(NodeHandshakeService.Operation, StringComparer.Ordinal);
    }

    private async Task RefreshHeartbeatsAsync(RecoveryQueue work, CancellationToken token)
    {
        var config = communication.CurrentValue;
        var targets = distribution.CurrentValue;
        if (!config.Enabled || config.Role != "Controller" || !targets.Enabled) return;
        var peers = targets.Nodes.Where(p => p.Value.Enabled && p.Value.ProtocolVersion == 2
            && config.Peers.TryGetValue(p.Key, out var peer)
            && NodeAdmissionPolicy.Allows(peer, NodeHandshakeService.Operation)
            && peer.AllowedOperations.Contains(NodeHandshakeService.Operation, StringComparer.Ordinal))
            .Select(p => p.Key).ToArray();
        if (peers.Length == 0) return;
        var offset = _nextPeer % peers.Length;
        peers = peers.Skip(offset).Concat(peers.Take(offset)).ToArray();
        var observations = new string?[peers.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, peers.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (index, cancellation) =>
            {
                var peer = peers[index];
                NodeProbeObservation? started = null;
                try
                {
                    started = observer.CaptureProbe(peer);
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    budget.CancelAfter(HeartbeatBudget);
                    var remote = await probe.CaptureAsync(peer, budget.Token);
                    budget.Token.ThrowIfCancellationRequested();
                    if (!Eligible(peer)) return;
                    var now = DateTimeOffset.UtcNow;
                    var observed = observer.ObserveAuthenticated(new(2, started.ClusterId, peer, started.NodeId,
                        Guid.NewGuid().ToString("D"), "heartbeat", NodeHandshakeService.Operation, now, now.AddMinutes(1),
                        JsonSerializer.SerializeToElement(remote, NodeMessageAuthentication.JsonOptions)));
                    if (!observer.CanStartTasks(peer, observed.ObservationId) && observed.State != "IdentityConflict")
                        observations[index] = observed.ObservationId;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    try { if (started is not null) observer.MarkDisconnected(started); }
                    catch (Exception storageError)
                    { logger.LogWarning("Node handshake observation could not be invalidated: {ErrorType}", storageError.GetType().Name); }
                    logger.LogWarning("Node {NodeId} handshake failed: {ErrorType}", peer, ex.GetType().Name);
                }
            });
        // Refresh existing membership even when admission is saturated. Admit in stable order,
        // resuming at the first excluded peer rather than allowing fast probes to cut ahead.
        for (var i = 0; i < peers.Length; i++)
            if (observations[i] is { } id) work.UpdateExisting(peers[i], id);
        for (var i = 0; i < peers.Length; i++)
        {
            if (observations[i] is { } id && !work.TryEnqueue(peers[i], id))
            {
                _nextPeer = (offset + i) % peers.Length;
                return;
            }
        }
        _nextPeer = (offset + 1) % peers.Length;
    }

    private async Task ReconcileAsync(string peer, string observationId, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Eligible(peer)) return;
            var current = observer.Get(peer);
            if (current is null || current.ObservationId != observationId || current.State == "IdentityConflict"
                || observer.CanStartTasks(peer, observationId)) return;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(RecoveryBudget);
            await recovery.ReconcileAsync(peer, observationId, budget.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Failed recovery does not mean the independent heartbeat failed.
            logger.LogWarning("Node {NodeId} reconciliation failed: {ErrorType}", peer, ex.GetType().Name);
        }
    }

    private sealed class RecoveryQueue
    {
        private readonly NodeHandshakeMonitor _owner;
        private readonly object _gate = new();
        // Membership includes running work, preventing concurrent reconciliation of one peer.
        private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
        private readonly Channel<string> _queue;
        public Task Completion { get; }

        public RecoveryQueue(NodeHandshakeMonitor owner, CancellationToken token)
        {
            _owner = owner;
            _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(owner.RecoveryCapacity)
            { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
            Completion = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => RunAsync(token))));
        }

        public void UpdateExisting(string peer, string observationId)
        {
            lock (_gate)
                if (_pending.ContainsKey(peer)) _pending[peer] = observationId;
        }

        public bool TryEnqueue(string peer, string observationId)
        {
            lock (_gate)
            {
                if (_pending.ContainsKey(peer)) { _pending[peer] = observationId; return true; }
                if (_pending.Count >= _owner.RecoveryCapacity) return false;
                _pending.Add(peer, observationId);
                if (_queue.Writer.TryWrite(peer)) return true;
                _pending.Remove(peer);
                return false;
            }
        }

        public void Complete() => _queue.Writer.TryComplete();

        private async Task RunAsync(CancellationToken token)
        {
            try
            {
                await foreach (var peer in _queue.Reader.ReadAllAsync(token))
                {
                    string observationId;
                    lock (_gate) observationId = _pending[peer];
                    try { await _owner.ReconcileAsync(peer, observationId, token); }
                    finally { lock (_gate) _pending.Remove(peer); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }
}
