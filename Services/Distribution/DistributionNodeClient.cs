using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Distribution;

/// <summary>控制节点调用目标节点两阶段分发协议的可替换客户端接口。</summary>
public interface IDistributionNodeClient
{
    Task<NodeAcknowledgement> PrepareAsync(string nodeId, PrepareConfigurationRequest request, CancellationToken cancellationToken);
    Task<NodeAcknowledgement> CommandAsync(string nodeId, string operation, DistributionCommandRequest request, CancellationToken cancellationToken);
    Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken cancellationToken);
    Task<NodeDashboardSnapshot> GetDashboardAsync(string nodeId, CancellationToken cancellationToken);
}

/// <summary>使用带 HMAC 签名的 HTTP 请求实现节点间分发协议。</summary>
public sealed class DistributionNodeClient(
    IOptionsMonitor<DistributionOptions> options,
    IHttpClientFactory clients) : IDistributionNodeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<NodeAcknowledgement> PrepareAsync(string nodeId, PrepareConfigurationRequest request, CancellationToken cancellationToken) =>
        SendAsync<PrepareConfigurationRequest, NodeAcknowledgement>(nodeId, "prepare", request, cancellationToken);

    public Task<NodeAcknowledgement> CommandAsync(string nodeId, string operation, DistributionCommandRequest request, CancellationToken cancellationToken) =>
        SendAsync<DistributionCommandRequest, NodeAcknowledgement>(nodeId, operation, request, cancellationToken);

    public Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken cancellationToken) =>
        SendAsync<DistributionCommandRequest, DistributionNodeStatus>(nodeId, "status", new("status"), cancellationToken);

    public Task<NodeDashboardSnapshot> GetDashboardAsync(string nodeId, CancellationToken cancellationToken) =>
        SendAsync<DistributionCommandRequest, NodeDashboardSnapshot>(nodeId, "dashboard", new("dashboard"), cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string nodeId, string operation, TRequest payload, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var node = current.Nodes[nodeId];
        var secret = string.IsNullOrWhiteSpace(node.Secret) ? current.SharedSecret : node.Secret;
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException($"节点 {nodeId} 未配置密钥");
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{node.Endpoint.TrimEnd('/')}/api/v1/distribution/agent/{operation}")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json");
        DistributionAuthentication.Sign(request, operation, secret, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(current.RequestTimeoutSeconds, 1, 120)));
        using var response = await clients.CreateClient("distribution-node")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        const int maxResponseBytes = 1024 * 1024;
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
        var result = JsonSerializer.Deserialize<TResponse>(responseBody, JsonOptions);
        if (result == null) throw new InvalidDataException($"节点 {nodeId} 返回空响应");
        return result;
    }
}
