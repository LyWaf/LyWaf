using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Statistic;

public sealed class CcPeerQueryClient : IDisposable
{
    private readonly IOptionsMonitor<NodeCommunicationOptions> _communication;
    private readonly IOptionsMonitor<RuntimeStateOptions> _runtime;
    private readonly HttpClient _http;
    private readonly NodeSocketConnections? _sockets;
    private readonly NodeHubMembership? _membership;
    private readonly LyWaf.Plugins.Core.IPluginManager? _plugins;
    public CcPeerQueryClient(IOptionsMonitor<NodeCommunicationOptions> communication, IOptionsMonitor<RuntimeStateOptions> runtime,
        NodeSocketConnections? sockets = null, NodeHubMembership? membership = null, LyWaf.Plugins.Core.IPluginManager? plugins = null)
        : this(communication, runtime, new SocketsHttpHandler { AllowAutoRedirect = false }) { _sockets = sockets; _membership = membership; _plugins = plugins; }
    internal CcPeerQueryClient(IOptionsMonitor<NodeCommunicationOptions> communication, IOptionsMonitor<RuntimeStateOptions> runtime, HttpMessageHandler handler)
    { _communication = communication; _runtime = runtime; _http = new(handler) { Timeout = TimeSpan.FromSeconds(2) }; }

