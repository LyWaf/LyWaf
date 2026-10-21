using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.Distribution;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record PluginNodeReceiveResult(PluginNodeMessageReply? Reply, NodeProtocolError? Error);

/// <summary>Only the host can bind a plugin identity or dispatch an authenticated envelope.</summary>
public sealed partial class PluginNodeMessageService(
    IPluginManager manager,
    IOptionsMonitor<PluginNodeMessageOptions> options,
    IOptionsMonitor<NodeCommunicationOptions> communication,
    IOptionsMonitor<DistributionOptions> distribution,
    IHttpClientFactory clients,
    NodeMessageAuthentication authentication,
    TimeProvider? clock = null,
    NodeSocketConnections? sockets = null)
{
    public const string Operation = "plugin.message";
    internal const int MaximumPayloadBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private readonly SemaphoreSlim _slots = new(16, 16);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _rateGate = new();
    private readonly Dictionary<(string Peer, string Plugin, string Direction), (long Minute, int Count)> _rates = new();

    internal IPluginNodeMessageClient Bind(string pluginId) => new BoundClient(this, pluginId);
    private sealed class BoundClient(PluginNodeMessageService host, string pluginId) : IPluginNodeMessageClient
    {
        public Task<IReadOnlyList<PluginNodeDelivery>> SendAsync(string messageType, int schemaVersion, string targetGroup,
            JsonElement payload, string messageId, CancellationToken cancellationToken) => host.SendAsync(pluginId, messageType, schemaVersion, targetGroup, payload, messageId, cancellationToken);
    }

    // Standalone host/test entry point. The shared node router should use the authenticated dispatcher below.
    public async Task<IResult> ReceiveAsync(HttpContext context)
    {
        var (message, error) = await authentication.ReadAsync(context, Operation, context.RequestAborted);
        if (error is not null) return NodeProtocolResponse.Error(context, error);
        var received = await HandleAuthenticatedAsync(message!, context.RequestAborted);
        return received.Error is { } rejected ? NodeProtocolResponse.Error(context, rejected) : Results.Json(received.Reply);
    }

    internal async Task<PluginNodeReceiveResult> HandleAuthenticatedAsync(NodeMessage message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var config = communication.CurrentValue;
        if (!options.CurrentValue.Enabled || !config.Enabled || message.Type != Operation || message.ClusterId != config.ClusterId
            || message.ReceiverNodeId != config.NodeId || message.ExpiresAt <= DateTimeOffset.UtcNow
            || !Guid.TryParseExact(message.MessageId, "D", out _) || !config.Peers.TryGetValue(message.SenderNodeId, out var peer)
            || !NodeAdmissionPolicy.Allows(peer, Operation) || !peer.AllowedOperations.Contains(Operation, StringComparer.Ordinal))
            return Reject("node_plugin_forbidden", 403);
        PluginNodeMessagePayload? payload;
        try { payload = message.Payload.Deserialize<PluginNodeMessagePayload>(Json); }
        catch (JsonException) { return Reject("node_plugin_invalid_payload", 400); }
        if (payload is null || message.CorrelationId != payload.PluginId || !ValidPayload(payload)) return Reject("node_plugin_invalid_payload", 400);
        var origin = message.SenderNodeId;
        if (payload.OriginNodeId is { } forwarded)
        {
            if (config.Mode != "Node" || config.Role != "Node" || origin != config.ControllerNodeId || peer.Role != "Controller")
                return Reject("node_plugin_forbidden", 403);
            origin = forwarded;
        }
        if (!TakeRate(message.SenderNodeId, payload.PluginId, "receive") || !await _slots.WaitAsync(0, token))
            return Reject("node_plugin_rate_limited", 429);
        var releaseSlot = true;
        try
        {
            var journal = new PluginNodeMessageJournal(config.StateDirectory, "incoming");
            using var gate = journal.Lock(message);
            var old = journal.Read(message);
            if (old is not null)
            {
                if (old.Digest != PluginNodeMessageJournal.Digest(message)) return Reject("node_plugin_message_conflict", 409);
                return old.Result is null ? Reject("node_plugin_outcome_unknown", 409)
                    : old.StatusCode is { } status ? Reject(old.Result.ErrorCode!, status) : new(old.Result, null);
            }
            var audit = Audit(message, payload);
            journal.Save(message, audit);
            var denial = Authorize(payload, send: false, origin, out var plugin);
            if (denial is not null)
            {
                var reply = Reply(message, payload, false, denial.Code);
                journal.Save(message, audit with { State = "Completed", Result = reply, StatusCode = denial.StatusCode });
                return new(null, denial);
            }
            if (plugin is not IPluginNodeMessageHandler handler)
            {
                var reply = Reply(message, payload, false, "node_plugin_missing");
                journal.Save(message, audit with { State = "Completed", Result = reply, StatusCode = 404 });
                return Reject("node_plugin_missing", 404);
            }
            token.ThrowIfCancellationRequested();
            var remaining = message.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return Reject("node_message_expired", 409);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10));
            var received = new PluginNodeReceivedMessage(message.MessageId, origin, payload.MessageType,
                payload.SchemaVersion, payload.TargetGroup, payload.Data.Clone());
            var handlerToken = deadline.Token;
            var running = Task.Run(() => handler.HandleNodeMessageAsync(received, handlerToken), CancellationToken.None);
            PluginNodeHandlerResult result;
            try { result = await running.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) when (!running.IsCompleted)
            {
                // An uncooperative handler retains its slot until it really stops; cancellation cannot bypass concurrency limits.
                releaseSlot = false;
                _ = running.ContinueWith(task => { _ = task.Exception; _slots.Release(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            if (result is null || (result.Ack ? result.ErrorCode is not null : !ValidCode(result.ErrorCode)))
                return Reject("node_plugin_outcome_unknown", 409);
            var completed = Reply(message, payload, result.Ack, result.ErrorCode);
            journal.Save(message, audit with { State = "Completed", Result = completed });
            return new(completed, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return Reject("node_plugin_store_unavailable", 503); }
        catch (Exception) { return Reject("node_plugin_outcome_unknown", 409); }
        finally { if (releaseSlot) _slots.Release(); }
    }

    private async Task<IReadOnlyList<PluginNodeDelivery>> SendAsync(string pluginId, string type, int schema,
        string group, JsonElement data, string messageId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(messageId, "D", out var businessId) || businessId.ToString("D") != messageId)
            throw new ArgumentException("A stable lowercase UUID is required for plugin node messages");
        var payload = new PluginNodeMessagePayload(pluginId, type, schema, group, data);
        if (!ValidPayload(payload)) throw new ArgumentException("Invalid plugin node payload");
        var denied = Authorize(payload, send: true, null, out _);
        if (denied is not null) throw new InvalidOperationException(denied.Code);
        var config = communication.CurrentValue;
        var members = options.CurrentValue.Groups[group].Where(id => id != config.NodeId).Distinct(StringComparer.Ordinal).ToArray();
        if (members.Length == 0 || members.Length > 16) throw new InvalidOperationException("node_plugin_forbidden");
        // Validate the complete group before sending any member. Plugins cannot supply an address or key.
        var targets = members.Select(id => Target(id, config)).ToArray();
        var id = new Guid(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { pluginId, messageId })).AsSpan(0, 16)).ToString("D");
        var manifest = new NodeMessage(2, config.ClusterId, config.NodeId, config.NodeId, id, pluginId, Operation,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30), JsonSerializer.SerializeToElement(new
            { Request = payload, Targets = targets.Select(t => new { t.NodeId, t.Endpoint }).OrderBy(t => t.NodeId, StringComparer.Ordinal).ToArray() }, Json));
        var manifests = new PluginNodeMessageJournal(config.StateDirectory, "groups");
        using var manifestGate = manifests.Lock(manifest);
        var prior = manifests.Read(manifest);
        if (prior is not null && prior.Digest != PluginNodeMessageJournal.Digest(manifest))
            throw new InvalidOperationException("node_plugin_message_conflict");
        if (prior is null) manifests.Save(manifest, Audit(manifest, payload));
        var results = new PluginNodeDelivery[targets.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, targets.Length), new ParallelOptions
        { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, ct) =>
        {
            var target = targets[index];
            var now = DateTimeOffset.UtcNow;
            var message = new NodeMessage(2, config.ClusterId, config.NodeId, target.NodeId, id,
                pluginId, Operation, now, now.AddSeconds(30), JsonSerializer.SerializeToElement(payload, Json));
            results[index] = await SendOneAsync(message, payload, target, config.StateDirectory, ct);
        });
        return results;
    }

    private sealed record TargetPeer(string NodeId, string Endpoint, string KeyId, string Secret);
    private TargetPeer Target(string id, NodeCommunicationOptions config)
    {
        if (config.Mode == "Node")
        {
            if (!NodeMessageAuthentication.ValidId(id) || id == config.NodeId)
                throw new InvalidOperationException("node_plugin_forbidden");
            var controller = DirectTarget(config.ControllerNodeId, config, id == config.ControllerNodeId ? Operation : RelayOperation);
            return controller with { NodeId = id };
        }
        return DirectTarget(id, config, Operation);
    }

    private TargetPeer DirectTarget(string id, NodeCommunicationOptions config, string operation)
    {
        if (!config.Enabled || !NodeMessageAuthentication.ValidId(config.ClusterId) || !NodeMessageAuthentication.ValidId(config.NodeId)
            || !distribution.CurrentValue.Nodes.TryGetValue(id, out var target) || !target.Enabled || target.ProtocolVersion != 2
            || !config.Peers.TryGetValue(id, out var peer) || !NodeAdmissionPolicy.Allows(peer, operation) || !peer.AllowedOperations.Contains(operation, StringComparer.Ordinal)
            || !peer.Keys.TryGetValue(peer.ActiveKeyId, out var key) || string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) < 32
            || !Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.AbsolutePath != "/"
            || endpoint.Scheme is not ("https" or "http") || (config.RequireHttps && endpoint.Scheme != "https")
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("node_plugin_forbidden");
        return new(id, target.Endpoint, peer.ActiveKeyId, key);
    }

    private async Task<PluginNodeDelivery> SendOneAsync(NodeMessage message, PluginNodeMessagePayload payload,
        TargetPeer target, string directory, CancellationToken token)
    {
        if (!TakeRate(target.NodeId, payload.PluginId, "send") || !await _slots.WaitAsync(0, token))
            return new(target.NodeId, message.MessageId, false, "node_plugin_rate_limited");
        try
        {
            var journal = new PluginNodeMessageJournal(directory, "outgoing");
            using var gate = journal.Lock(message);
            var existing = journal.Read(message);
            if (existing is not null)
            {
                if (existing.Digest != PluginNodeMessageJournal.Digest(message))
                    return new(target.NodeId, message.MessageId, false, "node_plugin_message_conflict");
                return existing.Result is null ? new(target.NodeId, message.MessageId, false, "node_plugin_outcome_unknown")
                    : new(target.NodeId, message.MessageId, existing.Result.Ack, existing.Result.ErrorCode);
            }
            var audit = Audit(message, payload);
            journal.Save(message, audit);
            if (Authorize(payload, send: true, null, out _) is not null
                || !options.CurrentValue.Groups[payload.TargetGroup].Contains(target.NodeId, StringComparer.Ordinal)
                || Target(target.NodeId, communication.CurrentValue) != target)
                return new(target.NodeId, message.MessageId, false, "node_plugin_forbidden");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var config = communication.CurrentValue;
            var relay = config.Mode == "Node" && target.NodeId != config.ControllerNodeId;
            var wire = relay ? message with { ReceiverNodeId = config.ControllerNodeId, Type = RelayOperation,
                Payload = JsonSerializer.SerializeToElement(new PluginNodeRelayPayload(target.NodeId, payload), Json) } : message;
            using var request = new HttpRequestMessage(HttpMethod.Post, target.Endpoint.TrimEnd('/') + NodeMessageAuthentication.Prefix + wire.Type);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(wire, Json);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new("application/json");
            NodeMessageAuthentication.Sign(request, wire, target.KeyId, target.Secret, bytes);
            using var response = await NodeControlTransport.SendAsync(config, sockets, wire.ReceiverNodeId,
                clients.CreateClient("node-plugin-messages"), request, deadline.Token);
            if (!response.IsSuccessStatusCode) throw await NodeRemoteError.ReadAsync(response, deadline.Token);
            if (response.Content.Headers.ContentLength > 16384) throw new InvalidDataException("Plugin reply too large");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            while (body.Length <= 16384)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 16385 - (int)body.Length)), deadline.Token);
                if (count == 0) break;
                body.Write(buffer, 0, count);
            }
            if (body.Length > 16384) throw new InvalidDataException("Plugin reply too large");
            var reply = JsonSerializer.Deserialize<PluginNodeMessageReply>(body.ToArray(), Json);
            if (reply is null || reply.PluginId != payload.PluginId || reply.MessageType != payload.MessageType
                || reply.SchemaVersion != payload.SchemaVersion || reply.NodeId != target.NodeId || reply.MessageId != message.MessageId
                || (reply.Ack ? reply.ErrorCode is not null : !ValidCode(reply.ErrorCode))) throw new InvalidDataException("Plugin reply identity mismatch");
            journal.Save(message, audit with { State = "Completed", Result = reply });
            return new(target.NodeId, message.MessageId, reply.Ack, reply.ErrorCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var code = error is InvalidDataException or JsonException ? "node_invalid_response"
                : error is IOException or UnauthorizedAccessException ? "node_plugin_store_unavailable" : NodeFailure.FromException(error).Code;
            return new(target.NodeId, message.MessageId, false, code);
        }
        finally { _slots.Release(); }
    }

    private NodeProtocolError? Authorize(PluginNodeMessagePayload payload, bool send, string? sender, out ILyWafPlugin? plugin)
    {
        plugin = manager.GetPlugin(payload.PluginId);
        var config = options.CurrentValue;
        if (!config.Enabled || !communication.CurrentValue.Enabled) return new("node_plugin_forbidden", 403);
        if (plugin is null || manager.GetPluginState(payload.PluginId) != PluginState.Running) return new("node_plugin_missing", 404);
        if (!plugin.Metadata.RequestedPermissions.HasFlag(PluginPermission.NodeMessaging)
            || !manager.IsPermissionGranted(payload.PluginId, PluginPermission.NodeMessaging)) return new("node_plugin_forbidden", 403);
        var declarations = plugin.Metadata.NodeMessages.Where(c => c.MessageType == payload.MessageType && c.SchemaVersion == payload.SchemaVersion).ToArray();
        if (declarations.Length != 1) return new("node_plugin_schema_unsupported", 422);
        var declaration = declarations[0];
        if (!(send ? declaration.CanSend : declaration.CanReceive) || !declaration.TargetGroups.Contains(payload.TargetGroup, StringComparer.Ordinal)
            || !config.Grants.TryGetValue(payload.PluginId, out var grant) || !config.Groups.TryGetValue(payload.TargetGroup, out var members)
            || members.Length is < 1 or > 16 || members.Any(id => !NodeMessageAuthentication.ValidId(id))
            || !(send ? grant.SendGroups : grant.ReceiveGroups).Contains(payload.TargetGroup, StringComparer.Ordinal)
            || (!send && (!members.Contains(communication.CurrentValue.NodeId, StringComparer.Ordinal)
                || !grant.AllowedSenders.Contains(sender!, StringComparer.Ordinal)))) return new("node_plugin_forbidden", 403);
        return null;
    }

    private bool TakeRate(string peer, string plugin, string direction)
    {
        lock (_rateGate)
        {
            var minute = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
            foreach (var key in _rates.Where(p => p.Value.Minute != minute).Select(p => p.Key).ToArray()) _rates.Remove(key);
            var id = (peer, plugin, direction);
            if (!_rates.TryGetValue(id, out var value) && _rates.Count >= 1024) return false;
            if (value.Count >= 60) return false;
            _rates[id] = (minute, value.Count + 1);
            return true;
        }
    }

    private static bool ValidPayload(PluginNodeMessagePayload payload)
    {
        if (!NodeMessageAuthentication.ValidId(payload.PluginId) || !NodeMessageAuthentication.ValidId(payload.MessageType)
            || !NodeMessageAuthentication.ValidId(payload.TargetGroup) || payload.SchemaVersion is < 1 or > 65535
            || (payload.OriginNodeId is not null && !NodeMessageAuthentication.ValidId(payload.OriginNodeId))
            || payload.Data.ValueKind != JsonValueKind.Object) return false;
        try { return JsonSerializer.SerializeToUtf8Bytes(payload.Data, Json).Length <= MaximumPayloadBytes; }
        catch (JsonException) { return false; }
    }
    private static bool ValidCode(string? code) => code is { Length: > 0 and <= 128 }
        && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
    private static PluginNodeReceiveResult Reject(string code, int status) => new(null, new(code, status));
    private static PluginNodeMessageReply Reply(NodeMessage m, PluginNodeMessagePayload p, bool ack, string? error = null) =>
        new(p.PluginId, p.MessageType, p.SchemaVersion, m.MessageId, m.ReceiverNodeId, ack, error);
    private static PluginNodeAudit Audit(NodeMessage m, PluginNodeMessagePayload p) => new(m.ClusterId, m.SenderNodeId,
        m.ReceiverNodeId, m.MessageId, p.PluginId, p.MessageType, p.SchemaVersion, p.TargetGroup,
        PluginNodeMessageJournal.Digest(m), DateTimeOffset.UtcNow, OriginNodeId: p.OriginNodeId);
}
