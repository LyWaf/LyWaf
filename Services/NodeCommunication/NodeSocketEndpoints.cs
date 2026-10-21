using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public static class NodeSocketEndpoints
{
    public const string Path = NodeMessageAuthentication.Prefix + "connect";
    public const string HandshakeHeader = "X-LyWaf-Handshake";

    public static void MapNodeSocket(this WebApplication app, int controlPort)
    {
        app.MapGet(Path, async (HttpContext context, IOptionsMonitor<NodeCommunicationOptions> options,
            NodeMessageAuthentication authentication, NodeHandshakeService handshakes,
            NodeSocketConnections connections, NodeSocketProtocol protocol) =>
        {
            var config = options.CurrentValue;
            if (context.Connection.LocalPort != controlPort || !config.Enabled || config.Role != "Controller")
            { await NodeProtocolResponse.Error(context, "node_protocol_disabled", 403).ExecuteAsync(context); return; }
            if (!context.WebSockets.IsWebSocketRequest || context.Request.QueryString.HasValue)
            { await NodeProtocolResponse.Error(context, "node_invalid_path", 400).ExecuteAsync(context); return; }
            var encoded = context.Request.Headers[HandshakeHeader];
            if (encoded.Count != 1 || encoded[0] is not { Length: > 0 and <= 24 * 1024 })
            { await NodeProtocolResponse.Error(context, "node_invalid_headers", 401).ExecuteAsync(context); return; }
            byte[] body;
            try { body = Convert.FromBase64String(encoded[0]!); }
            catch (FormatException)
            { await NodeProtocolResponse.Error(context, "node_invalid_headers", 401).ExecuteAsync(context); return; }
            // Authenticate the upgrade with the same canonical POST handshake signature and durable nonce guard.
            var upgrade = new DefaultHttpContext { RequestServices = context.RequestServices, RequestAborted = context.RequestAborted };
            upgrade.Request.Method = "POST";
            upgrade.Request.Scheme = context.Request.Scheme;
            upgrade.Request.Path = NodeMessageAuthentication.Prefix + NodeHandshakeService.Operation;
            upgrade.Request.ContentLength = body.Length;
            using var input = new MemoryStream(body, writable: false);
            upgrade.Request.Body = input;
            foreach (var name in NodeSocketProtocol.SignedHeaders) upgrade.Request.Headers[name] = context.Request.Headers[name];
            var (message, error) = await authentication.ReadAsync(upgrade, NodeHandshakeService.Operation, context.RequestAborted);
            if (error is not null)
            { await NodeProtocolResponse.Error(context, error).ExecuteAsync(context); return; }
            if (!config.Peers.TryGetValue(message!.SenderNodeId, out var peer) || peer.Role != "Node")
            { await NodeProtocolResponse.Error(context, "node_handshake_forbidden", 403).ExecuteAsync(context); return; }
            string boot;
            try
            {
                var remote = message.Payload.Deserialize<NodeHandshake>(NodeMessageAuthentication.JsonOptions);
                if (remote is null || remote.Ending) throw new InvalidDataException("Session end cannot open a new socket");
                boot = remote.BootId;
                var exchange = await handshakes.HandleAuthenticatedAsync(message!, context.RequestAborted,
                    context.Request.Headers["X-LyWaf-KeyId"].ToString());
                if (exchange.Registration?.State == "IdentityConflict")
                { await NodeProtocolResponse.Error(context, "node_identity_mismatch", 409).ExecuteAsync(context); return; }
            }
            catch (UnauthorizedAccessException)
            { await NodeProtocolResponse.Error(context, "node_handshake_forbidden", 403).ExecuteAsync(context); return; }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
            { await NodeProtocolResponse.Error(context, "node_handshake_invalid", 400).ExecuteAsync(context); return; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OptionsValidationException)
            { await NodeProtocolResponse.Error(context, "node_handshake_unavailable", 503).ExecuteAsync(context); return; }
            using var lease = connections.Reserve(message!.SenderNodeId, config.ClusterId, config.NodeId, context.Request.IsHttps);
            if (lease is null)
            { await NodeProtocolResponse.Error(context, "node_outcome_unknown", 409).ExecuteAsync(context); return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var frameLimit = checked((int)Math.Clamp(config.MaxMessageBytes * 2 + 64 * 1024, 128 * 1024, 40 * 1024 * 1024));
            await using var channel = new NodeSocketChannel(socket, frameLimit);
            lease.Attach(channel);
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            try
            {
                await channel.RunAsync((bytes, token) => protocol.DispatchAsync(bytes, message.SenderNodeId,
                    context.Request.IsHttps, token, boot), shutdown.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.WebSockets.WebSocketException
                or JsonException or InvalidDataException) { /* Disconnect leaves durable command outcomes for reconciliation. */ }
            finally
            {
                try { context.RequestServices.GetRequiredService<NodeRegistrationObserver>().MarkDisconnected(message.SenderNodeId); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or OptionsValidationException)
                {
                    app.Logger.LogWarning("Node socket closed; durable disconnect marker is unavailable ({FailureType})", ex.GetType().Name);
                }
            }
        }).RequireHost($"*:{controlPort}");
    }
}