    internal static string Digest(CcExecutionQuery query) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(query, NodeMessageAuthentication.JsonOptions)));
    internal static string Sign(CcPeerReply reply, string secret) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
        JsonSerializer.SerializeToUtf8Bytes(reply with { Signature = "" }, NodeMessageAuthentication.JsonOptions)));

    public Task ProbeAsync(string node, CcExecutionQuery query, CcCohortState cohort, CancellationToken token)
        => ProbeCoreAsync(node, [query], cohort, false, token);

    public Task ProbeBatchAsync(string node, CcExecutionQuery[] queries, CcCohortState cohort, CancellationToken token)
        => ProbeCoreAsync(node, queries, cohort, true, token);

    private async Task ProbeCoreAsync(string node, CcExecutionQuery[] queries, CcCohortState cohort, bool batch, CancellationToken token)
    {
        try
        {
            var result = await QueryAsync(node, queries, batch, token);
            for (var i = 0; i < queries.Length; i++)
                cohort.Accept(queries[i].Policy, node, result.Endpoint, result.Replies[i], result.Sent, result.Received, result.ElapsedMs);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { cohort.Invalidate(node); throw; }
    }

    internal sealed record Observation(CcPeerReply[] Replies, string Endpoint, DateTimeOffset Sent, DateTimeOffset Received, double ElapsedMs);

    internal async Task<Observation> QueryAsync(string node, CcExecutionQuery[] queries, bool batch, CancellationToken token)
    {
        // ResponseHeadersRead ends HttpClient's timeout at headers; bound body reads as well.
        var callerToken = token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        token = deadline.Token;
        try
        {
            if (queries.Length is < 1 or > CcExecutionQueryService.MaxBatchItems)
                throw new InvalidOperationException("Invalid CC query batch size");
            var config = _communication.CurrentValue; var runtime = _runtime.CurrentValue;
            var relay = config.Mode == "Node" && node != config.ControllerNodeId;
            var signer = relay ? config.ControllerNodeId : node;
            var operation = relay ? CcExecutionRelayService.Operation : CcExecutionQueryService.Operation;
            PluginNodeRequests.EnsureSender(_plugins, LyWaf.Plugins.Sys.Analysis.AnalysisNodeRequests.Id, operation);
            if (relay && (config.Role != "Node" || !NodeMessageAuthentication.ValidId(node)
                || _membership is null || !_membership.IsKnown(config.ClusterId, signer, node)))
                throw new InvalidOperationException("CC query source is not endorsed by the controller");
            if (!config.Enabled || !runtime.Enabled || config.NodeId != runtime.NodeId
                || !runtime.Peers.TryGetValue(signer, out var target) || !target.Enabled || target.ProtocolVersion != 2
                || !config.Peers.TryGetValue(signer, out var peer) || !peer.Enabled || !NodeAdmissionPolicy.Allows(peer, operation)
                || !PluginNodeRequests.Allows(peer, operation)
                || !peer.Keys.TryGetValue(peer.ActiveKeyId, out var secret) || Encoding.UTF8.GetByteCount(secret) < 32
                || !Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                || config.RequireHttps && uri.Scheme != "https" || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
                throw new InvalidOperationException("CC query peer is not authorized");
            var sent = DateTimeOffset.UtcNow; var timer = Stopwatch.GetTimestamp();
            var endpoint = target.Endpoint;
            var keyId = peer.ActiveKeyId;
            var envelope = new NodeMessage(2, config.ClusterId, config.NodeId, signer, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("N"),
                operation, sent, sent.AddSeconds(10), relay
                    ? JsonSerializer.SerializeToElement(new CcExecutionRelayQuery(node, queries), NodeMessageAuthentication.JsonOptions) : batch
                    ? JsonSerializer.SerializeToElement(new CcExecutionBatch(queries), NodeMessageAuthentication.JsonOptions)
                    : JsonSerializer.SerializeToElement(queries[0], NodeMessageAuthentication.JsonOptions));
            if (config.Mode is "Controller" or "Node" && peer.AllowedOperations.Contains(PluginNodeRequests.Operation, StringComparer.Ordinal))
                envelope = PluginNodeRequests.Wrap(envelope, LyWaf.Plugins.Sys.Analysis.AnalysisNodeRequests.Id);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, NodeMessageAuthentication.JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, NodeMessageAuthentication.Prefix + envelope.Type))
            { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            NodeMessageAuthentication.Sign(request, envelope, keyId, secret, bytes);
            using var response = await NodeControlTransport.SendAsync(config, _sockets, signer, _http, request, token);
            if (!response.IsSuccessStatusCode) throw await NodeRemoteError.ReadAsync(response, token);
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream(); var chunk = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(chunk, token)) > 0)
            { if (buffer.Length + read > (batch || relay ? 256 : 64) * 1024) throw new InvalidDataException("Oversized CC query reply"); buffer.Write(chunk, 0, read); }
            var replies = batch || relay ? JsonSerializer.Deserialize<CcPeerBatchReply>(buffer.ToArray(), NodeMessageAuthentication.JsonOptions)?.Items
                : [JsonSerializer.Deserialize<CcPeerReply>(buffer.ToArray(), NodeMessageAuthentication.JsonOptions)!];
            if (replies is null || replies.Length != queries.Length) throw new InvalidDataException("Incomplete CC query batch");
            for (var i = 0; i < queries.Length; i++)
            {
            var query = queries[i]; var reply = replies[i];
            if (reply?.Evidence is not { } evidence || reply.Execution is null || reply.RequestId != envelope.MessageId
                || reply.RequestDigest != Digest(query) || reply.KeyId != keyId || reply.Signature is not { Length: 64 }
                || !reply.Signature.All(char.IsAsciiHexDigit)
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(reply.Signature), Convert.FromHexString(Sign(reply, secret)))
                || evidence.NodeId != node || !Guid.TryParseExact(evidence.BootId, "N", out _)
                || evidence.PolicyKey != query.Policy.PolicyKey || evidence.LocalCount < 0
                || evidence.WindowStart != query.WindowStart || reply.Execution.NodeId != node || reply.Execution.DecisionId != query.DecisionId)
                throw new InvalidDataException("CC query response binding mismatch");
            if (relay ? reply.ViaController != signer || reply.ControllerBootId != _membership!.BootId(config.ClusterId, signer)
                    || reply.ControllerBootId is null || reply.ReceivedAt is not null || reply.ProcessingMilliseconds is not null
                : reply.ViaController is not null || reply.ControllerBootId is not null)
                throw new InvalidDataException("CC query relay binding mismatch");
            }
            // Configuration changes during the request must not resurrect an earlier destination's observation.
            if (!_runtime.CurrentValue.Peers.TryGetValue(signer, out var current) || !current.Enabled || current.Endpoint != endpoint)
                throw new InvalidOperationException("CC query destination changed");
            var currentConfig = _communication.CurrentValue;
            if (!currentConfig.Enabled || currentConfig.ClusterId != envelope.ClusterId || currentConfig.NodeId != envelope.SenderNodeId
                || !currentConfig.Peers.TryGetValue(signer, out var currentPeer) || !NodeAdmissionPolicy.Allows(currentPeer, operation)
                || !PluginNodeRequests.Allows(currentPeer, operation)
                || !currentPeer.Keys.TryGetValue(keyId, out var currentSecret) || currentSecret != secret
                || (relay && currentConfig.ControllerNodeId != signer))
                throw new InvalidOperationException("CC query authorization changed");
            var received = DateTimeOffset.UtcNow; var elapsed = Stopwatch.GetElapsedTime(timer).TotalMilliseconds;
            return new(replies, endpoint, sent, received, elapsed);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
    }
    public void Dispose() => _http.Dispose();
}

