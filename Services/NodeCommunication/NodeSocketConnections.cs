using System.Collections.Concurrent;

namespace LyWaf.Services.NodeCommunication;

/// <summary>Live routes only. Durable delivery and outcome reconciliation remain owned by the existing outboxes.</summary>
public sealed class NodeSocketConnections
{
    private readonly ConcurrentDictionary<string, Lease> connections = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public bool IsConnected(string peer) => connections.TryGetValue(peer, out var lease) && lease.Channel is not null;

    internal Lease? Reserve(string peer, string? cluster = null, string? localNode = null, bool? secureTransport = null)
    {
        lock (gate)
        {
            if (connections.Count >= 1024 || connections.ContainsKey(peer)) return null;
            var lease = new Lease(this, peer, cluster, localNode, secureTransport);
            return connections.TryAdd(peer, lease) ? lease : null;
        }
    }

    public async Task<HttpResponseMessage> SendAsync(string peer, HttpRequestMessage request, CancellationToken token,
        bool requireIdentityBinding = false, bool requireSecureTransport = false)
    {
        if (!connections.TryGetValue(peer, out var lease) || lease.Channel is not { } channel)
            throw new IOException("Node socket is not connected");
        if (!Header("X-LyWaf-Receiver", peer) || requireIdentityBinding && (lease.Cluster is null || lease.LocalNode is null)
            || lease.Cluster is not null && !Header("X-LyWaf-Cluster", lease.Cluster)
            || lease.LocalNode is not null && !Header("X-LyWaf-Sender", lease.LocalNode)
            || requireSecureTransport && lease.SecureTransport != true)
            throw new IOException("Node socket identity or transport changed; reconnect required");
        return NodeSocketProtocol.Decode(await channel.RequestAsync(await NodeSocketProtocol.EncodeAsync(request, token), token));

        bool Header(string name, string expected) => request.Headers.TryGetValues(name, out var values)
            && values.SequenceEqual(new[] { expected }, StringComparer.Ordinal);
    }

    internal sealed class Lease(NodeSocketConnections owner, string peer, string? cluster, string? localNode, bool? secureTransport) : IDisposable
    {
        public string? Cluster => cluster;
        public string? LocalNode => localNode;
        public bool? SecureTransport => secureTransport;
        private NodeSocketChannel? channel;
        public NodeSocketChannel? Channel => Volatile.Read(ref channel);
        public void Attach(NodeSocketChannel value) => Volatile.Write(ref channel, value);
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (owner.connections.TryGetValue(peer, out var current) && ReferenceEquals(current, this))
                    owner.connections.TryRemove(peer, out _);
                Volatile.Write(ref channel, null);
            }
        }
    }
}
