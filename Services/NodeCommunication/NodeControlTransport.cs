namespace LyWaf.Services.NodeCommunication;

/// <summary>One routing policy for every v2 client. Managed nodes never dial peers or downgrade on disconnect.</summary>
public static class NodeControlTransport
{
    public static async Task<HttpResponseMessage> SendAsync(NodeCommunicationOptions? configuration,
        NodeSocketConnections? sockets, string peer, HttpClient http, HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var managed = configuration?.Mode is "Controller" or "Node";
        if (configuration?.Mode == "Standalone") throw new InvalidOperationException("Node communication is disabled in standalone mode");
        if (managed)
        {
            if (!configuration!.Enabled || configuration.Role != configuration.Mode
                || configuration.Mode == "Node" && peer != configuration.ControllerNodeId
                || !configuration.Peers.TryGetValue(peer, out var identity) || !identity.Enabled
                || !Header("X-LyWaf-Cluster", configuration.ClusterId)
                || !Header("X-LyWaf-Sender", configuration.NodeId) || !Header("X-LyWaf-Receiver", peer)
                || request.RequestUri is not { IsAbsoluteUri: true } uri || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || request.Method != HttpMethod.Post || !uri.AbsolutePath.StartsWith(NodeMessageAuthentication.Prefix, StringComparison.Ordinal)
                || (!NodeProtocolOperations.IsSupported(uri.AbsolutePath[NodeMessageAuthentication.Prefix.Length..])
                    && !identity.AllowedOperations.Contains(uri.AbsolutePath[NodeMessageAuthentication.Prefix.Length..], StringComparer.Ordinal))
                || !NodeAdmissionPolicy.Allows(identity, uri.AbsolutePath[NodeMessageAuthentication.Prefix.Length..]))
                throw new InvalidOperationException("Node control route is not authorized");
        }
        if (managed || sockets?.IsConnected(peer) == true)
        {
            if (sockets is null) throw new HttpRequestException("Node control socket is unavailable");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(30) : http.Timeout);
            try { return await sockets.SendAsync(peer, request, deadline.Token, managed, configuration?.RequireHttps == true); }
            catch (IOException ex) { throw new HttpRequestException("Node control connection failed; delivery outcome may be unknown", ex); }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            { throw new TaskCanceledException("Node control request timed out", ex, deadline.Token); }
        }
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        bool Header(string name, string expected) => request.Headers.TryGetValues(name, out var values)
            && values.SequenceEqual(new[] { expected }, StringComparer.Ordinal);
    }
}