public sealed class CcCohortMonitor(CcCohortState cohort, CcPeerQueryClient client, IStatisticService statistics,
    IRuntimeStateStore store) : BackgroundService
{
    private readonly Dictionary<string, (Task Work, long Started, int Cursor)> _peers = new(StringComparer.Ordinal);
    private readonly string _local = store.GetStatus().NodeId;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
        while (!stoppingToken.IsCancellationRequested)
        {
            cohort.ObserveRules(statistics.GetAdvancedCcRules());
            var tracked = cohort.Tracked();
            var groups = tracked.SelectMany(t => cohort.ExpectedNodes(t.Rule).Where(n => n != _local)
                .Select(n => (Node: n, t.Policy, t.Rule))).GroupBy(t => t.Node).ToArray();
            foreach (var retired in _peers.Keys.Except(groups.Select(g => g.Key)).ToArray())
                if (_peers[retired].Work.IsCompleted) _peers.Remove(retired);
            var available = Math.Max(0, 8 - _peers.Values.Count(p => !p.Work.IsCompleted));
            var ready = groups.Where(g =>
            {
                var previous = _peers.GetValueOrDefault(g.Key);
                return previous.Work is not { IsCompleted: false }
                    && (previous.Started == 0 || Stopwatch.GetElapsedTime(previous.Started).TotalMilliseconds >= 100);
            }).OrderBy(g => _peers.GetValueOrDefault(g.Key).Started).Take(available).ToArray();
            if (ready.Length == 0)
            {
                await Task.Delay(25, stoppingToken);
                continue;
            }
            var now = DateTimeOffset.UtcNow;
            // Deserialize decisions only when a batch can actually be sent.
            var active = store.GetValues<CcDecision>(CcDecisionService.DecisionDomain).Select(p => p.Value)
                .Where(d => d is not null && d.ExpiresAt > now).GroupBy(d => d.Policy.StateKey)
                .ToDictionary(g => g.Key, g => g.OrderBy(d => d.CreatedAt).First().DecisionId);
            foreach (var group in ready)
            {
                var previous = _peers.GetValueOrDefault(group.Key);
                var items = group.ToArray(); var count = Math.Min(CcExecutionQueryService.MaxBatchItems, items.Length);
                var queries = Enumerable.Range(0, count).Select(i =>
                {
                    var item = items[(previous.Cursor + i) % items.Length];
                    var window = CcDecisionService.Window(now, item.Rule.Period);
                    return new CcExecutionQuery(item.Policy, active.GetValueOrDefault(item.Policy.StateKey)
                        ?? CcDecisionService.DecisionId(item.Policy, window), window);
                }).ToArray();
                _peers[group.Key] = (Probe(group.Key, queries, stoppingToken), Stopwatch.GetTimestamp(), (previous.Cursor + count) % items.Length);
            }
            await Task.Delay(25, stoppingToken);
        }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(_peers.Values.Select(p => p.Work)); }
    }

    private async Task Probe(string node, CcExecutionQuery[] queries, CancellationToken token)
    {
        try { await client.ProbeBatchAsync(node, queries, cohort, token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or JsonException or OperationCanceledException)
        { /* Failures invalidate peer evidence; caller cancellation leaves existing evidence to expire normally. */ }
    }
}
