using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Services.Distribution;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed class PluginNodeRequestClient(IPluginManager manager, IOptionsMonitor<NodeCommunicationOptions> communication,
    IOptionsMonitor<DistributionOptions> distribution, IHttpClientFactory clients, NodeSocketConnections? sockets = null)
{
    internal IPluginNodeRequestClient Bind(string pluginId) => new Bound(this, pluginId);
    private sealed class Bound(PluginNodeRequestClient host, string pluginId) : IPluginNodeRequestClient
    {
        public Task<PluginNodeResponse> RequestAsync(string targetNodeId, string messageType, int schemaVersion,
            JsonElement payload, string messageId, CancellationToken cancellationToken) =>
            host.SendAsync(pluginId, targetNodeId, messageType, schemaVersion, payload, messageId, cancellationToken);
    }

    private async Task<PluginNodeResponse> SendAsync(string pluginId, string target, string type, int schema,
        JsonElement data, string id, CancellationToken token)
    {
        PluginNodeRequests.EnsureSender(manager, pluginId, type, schema);
        if (!Guid.TryParseExact(id, "D", out var guid) || guid.ToString("D") != id || schema is < 1 or > 65535
            || !NodeMessageAuthentication.ValidId(target) || data.ValueKind != JsonValueKind.Object
            || JsonSerializer.SerializeToUtf8Bytes(data).Length > 256 * 1024)
            throw new ArgumentException("Invalid plugin node request");
        var config = communication.CurrentValue;
        if (!config.Enabled || target == config.NodeId || config.Mode == "Node" && target != config.ControllerNodeId
            || !config.Peers.TryGetValue(target, out var peer) || !NodeAdmissionPolicy.Allows(peer, PluginNodeRequests.Operation)
            || !peer.AllowedOperations.Contains(PluginNodeRequests.Operation, StringComparer.Ordinal)
            || !peer.Keys.TryGetValue(peer.ActiveKeyId, out var secret) || string.IsNullOrEmpty(secret)
            || !distribution.CurrentValue.Nodes.TryGetValue(target, out var destination) || !destination.Enabled || destination.ProtocolVersion != 2
            || !Uri.TryCreate(destination.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || config.RequireHttps && uri.Scheme != "https" || uri.AbsolutePath != "/" || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("node_plugin_forbidden");
        var now = DateTimeOffset.UtcNow;
        var message = PluginNodeRequests.Wrap(new(2, config.ClusterId, config.NodeId, target, id, pluginId, type,
            now, now.AddSeconds(10), data.Clone()), pluginId, schema);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, NodeMessageAuthentication.Prefix + PluginNodeRequests.Operation))
        { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json");
        NodeMessageAuthentication.Sign(request, message, peer.ActiveKeyId, secret, bytes);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await NodeControlTransport.SendAsync(config, sockets, target,
            clients.CreateClient("node-plugin-messages"), request, deadline.Token);
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var body = new byte[256 * 1024 + 1];
        var count = 0;
        while (count < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(count), deadline.Token);
            if (read == 0) break;
            count += read;
        }
        if (count >= body.Length) throw new InvalidDataException("Plugin node response exceeds limit");
        var current = communication.CurrentValue;
        if (!current.Enabled || current.ClusterId != config.ClusterId || current.NodeId != config.NodeId
            || !current.Peers.TryGetValue(target, out var active) || !NodeAdmissionPolicy.Allows(active, PluginNodeRequests.Operation)
            || !active.AllowedOperations.Contains(PluginNodeRequests.Operation, StringComparer.Ordinal)
            || !active.Keys.TryGetValue(peer.ActiveKeyId, out var key) || key != secret
            || !distribution.CurrentValue.Nodes.TryGetValue(target, out var latest) || !latest.Enabled || latest.Endpoint != destination.Endpoint)
            throw new InvalidOperationException("node_plugin_forbidden");
        PluginNodeRequests.EnsureSender(manager, pluginId, type, schema);
        using var document = JsonDocument.Parse(body.AsMemory(0, count));
        return new(id, (int)response.StatusCode, document.RootElement.Clone());
    }
}
