using System.Text.Json;
using LyWaf.Plugins.Core;

namespace LyWaf.Services.NodeCommunication;

public sealed record PluginNodeRequestPayload(string PluginId, string MessageType, int SchemaVersion, JsonElement Data);

/// <summary>Dispatches authenticated requests by plugin declaration, without inspecting business payloads.</summary>
public static class PluginNodeRequests
{
    public const string Operation = "plugin.request";
    public static NodeMessage Wrap(NodeMessage message, string pluginId, int schemaVersion = 1) => message with
    {
        Type = Operation,
        Payload = JsonSerializer.SerializeToElement(new PluginNodeRequestPayload(pluginId, message.Type, schemaVersion, message.Payload),
            NodeMessageAuthentication.JsonOptions)
    };

    public static bool Allows(NodePeerOptions peer, string legacyOperation) => NodeAdmissionPolicy.Allows(peer, legacyOperation)
        && (peer.AllowedOperations.Contains(Operation, StringComparer.Ordinal)
            || peer.AllowedOperations.Contains(legacyOperation, StringComparer.Ordinal));

    internal static void EnsureSender(IPluginManager? manager, string pluginId, string messageType, int schemaVersion = 1)
    {
        if (manager is null) return;
        var plugin = manager.GetPlugin(pluginId);
        if (plugin is null || manager.GetPluginState(pluginId) != PluginState.Running
            || !plugin.Metadata.RequestedPermissions.HasFlag(PluginPermission.NodeMessaging)
            || !manager.IsPermissionGranted(pluginId, PluginPermission.NodeMessaging)
            || plugin.Metadata.NodeRequests.Count(item => item.MessageType == messageType && item.SchemaVersion == schemaVersion) != 1)
            throw new InvalidOperationException("node_plugin_forbidden");
    }

    internal static IEnumerable<string> LegacyOperations(IServiceProvider services) => services.GetServices<IPluginNodeRequestHandler>()
        .SelectMany(handler => handler.Requests).Select(request => request.LegacyOperation).OfType<string>()
        .Where(operation => NodeMessageAuthentication.ValidId(operation) && !NodeProtocolOperations.IsSupported(operation)).Distinct(StringComparer.Ordinal);

    internal static async Task<IResult?> DispatchAsync(NodeMessage message, HttpContext context)
    {
        var wrapped = message.Type == Operation;
        PluginNodeRequestPayload? payload = null;
        if (wrapped)
        {
            try { payload = message.Payload.Deserialize<PluginNodeRequestPayload>(NodeMessageAuthentication.JsonOptions); }
            catch (JsonException) { return NodeProtocolResponse.Error(context, "node_plugin_invalid_payload", 400); }
            if (payload is null || !NodeMessageAuthentication.ValidId(payload.PluginId)
                || !NodeMessageAuthentication.ValidId(payload.MessageType) || payload.SchemaVersion is < 1 or > 65535
                || payload.Data.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(payload.Data).Length > 256 * 1024)
                return NodeProtocolResponse.Error(context, "node_plugin_invalid_payload", 400);
        }
        if (!wrapped && NodeProtocolOperations.IsSupported(message.Type)) return null;
        var candidates = context.RequestServices.GetServices<IPluginNodeRequestHandler>()
            .SelectMany(handler => handler.Requests.Select(request => (Handler: handler, Request: request)))
            .Where(item => wrapped ? item.Handler.PluginId == payload!.PluginId && item.Request.MessageType == payload.MessageType
                && item.Request.SchemaVersion == payload.SchemaVersion : item.Request.LegacyOperation == message.Type).ToArray();
        if (candidates.Length != 1)
            return wrapped || candidates.Length > 1 ? NodeProtocolResponse.Error(context, "node_plugin_schema_unsupported", 422) : null;
        var (handler, declaration) = candidates[0];
        var manager = context.RequestServices.GetService<IPluginManager>();
        // Isolated protocol hosts may register handlers without the lifecycle host. Production always has a manager.
        if (manager is not null)
        {
            var plugin = manager.GetPlugin(handler.PluginId);
            if (plugin is null || manager.GetPluginState(handler.PluginId) != PluginState.Running)
                return NodeProtocolResponse.Error(context, "node_plugin_missing", 404);
            if (!plugin.Metadata.RequestedPermissions.HasFlag(PluginPermission.NodeMessaging)
                || !manager.IsPermissionGranted(handler.PluginId, PluginPermission.NodeMessaging)
                || plugin.Metadata.NodeRequests.Count(item => item == declaration) != 1)
                return NodeProtocolResponse.Error(context, "node_plugin_forbidden", 403);
        }
        var inner = wrapped ? message with { Type = declaration.MessageType, Payload = payload!.Data.Clone() } : message;
        return await handler.HandleNodeRequestAsync(new(inner.ClusterId, inner.SenderNodeId, inner.ReceiverNodeId,
            inner.MessageId, inner.CorrelationId, declaration.MessageType, declaration.SchemaVersion,
            inner.CreatedAt, inner.ExpiresAt, inner.Payload.Clone()), context);
    }
}
