using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

/// <summary>The node owns its outbound control connection; proxy requests never depend on this loop.</summary>
public sealed class NodeSocketConnector(IOptionsMonitor<NodeCommunicationOptions> options,
    NodeHandshakeMetadataService metadata, NodeSocketConnections connections, NodeSocketProtocol protocol,
    ILogger<NodeSocketConnector> logger, NodeHubMembership? membership = null, NodeSetupStore? setup = null) : BackgroundService
{
    private int stopping;
    private int canEndSession;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Interlocked.Exchange(ref stopping, 1) == 0)
            {
                var config = options.CurrentValue;
                if (Volatile.Read(ref canEndSession) == 1 && config is { Enabled: true, Mode: "Node", Role: "Node" } && connections.IsConnected(config.ControllerNodeId)
                    && Uri.TryCreate(config.ControllerUrl, UriKind.Absolute, out var endpoint)
                    && config.Peers.TryGetValue(config.ControllerNodeId, out var peer)
                    && peer.Keys.TryGetValue(peer.ActiveKeyId, out var secret))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    using var request = await CreateHandshakeAsync(config, peer.ActiveKeyId, secret, endpoint, deadline.Token, ending: true);
                    using var response = await connections.SendAsync(config.ControllerNodeId, request, deadline.Token,
                        requireIdentityBinding: true, requireSecureTransport: config.RequireHttps);
                    response.EnsureSuccessStatusCode();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or ArgumentException
            or OperationCanceledException or OptionsValidationException)
        {
            logger.LogWarning("Node session end could not be confirmed ({FailureType}); identity conflict protection remains active", ex.GetType().Name);
        }
        finally { await base.StopAsync(cancellationToken); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = options.CurrentValue;
                if (!config.Enabled || config.Role != "Node" || string.IsNullOrWhiteSpace(config.ControllerUrl))
                { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
                await ConnectOnceAsync(stoppingToken);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or WebSocketException or HttpRequestException or JsonException
                or InvalidOperationException or ArgumentException or UnauthorizedAccessException or OperationCanceledException or OptionsValidationException)
            {
                failures = Math.Min(failures + 1, 6);
                logger.LogWarning("Node control connection unavailable ({FailureType}); reconnecting", ex.GetType().Name);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(30000, 500 * Math.Pow(2, failures))
                    + Random.Shared.Next(500)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task ConnectOnceAsync(CancellationToken token)
    {
        var config = options.CurrentValue;
        var connectionSettings = ConnectionSettings(config);
        if (!config.Enabled || config.Role != "Node"
            || !Uri.TryCreate(config.ControllerUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.AbsolutePath != "/"
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0
            || config.RequireHttps && endpoint.Scheme != "https"
            || !config.Peers.TryGetValue(config.ControllerNodeId, out var peer) || peer.Role != "Controller"
            || !NodeAdmissionPolicy.Allows(peer, NodeHandshakeService.Operation)
            || !peer.Keys.TryGetValue(peer.ActiveKeyId, out var secret) || System.Text.Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Invalid controller connection configuration");
        using var lease = connections.Reserve(config.ControllerNodeId, config.ClusterId, config.NodeId, endpoint.Scheme == "https")
            ?? throw new IOException("Controller socket is already connected");
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        using var handshake = await CreateHandshakeAsync(config, peer.ActiveKeyId, secret, endpoint, token);
        var body = await handshake.Content!.ReadAsByteArrayAsync(token);
        var encoded = Convert.ToBase64String(body);
        if (encoded.Length > 24 * 1024) throw new InvalidDataException("Node upgrade metadata exceeds limit");
        socket.Options.SetRequestHeader(NodeSocketEndpoints.HandshakeHeader, encoded);
        foreach (var name in NodeSocketProtocol.SignedHeaders)
            socket.Options.SetRequestHeader(name, handshake.Headers.GetValues(name).Single());
        var address = new UriBuilder(new Uri(endpoint, NodeSocketEndpoints.Path))
            { Scheme = endpoint.Scheme == "https" ? "wss" : "ws" }.Uri;
        using var connected = CancellationTokenSource.CreateLinkedTokenSource(token);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(address, deadline.Token);
        }
        var frameLimit = checked((int)Math.Clamp(config.MaxMessageBytes * 2 + 64 * 1024, 128 * 1024, 40 * 1024 * 1024));
        await using var channel = new NodeSocketChannel(socket, frameLimit);
        lease.Attach(channel);
        var receiving = channel.RunAsync((bytes, ct) => protocol.DispatchAsync(bytes, config.ControllerNodeId,
            endpoint.Scheme == "https", ct), connected.Token);
        try
        {
            while (!receiving.IsCompleted && Volatile.Read(ref stopping) == 0)
            {
                // A fresh signed handshake refreshes admission metadata and also proves the server's configured identity.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connected.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                using var request = await CreateHandshakeAsync(config, peer.ActiveKeyId, secret, endpoint, deadline.Token);
                using var response = await connections.SendAsync(config.ControllerNodeId, request, deadline.Token);
                response.EnsureSuccessStatusCode();
                var exchange = JsonSerializer.Deserialize<NodeHandshakeExchange>(await response.Content.ReadAsByteArrayAsync(deadline.Token),
                    NodeMessageAuthentication.JsonOptions) ?? throw new InvalidDataException("Missing controller handshake");
                NodeHandshakeValidation.Validate(exchange.Local, DateTimeOffset.UtcNow);
                if (exchange.Local.NodeId != config.ControllerNodeId || exchange.Local.ClusterId != config.ClusterId
                    || exchange.Local.Role != "Controller" || exchange.Registration?.State == "IdentityConflict")
                    throw new InvalidDataException("Controller identity mismatch");
                if (config.Mode == "Node")
                {
                    var original = JsonSerializer.Deserialize<NodeMessage>(await request.Content!.ReadAsByteArrayAsync(deadline.Token),
                        NodeMessageAuthentication.JsonOptions)!;
                    if (setup is not null) await setup.StageAuthorityUpgradeAsync(config, original, exchange.Local, exchange.Membership, deadline.Token);
                    else NodeHubMembership.Validate(config, original, exchange.Local, exchange.Membership, DateTimeOffset.UtcNow);
                    membership?.Accept(config, original, exchange.Local, exchange.Membership);
                }
                Volatile.Write(ref canEndSession, exchange.Local.ControlCapabilities?.SessionEnd == 1 ? 1 : 0);
                var delay = Task.Delay(TimeSpan.FromSeconds(15), connected.Token);
                await Task.WhenAny(receiving, delay);
                if (ConnectionSettings(options.CurrentValue) != connectionSettings) return;
            }
            await receiving;
        }
        finally
        {
            Volatile.Write(ref canEndSession, 0);
            if (config.Mode == "Node") membership?.Invalidate();
            await connected.CancelAsync();
            socket.Abort();
            try { await receiving; }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
        }
    }

    // Reload creates fresh options even when only proxy configuration changed. Keep the socket in that case.
    internal static string ConnectionSettings(NodeCommunicationOptions config) => JsonSerializer.Serialize(config);

    private async Task<HttpRequestMessage> CreateHandshakeAsync(NodeCommunicationOptions config, string keyId, string secret,
        Uri endpoint, CancellationToken token, bool ending = false)
    {
        var local = await metadata.CaptureAsync(token);
        if (ending) local = local with { Ending = true };
        var now = DateTimeOffset.UtcNow;
        var message = new NodeMessage(2, config.ClusterId, config.NodeId, config.ControllerNodeId, Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("N"), NodeHandshakeService.Operation, now, now.AddMinutes(1),
            JsonSerializer.SerializeToElement(local, NodeMessageAuthentication.JsonOptions));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, NodeMessageAuthentication.Prefix + message.Type))
            { Content = new ByteArrayContent(bytes) };
        NodeMessageAuthentication.Sign(request, message, keyId, secret, bytes);
        return request;
    }
}
