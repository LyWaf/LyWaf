using System.Text.Json;
using Microsoft.Extensions.Options;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.Distribution;

/// <summary>控制节点调用目标节点两阶段分发协议的可替换客户端接口。</summary>
public interface IDistributionNodeClient
{
    Task<NodeAcknowledgement> PrepareAsync(string nodeId, PrepareConfigurationRequest request, CancellationToken cancellationToken);
    Task<NodeAcknowledgement> CommandAsync(string nodeId, string operation, DistributionCommandRequest request, CancellationToken cancellationToken);
    Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken cancellationToken);
    Task<NodeDashboardSnapshot> GetDashboardAsync(string nodeId, CancellationToken cancellationToken);
    IReadOnlyList<NodeOutboxRecord> ReadOutbox() => throw new NotSupportedException("This client has no durable outbox");
    Task<NodeAcknowledgement> RecoverAsync(string messageId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client has no durable outbox");
    Task<NodeAcknowledgement> QueryRecordedOutcomeAsync(NodeOutboxRecord record, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This client has no read-only result transport");
}

/// <summary>使用带 HMAC 签名的 HTTP 请求实现节点间分发协议。</summary>
public sealed class DistributionNodeClient(
    IOptionsMonitor<DistributionOptions> options,
    IHttpClientFactory clients,
    IOptionsMonitor<NodeCommunicationOptions>? communication = null,
    NodeRegistrationObserver? registration = null,
    NodeSocketConnections? sockets = null) : IDistributionNodeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<NodeOutboxRecord> ReadOutbox() => new NodeCommandOutbox(
        communication?.CurrentValue.StateDirectory ?? throw new InvalidOperationException("NodeCommunication is not configured")).ReadRecords();

    // Explicit recovery, not a background deployment resume: never starts an unattempted command.
    public Task<NodeAcknowledgement> RecoverAsync(string messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = communication?.CurrentValue ?? throw new InvalidOperationException("NodeCommunication is not configured");
        var record = ReadOutbox().Single(r => r.Message.MessageId == messageId
            && r.Message.ClusterId == config.ClusterId && r.Message.SenderNodeId == config.NodeId);
        if (record.Attempts == 0) throw new InvalidOperationException("Unsent command requires explicit business resumption");
        return SendAsync<JsonElement, NodeAcknowledgement>(record.Message.ReceiverNodeId,
            record.Message.Type["configuration.".Length..], record.Message.Payload, cancellationToken, record.Message);
    }

    public Task<NodeAcknowledgement> PrepareAsync(string nodeId, PrepareConfigurationRequest request, CancellationToken cancellationToken) =>
        SendAsync<PrepareConfigurationRequest, NodeAcknowledgement>(nodeId, "prepare", request, cancellationToken);

    // Explicit operator query: the reconciliation service audits and bounds this separately from automatic recovery.
    public Task<NodeAcknowledgement> QueryRecordedOutcomeAsync(NodeOutboxRecord record, CancellationToken cancellationToken)
    {
        var nodeId = record.Message.ReceiverNodeId;
        if (record.Attempts < 1 || record.Archived || record.Message.Type is not
            ("configuration.prepare" or "configuration.commit" or "configuration.rollback")
            || !options.CurrentValue.Nodes.TryGetValue(nodeId, out var node) || !node.Enabled
            || node.ProtocolVersion != 2 || node.Endpoint != record.Endpoint)
            throw new InvalidOperationException("Recorded query target is unavailable or changed");
        return SendAsync<JsonElement, NodeAcknowledgement>(nodeId, record.Message.Type["configuration.".Length..],
            record.Message.Payload, cancellationToken, record.Message, bypassOutbox: true, queryOnly: true, expectedEndpoint: record.Endpoint);
    }

    public Task<NodeAcknowledgement> CommandAsync(string nodeId, string operation, DistributionCommandRequest request, CancellationToken cancellationToken) =>
        SendAsync<DistributionCommandRequest, NodeAcknowledgement>(nodeId, operation, request, cancellationToken);

    public Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken cancellationToken) =>
        SendAsync<DistributionCommandRequest, DistributionNodeStatus>(nodeId, "status", new("status"), cancellationToken);

    public Task<NodeDashboardSnapshot> GetDashboardAsync(string nodeId, CancellationToken cancellationToken) =>
        SendAsync<DistributionCommandRequest, NodeDashboardSnapshot>(nodeId, "dashboard", new("dashboard"), cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string nodeId, string operation, TRequest payload, CancellationToken cancellationToken,
        NodeMessage? persisted = null, bool bypassOutbox = false, bool queryOnly = false, string? expectedEndpoint = null)
    {
        var current = options.CurrentValue;
        var node = current.Nodes[nodeId];
        if (registration is not null && communication?.CurrentValue is { Enabled: true, Role: "Controller" }
            && node.ProtocolVersion == 2 && operation is "prepare" or "commit" && !queryOnly
            && (persisted is null || bypassOutbox))
        {
            var observation = registration.Get(nodeId);
            if (observation is null || !registration.CanStartTasks(nodeId, observation.ObservationId))
                throw new InvalidOperationException("Node handshake reconciliation is required before starting commands");
        }
        if (communication?.CurrentValue is { Enabled: true } admission
            && admission.Peers.TryGetValue(nodeId, out var admittedPeer)
            && !NodeAdmissionPolicy.Allows(admittedPeer, queryOnly ? "configuration.result"
                : operation is "status" or "dashboard" ? "node." + operation : "configuration." + operation))
            throw new InvalidOperationException("Node admission state does not allow this operation");
        if (expectedEndpoint is not null && (node.Endpoint != expectedEndpoint || !node.Enabled || node.ProtocolVersion != 2))
            throw new InvalidOperationException("Recorded query target is unavailable or changed");
        if (persisted is not null && node.ProtocolVersion != 2)
            throw new InvalidOperationException("Persisted v2 command cannot downgrade");
        if (typeof(TResponse) == typeof(NodeAcknowledgement) && operation is not ("prepare" or "commit" or "rollback"))
            throw new ArgumentException("Unsupported configuration operation", nameof(operation));
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        NodeMessage? message = null;
        var secret = string.IsNullOrWhiteSpace(node.Secret) ? current.SharedSecret : node.Secret;
        var path = $"/api/v1/distribution/agent/{operation}";
        var keyId = "";
        if (node.ProtocolVersion == 2)
        {
            var config = communication?.CurrentValue ?? throw new InvalidOperationException("NodeCommunication is not configured");
            if (!config.Enabled || !config.Peers.TryGetValue(nodeId, out var peer) || !peer.Enabled
                || !peer.Keys.TryGetValue(peer.ActiveKeyId, out secret) || string.IsNullOrEmpty(secret)
                || System.Text.Encoding.UTF8.GetByteCount(secret) < 32)
                throw new InvalidOperationException("Node v2 credentials are not configured");
            if (!Uri.TryCreate(node.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.AbsolutePath != "/"
                || endpoint.Scheme is not ("http" or "https")
                || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0
                || (config.RequireHttps && endpoint.Scheme != "https"))
                throw new InvalidOperationException("Invalid node v2 endpoint");
            keyId = peer.ActiveKeyId;
            var type = operation is "status" or "dashboard" ? "node." + operation : "configuration." + operation;
            if (!NodeAdmissionPolicy.Allows(peer, queryOnly ? "configuration.result" : type))
                throw new InvalidOperationException("Node admission state does not allow this operation");
            var correlation = payload is PrepareConfigurationRequest prepare ? prepare.DeploymentId
                : payload is DistributionCommandRequest command ? command.DeploymentId : operation;
            var now = DateTimeOffset.UtcNow;
            var messageId = operation is "status" or "dashboard" ? Guid.NewGuid().ToString("D")
                : new Guid(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                    new[] { config.ClusterId, config.NodeId, nodeId, type, correlation })).AsSpan(0, 16)).ToString("D");
            message = new(2, config.ClusterId, config.NodeId, nodeId, messageId, correlation,
                type, now, now.AddMinutes(5), JsonSerializer.SerializeToElement(payload, JsonOptions));
            if (persisted is not null)
            {
                if (persisted.ClusterId != config.ClusterId || persisted.SenderNodeId != config.NodeId
                    || persisted.ReceiverNodeId != nodeId || persisted.Type != type)
                    throw new InvalidOperationException("Persisted command identity mismatch");
                message = persisted;
            }
            if (operation is "prepare" or "commit" or "rollback" && !bypassOutbox)
            {
                var acknowledgementResult = await new NodeCommandOutbox(config.StateDirectory).ExecuteAsync(message, node.Endpoint,
                    (stored, query, token) => SendAsync<TRequest, NodeAcknowledgement>(nodeId, operation, payload, token,
                        stored, bypassOutbox: true, queryOnly: query), cancellationToken, recoveryOnly: persisted is not null);
                return (TResponse)(object)acknowledgementResult;
            }
            if (queryOnly)
                message = new(2, config.ClusterId, config.NodeId, nodeId, Guid.NewGuid().ToString("D"),
                    message.CorrelationId, "configuration.result", now, now.AddMinutes(1), JsonSerializer.SerializeToElement(message, JsonOptions));
            body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            path = NodeMessageAuthentication.Prefix + message.Type;
        }
        else if (node.ProtocolVersion != 1 || communication?.CurrentValue is { Enabled: true, AllowLegacyProtocol: false })
            throw new InvalidOperationException("Legacy node protocol is disabled or unsupported");
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException($"节点 {nodeId} 未配置密钥");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            node.Endpoint.TrimEnd('/') + path)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json");
        if (message is null) DistributionAuthentication.Sign(request, operation, secret, body);
        else NodeMessageAuthentication.Sign(request, message, keyId, secret, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(current.RequestTimeoutSeconds, 1, 120)));
        using var response = await NodeControlTransport.SendAsync(communication?.CurrentValue, sockets, nodeId,
            clients.CreateClient("distribution-node"), request, timeout.Token);
        var businessFailure = typeof(TResponse) == typeof(NodeAcknowledgement)
            && response.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.UnprocessableEntity;
        if (!response.IsSuccessStatusCode && !businessFailure)
        {
            throw await NodeRemoteError.ReadAsync(response, timeout.Token);
        }
        var maxResponseBytes = typeof(TResponse) == typeof(NodeAcknowledgement) ? NodeRemoteError.MaximumBodyBytes : 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxResponseBytes)
            throw new InvalidDataException($"节点 {nodeId} 响应过大");
        await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (output.Length <= maxResponseBytes)
        {
            var remaining = Math.Min(buffer.Length, maxResponseBytes + 1 - (int)output.Length);
            var read = await responseStream.ReadAsync(buffer.AsMemory(0, remaining), timeout.Token);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        if (output.Length > maxResponseBytes) throw new InvalidDataException($"节点 {nodeId} 响应过大");
        var responseBody = output.ToArray();
        if (businessFailure)
        {
            bool hasAcknowledgement;
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                hasAcknowledgement = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("ack", out _);
                if (hasAcknowledgement && (document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)
                    || document.RootElement.GetProperty("ack").ValueKind != JsonValueKind.False))
                    throw new InvalidDataException("Invalid business rejection acknowledgement");
            }
            catch (JsonException) { hasAcknowledgement = false; }
            if (!hasAcknowledgement)
            {
                response.Content.Dispose();
                response.Content = new ByteArrayContent(responseBody);
                throw await NodeRemoteError.ReadAsync(response, timeout.Token);
            }
        }
        var result = JsonSerializer.Deserialize<TResponse>(responseBody, JsonOptions);
        if (result == null) throw new InvalidDataException($"节点 {nodeId} 返回空响应");
        if (result is NodeAcknowledgement acknowledgement)
        {
            if (message is not null)
            {
                using var document = JsonDocument.Parse(responseBody);
                if (!document.RootElement.TryGetProperty("ack", out var ackField)
                    || ackField.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || document.RootElement.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
                    throw new InvalidDataException("Invalid node acknowledgement shape");
            }
            var deploymentId = persisted?.CorrelationId ?? (payload is PrepareConfigurationRequest prepare ? prepare.DeploymentId
                : payload is DistributionCommandRequest command ? command.DeploymentId : null);
            if (!string.Equals(acknowledgement.NodeId, nodeId, StringComparison.Ordinal)
                || !string.Equals(acknowledgement.DeploymentId, deploymentId, StringComparison.Ordinal)
                || !string.Equals(acknowledgement.Stage, operation, StringComparison.Ordinal))
                throw new InvalidDataException("Node acknowledgement identity or operation mismatch");
            if (!acknowledgement.Ack && message is not null)
            {
                if (!NodeConfigurationErrors.IsKnown(acknowledgement.ErrorCode)
                    || acknowledgement.ErrorCode == NodeConfigurationErrors.Unknown)
                    throw new InvalidDataException("Unclassified node business outcome requires reconciliation");
                if (businessFailure && NodeConfigurationErrors.HttpStatus(acknowledgement.ErrorCode) != (int)response.StatusCode)
                    throw new InvalidDataException("Business rejection status mismatch");
                // Controller persists Error for legacy target models. Never propagate remote prose into it.
                result = (TResponse)(object)(acknowledgement with { Error = acknowledgement.ErrorCode,
                    MessageKey = "node.errors." + acknowledgement.ErrorCode });
            }
        }
        return result;
    }
}
