using System.Text.Json;
using Microsoft.Extensions.Options;
using NLog;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.RuntimeState;

/// <summary>
/// 将本地运行状态通过有界队列异步复制给对等节点，并周期性执行反熵广播以修复短暂断连造成的遗漏。
/// </summary>
public sealed class RuntimeStateReplicator : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RuntimeStateStore _store;
    private readonly IOptionsMonitor<RuntimeStateOptions> _options;
    private readonly RuntimeStateReplicationQueuePool _queuePool;
    private readonly RuntimeStateReplicationQueuePool.Queue _queue;
    private readonly int _queueCapacity;
    private readonly long _perQueueBytes;
    private readonly HttpClient _client;
    private readonly RuntimeStateSyncTransport _sync;
    private readonly IOptionsMonitor<NodeCommunicationOptions>? _communication;
    private readonly NodeSocketConnections? _sockets;
    private readonly Dictionary<string, PeerWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PeerWorker> _retired = [];
    private long _authorizationRejected;

    public long DroppedMutations => _queuePool.GetSnapshot().Dropped;
    public long CoalescedMutations => _queuePool.GetSnapshot().Coalesced;
    internal RuntimeStateReplicationQueuePool.Snapshot QueueStatus => _queuePool.GetSnapshot();
    public long AuthorizationRejectedMutations => Interlocked.Read(ref _authorizationRejected);

    private bool CanSend(RuntimeStateOptions options, string peer, RuntimeStateMutation mutation)
    {
        var operation = options.Peers.TryGetValue(peer, out var target) && target.DurableSyncEnabled
            ? RuntimeStateSyncProtocol.Operation : "runtime.merge";
        if (_store.IsCurrentForSync(mutation) && AllowsAdmission(peer, operation)
            && RuntimeStateReplicationAuthorization.AuthorizeSend(options, peer, mutation) is null) return true;
        Interlocked.Increment(ref _authorizationRejected);
        return false;
    }

    private bool AllowsAdmission(string peer, string operation)
    {
        var config = _communication?.CurrentValue;
        if (config is null) return true;
        return config.Peers.TryGetValue(peer, out var identity)
            ? NodeAdmissionPolicy.Allows(identity, operation) : !config.Enabled;
    }

    public RuntimeStateReplicator(RuntimeStateStore store, IOptionsMonitor<RuntimeStateOptions> options,
        IOptionsMonitor<NodeCommunicationOptions>? communication = null, NodeSocketConnections? sockets = null)
        : this(store, options, new SocketsHttpHandler { AllowAutoRedirect = false }, communication, sockets) { }

    internal RuntimeStateReplicator(RuntimeStateStore store, IOptionsMonitor<RuntimeStateOptions> options, HttpMessageHandler transport,
        IOptionsMonitor<NodeCommunicationOptions>? communication = null, NodeSocketConnections? sockets = null)
    {
        _client = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(10) };
        _store = store;
        _options = options;
        _communication = communication;
        _sockets = sockets;
        _sync = new(_client, options, communication, store, sockets: sockets);
        var limits = options.CurrentValue;
        if (!IsValidQueueConfiguration(limits)) throw new ArgumentException("Invalid replication queue limits", nameof(options));
        _queueCapacity = Math.Clamp(limits.QueueCapacity, 100, 1_000_000);
        _perQueueBytes = limits.ReplicationQueuePerQueueMaxBytes;
        _queuePool = new(limits.ReplicationQueueMaxEntries, limits.ReplicationQueueMaxBytes,
            limits.ReplicationQueueMaxEntries / 2, limits.ReplicationQueueMaxBytes / 2, limits.ReplicationMaxMutationBytes);
        _queue = _queuePool.CreateQueue(_queueCapacity, _perQueueBytes);
        _store.MutationPublished += OnMutation;
    }

    internal static bool IsValidQueueConfiguration(RuntimeStateOptions options) =>
        options.ReplicationQueueMaxEntries is >= 4 and <= 1_000_000 &&
        options.ReplicationQueueMaxBytes is >= 2048 and <= 1024L * 1024 * 1024 &&
        options.ReplicationQueuePerQueueMaxBytes is >= 2048 and <= 1024L * 1024 * 1024 &&
        options.ReplicationQueuePerQueueMaxBytes <= options.ReplicationQueueMaxBytes &&
        options.ReplicationMaxMutationBytes >= 1024 &&
        options.ReplicationMaxMutationBytes <= options.ReplicationQueuePerQueueMaxBytes / 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        var consume = ConsumeAsync(stoppingToken);
        var recover = _sync.RecoverAsync(stoppingToken);
        string? cursor = null;
        try
        {
            do
            {
                if (!_options.CurrentValue.Enabled) { cursor = null; continue; }
                var page = _store.ExportPage(cursor, Math.Clamp(_options.CurrentValue.QueueCapacity / 2, 1, 256));
                foreach (var mutation in page.Items) _queue.TryWrite(mutation, background: true);
                cursor = page.NextCursor;
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _queue.Complete();
            await consume;
            await recover;
        }
    }

    public override void Dispose()
    {
        _store.MutationPublished -= OnMutation;
        _queue.Complete();
        _client.Dispose();
        base.Dispose();
    }

    private void OnMutation(RuntimeStateMutation mutation)
    {
        if (_options.CurrentValue.Enabled) Enqueue(_queue, mutation);
    }

    private static void Enqueue(RuntimeStateReplicationQueuePool.Queue queue, RuntimeStateMutation mutation)
    {
        // Never block the request path. Anti-entropy repairs dropped updates.
        queue.TryWrite(mutation);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var mutation in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var current = _options.CurrentValue;
                    foreach (var id in _workers.Keys.ToArray())
                    {
                        if (current.Enabled && current.Peers.TryGetValue(id, out var peer) && peer.Enabled
                            && !id.Equals(current.NodeId, StringComparison.OrdinalIgnoreCase)) continue;
                        var removed = _workers[id];
                        _workers.Remove(id);
                        removed.Stop.Cancel();
                        removed.Queue.Complete();
                        _retired.Add(removed);
                    }
                    _retired.RemoveAll(worker =>
                    {
                        if (!worker.Completion.IsCompletedSuccessfully) return false;
                        worker.Stop.Dispose();
                        return true;
                    });
                    if (!current.Enabled) continue;
                    foreach (var pair in current.Peers.Where(x => x.Value.Enabled && !x.Key.Equals(current.NodeId, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!CanSend(current, pair.Key, mutation)) continue;
                        if (!_workers.TryGetValue(pair.Key, out var worker))
                        {
                            var queue = _queuePool.CreateQueue(_queueCapacity, _perQueueBytes);
                            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            worker = new PeerWorker(queue, stop, SendToPeerAsync(pair.Key, queue, stop));
                            _workers.Add(pair.Key, worker);
                        }
                        worker.Queue.TryWrite(mutation, background: true);
                    }
                }
                finally { _queue.ReleaseReadBatch(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            foreach (var worker in _workers.Values)
            {
                worker.Stop.Cancel();
                worker.Queue.Complete();
            }
            var all = _workers.Values.Concat(_retired).ToArray();
            try { await Task.WhenAll(all.Select(worker => worker.Completion)); }
            finally { foreach (var worker in all) worker.Stop.Dispose(); }
            _workers.Clear();
            _retired.Clear();
        }
    }

    private async Task SendToPeerAsync(string peerId, RuntimeStateReplicationQueuePool.Queue queue, CancellationTokenSource stop)
    {
        var cancellationToken = stop.Token;
        var failures = 0;
        long lastWarning = 0;
        try
        {
            await foreach (var mutation in queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var current = _options.CurrentValue;
                    if (!current.Enabled || !current.Peers.TryGetValue(peerId, out var peer) || !peer.Enabled) continue;
                    // Recheck all component sources after coalescing, including after a policy revocation.
                    if (!CanSend(current, peerId, mutation)) continue;
                    if (peer.DurableSyncEnabled)
                    {
                        await _sync.SendAsync(peerId, mutation, cancellationToken, queue.Reader);
                        failures = 0;
                        continue;
                    }
                    var body = JsonSerializer.SerializeToUtf8Bytes(mutation, JsonOptions);
                    var endpoint = peer.Endpoint.TrimEnd('/') + "/api/runtime-state/agent/merge";
                    NodeMessage? message = null;
                    var secret = string.IsNullOrWhiteSpace(peer.Secret) ? current.SharedSecret : peer.Secret;
                    var keyId = "";
                    if (peer.ProtocolVersion == 2)
                    {
                        var config = _communication?.CurrentValue;
                        if (config is not { Enabled: true } || config.NodeId != current.NodeId ||
                            !config.Peers.TryGetValue(peerId, out var identity) || !NodeAdmissionPolicy.Allows(identity, "runtime.merge") ||
                            !identity.Keys.TryGetValue(identity.ActiveKeyId, out secret) || string.IsNullOrEmpty(secret) ||
                            System.Text.Encoding.UTF8.GetByteCount(secret) < 32)
                            throw new InvalidOperationException("Runtime-state v2 identity is not configured");
                        if (!Uri.TryCreate(peer.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
                            (config.RequireHttps && uri.Scheme != "https"))
                            throw new InvalidOperationException("Invalid runtime-state v2 endpoint");
                        keyId = identity.ActiveKeyId;
                        var now = DateTimeOffset.UtcNow;
                        message = new(2, config.ClusterId, config.NodeId, peerId, Guid.NewGuid().ToString("D"), "runtime-state",
                            "runtime.merge", now, now.AddMinutes(5), JsonSerializer.SerializeToElement(mutation, JsonOptions));
                        (message, body) = RuntimeStateBatch.Pack(message, mutation, queue.Reader,
                            Math.Clamp(config.MaxMessageBytes, 1024, 16 * 1024 * 1024),
                            next => CanSend(current, peerId, next));
                        endpoint = peer.Endpoint.TrimEnd('/') + NodeMessageAuthentication.Prefix + message.Type;
                    }
                    else if (peer.ProtocolVersion != 1 || _communication?.CurrentValue is { Enabled: true, AllowLegacyProtocol: false })
                        throw new InvalidOperationException("Legacy runtime-state protocol is disabled or unsupported");
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new ByteArrayContent(body)
                    };
                    request.Content.Headers.ContentType = new("application/json");
                    if (message is null) RuntimeStateAuthentication.Sign(request, current.NodeId, secret, body);
                    else NodeMessageAuthentication.Sign(request, message, keyId, secret, body);
                    if (!AllowsAdmission(peerId, message?.Type ?? "runtime.merge"))
                        throw new InvalidOperationException("Runtime-state peer admission no longer permits replication");
                    using var response = await NodeControlTransport.SendAsync(_communication?.CurrentValue, _sockets,
                        peerId, _client, request, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                        Logger.Warn("运行状态复制被节点拒绝: {Peer}, {Status}", peerId, (int)response.StatusCode);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException or IOException or JsonException or UnauthorizedAccessException)
                {
                    if (lastWarning == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(lastWarning) >= TimeSpan.FromSeconds(30))
                    {
                        lastWarning = System.Diagnostics.Stopwatch.GetTimestamp();
                        Logger.Warn(ex, "运行状态复制失败: {Peer}", peerId);
                    }
                    failures = Math.Min(5, failures + 1);
                    await Task.Delay(TimeSpan.FromSeconds(failures), cancellationToken);
                }
                finally { queue.ReleaseReadBatch(); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private sealed record PeerWorker(RuntimeStateReplicationQueuePool.Queue Queue, CancellationTokenSource Stop, Task Completion);
}
