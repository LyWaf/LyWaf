using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeHandshakeExchange(NodeHandshake Local, NodeRegistrationObservation? Registration,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NodeHubMembershipProof? Membership = null);

public sealed class NodeHandshakeService(NodeHandshakeMetadataService metadata, NodeRegistrationObserver observer,
    IOptionsMonitor<NodeCommunicationOptions> options, NodeMessageAuthentication authentication, NodeSetupStore? setup = null)
{
    public const string Operation = "node.handshake";

    public async Task<NodeHandshakeExchange> HandleAuthenticatedAsync(NodeMessage message, CancellationToken token, string? keyId = null,
        string? authenticatedBootId = null)
    {
        token.ThrowIfCancellationRequested();
        var config = options.CurrentValue;
        var peer = NodeRegistrationObserver.Peer(config, message.SenderNodeId);
        if (!config.Enabled || message.Type != Operation || message.ClusterId != config.ClusterId
            || message.ReceiverNodeId != config.NodeId || message.ProtocolVersion != 2 || message.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("Handshake envelope mismatch");
        var remote = message.Payload.Deserialize<NodeHandshake>(NodeMessageAuthentication.JsonOptions)
            ?? throw new InvalidDataException("Missing handshake");
        NodeHandshakeValidation.Validate(remote, DateTimeOffset.UtcNow);
        if (remote.ClusterId != message.ClusterId || remote.NodeId != message.SenderNodeId || remote.Role != peer.Role)
            throw new InvalidDataException("Handshake identity mismatch");
        if (remote.Ending && (config.Mode != "Controller" || config.Role != "Controller" || remote.BootId != authenticatedBootId))
            throw new UnauthorizedAccessException("Session end requires its authenticated socket boot");
        var local = await metadata.CaptureAsync(token);
        return new(local, config.Role == "Controller" ? observer.ObserveAuthenticated(message) : null,
            config.Mode == "Controller" ? NodeHubMembership.Create(config, message, local, keyId ?? peer.ActiveKeyId,
                DateTimeOffset.UtcNow, remote.ControlCapabilities?.AuthorityBootstrap == 1 ? setup?.ControllerAuthority(config) : null,
                remote.ControlCapabilities?.CcMembership == 1) : null);
    }

    // A standalone authenticated adapter for hosts/tests; the shared router can call HandleAuthenticatedAsync.
    public async Task<IResult> ReceiveAsync(HttpContext context)
    {
        var (message, error) = await authentication.ReadAsync(context, Operation, context.RequestAborted);
        if (error is not null) return NodeProtocolResponse.Error(context, error);
        try { return Results.Json(await HandleAuthenticatedAsync(message!, context.RequestAborted,
            context.Request.Headers["X-LyWaf-KeyId"].ToString())); }
        catch (UnauthorizedAccessException) { return NodeProtocolResponse.Error(context, "node_handshake_forbidden", 403); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        { return NodeProtocolResponse.Error(context, "node_handshake_invalid", 400); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { return NodeProtocolResponse.Error(context, "node_handshake_unavailable", 503); }
    }
}

public static class NodeHandshakeRegistration
{
    public static IServiceCollection AddNodeHandshakeServices(this IServiceCollection services)
    {
        services.TryAddSingleton<INodeHandshakeConfigurationSource, NodeHandshakeConfigurationSource>();
        services.TryAddSingleton<NodeHandshakeMetadataService>();
        services.TryAddSingleton<NodeHubMembership>();
        services.TryAddSingleton<NodeRegistrationObserver>();
        services.TryAddSingleton<NodeIdentityConflictResolutionService>();
        services.TryAddSingleton<NodeHandshakeRecoveryService>();
        services.TryAddSingleton<INodeHandshakePeerProbe, NodeHandshakeClient>();
        services.TryAddSingleton<NodeHandshakeService>();
        return services;
    }
}
