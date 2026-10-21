using System.Text.Json;
using LyWaf.Services.Distribution;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public interface INodeHandshakePeerProbe
{
    Task<NodeHandshake> CaptureAsync(string peer, CancellationToken token);
}

/// <summary>Uses only configured endpoints and v2 credentials; a reply never grants admission.</summary>
public sealed class NodeHandshakeClient : INodeHandshakePeerProbe, IDisposable
{
    private readonly IOptionsMonitor<NodeCommunicationOptions> _communication;
    private readonly IOptionsMonitor<DistributionOptions> _distribution;
    private readonly NodeHandshakeMetadataService _metadata;
    private readonly HttpClient _http;
    private readonly NodeSocketConnections? _sockets;

    public NodeHandshakeClient(IOptionsMonitor<NodeCommunicationOptions> communication,
        IOptionsMonitor<DistributionOptions> distribution, NodeHandshakeMetadataService metadata, NodeSocketConnections? sockets = null)
        : this(communication, distribution, metadata, new SocketsHttpHandler { AllowAutoRedirect = false }) { _sockets = sockets; }

    internal NodeHandshakeClient(IOptionsMonitor<NodeCommunicationOptions> communication,
        IOptionsMonitor<DistributionOptions> distribution, NodeHandshakeMetadataService metadata, HttpMessageHandler handler)
    {
        _communication = communication; _distribution = distribution; _metadata = metadata;
        _http = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<NodeHandshake> CaptureAsync(string peer, CancellationToken token)
    {
        var config = _communication.CurrentValue;
        var admission = NodeRegistrationObserver.Peer(config, peer);
        if (!config.Enabled || !_distribution.CurrentValue.Nodes.TryGetValue(peer, out var destination)
            || !destination.Enabled || destination.ProtocolVersion != 2
            || !admission.Keys.TryGetValue(admission.ActiveKeyId, out var secret) || System.Text.Encoding.UTF8.GetByteCount(secret) < 32
            || !Uri.TryCreate(destination.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")
            || config.RequireHttps && endpoint.Scheme != "https" || endpoint.AbsolutePath != "/"
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
            throw new InvalidOperationException("Handshake destination is not authorized or configured");
        var local = await _metadata.CaptureAsync(token);
        var now = DateTimeOffset.UtcNow;
        var request = new NodeMessage(2, config.ClusterId, config.NodeId, peer, Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("N"), NodeHandshakeService.Operation, now, now.AddMinutes(1),
            JsonSerializer.SerializeToElement(local, NodeMessageAuthentication.JsonOptions));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, NodeMessageAuthentication.JsonOptions);
        using var http = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, NodeMessageAuthentication.Prefix + request.Type))
            { Content = new ByteArrayContent(bytes) };
        http.Content.Headers.ContentType = new("application/json");
        NodeMessageAuthentication.Sign(http, request, admission.ActiveKeyId, secret, bytes);
        using var response = await NodeControlTransport.SendAsync(config, _sockets, peer, _http, http, token);
        if (!response.IsSuccessStatusCode) throw await NodeRemoteError.ReadAsync(response, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bounded = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bounded.Length + count > 512 * 1024) throw new InvalidDataException("Oversized handshake response");
            bounded.Write(buffer, 0, count);
        }
        var remote = JsonSerializer.Deserialize<NodeHandshakeExchange>(bounded.ToArray(), NodeMessageAuthentication.JsonOptions)?.Local
            ?? throw new InvalidDataException("Missing handshake response");
        NodeHandshakeValidation.Validate(remote, DateTimeOffset.UtcNow);
        if (remote.ClusterId != config.ClusterId || remote.NodeId != peer || remote.Role != admission.Role)
            throw new InvalidDataException("Handshake response identity mismatch");
        return remote;
    }

    public void Dispose() => _http.Dispose();
}
