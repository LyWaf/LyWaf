using System.ComponentModel.DataAnnotations;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Auth;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeSetupUpdateRequest(
    [property: Required, StringLength(16)] string Mode,
    [property: StringLength(2048)] string? ControllerUrl = null,
    [property: StringLength(4096)] string? Credential = null,
    bool? SynchronizeRuntimeState = null,
    [property: StringLength(16)] string? AuthenticationMode = null,
    [property: StringLength(256)] string? SharedKey = null,
    [property: StringLength(128)] string? NodeName = null);
public sealed record NodeSetupCreatePeerRequest([property: Required, StringLength(128)] string Name);
public sealed record NodeSetupPeerEnabledRequest(bool Enabled);
public sealed record NodeSetupSharedKeyRequest([property: Required, StringLength(256, MinimumLength = 32)] string Key);

public static class NodeSetupEndpoints
{
    public static void MapNodeSetup(this WebApplication app, int port)
    {
        app.MapPost(NodeSharedKeyEnrollment.Path, async (HttpContext context, NodeSetupStore store,
            IOptionsMonitor<NodeCommunicationOptions> communication) =>
        {
            var config = communication.CurrentValue;
            if (context.Connection.LocalPort != port || !config.Enabled || config.Role != "Controller")
                return NodeProtocolResponse.Error(context, "node_protocol_disabled", 403);
            if (config.RequireHttps && !context.Request.IsHttps)
                return NodeProtocolResponse.Error(context, "node_https_required", 403);
            if (context.Request.QueryString.HasValue || context.Request.ContentLength > 8192)
                return NodeProtocolResponse.Error(context, "node_invalid_message", 400);
            try
            {
                var bytes = new byte[8193];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), context.RequestAborted);
                    if (read == 0) break;
                    count += read;
                }
                if (count > 8192) return NodeProtocolResponse.Error(context, "node_message_too_large", 413);
                var request = System.Text.Json.JsonSerializer.Deserialize<NodeSharedKeyRequest>(bytes.AsSpan(0, count), NodeMessageAuthentication.JsonOptions);
                if (request is null) return NodeProtocolResponse.Error(context, "node_invalid_message", 400);
                var credential = await store.EnrollSharedKeyAsync(request, context.RequestAborted);
                RefreshPeerOptions(context);
                context.Response.Headers.CacheControl = "no-store";
                Audit(context, "node.setup.shared-key.enroll:" + request.NodeId);
                return Results.Json(NodeSharedKeyEnrollment.Reply(NodeSetupStore.DecodeCredential(credential).Secret, request, credential));
            }
            catch (UnauthorizedAccessException) { return NodeProtocolResponse.Error(context, "node_unknown_identity", 401); }
            catch (System.Text.Json.JsonException) { return NodeProtocolResponse.Error(context, "node_invalid_message", 400); }
            catch (InvalidOperationException) { return NodeProtocolResponse.Error(context, "node_setup_conflict", 409); }
            catch (IOException) { return NodeProtocolResponse.Error(context, "node_setup_storage_unavailable", 503); }
        }).RequireHost($"*:{port}")
            .WithMetadata(new LyWaf.Services.ManagementApi.ManagementNonReplayableResponse());

        app.MapGet("/api/node-setup", (NodeSetupStore store, IOptionsMonitor<NodeCommunicationOptions> communication) =>
        {
            var config = communication.CurrentValue;
            return Results.Json(new { success = true, settings = store.Status(), peers = store.Peers(),
                managed = store.Active is not null, effectiveMode = config.Enabled ? config.Role : "Standalone" });
        }).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.Read);

        app.MapPut("/api/node-setup", async (HttpContext context, NodeSetupStore store, NodeSetupUpdateRequest request) =>
        {
            try
            {
                var settings = await store.ConfigureAsync(request.Mode, request.ControllerUrl, request.Credential, context.RequestAborted,
                    request.SynchronizeRuntimeState, request.AuthenticationMode, request.SharedKey, request.NodeName);
                Audit(context, "node.setup.configure:" + request.Mode);
                return Results.Json(new { success = true, settings });
            }
            catch (ArgumentException) { return NodeProtocolResponse.Error(context, "node_setup_invalid", 400); }
            catch (HttpRequestException) { return NodeProtocolResponse.Error(context, "node_setup_join_failed", 502); }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            { return NodeProtocolResponse.Error(context, "node_setup_join_failed", 504); }
            catch (InvalidOperationException) { return NodeProtocolResponse.Error(context, "node_setup_conflict", 409); }
            catch (IOException) { return NodeProtocolResponse.Error(context, "node_setup_storage_unavailable", 503); }
        }).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.ManageSystem);

        app.MapPost("/api/node-setup/peers", async (HttpContext context, NodeSetupStore store, NodeSetupCreatePeerRequest request) =>
        {
            try
            {
                var issued = await store.CreateCredentialAsync(request.Name, context.RequestAborted);
                RefreshPeerOptions(context);
                Audit(context, "node.setup.peer.create:" + issued.Peer.NodeId);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { success = true, peer = issued.Peer, credential = issued.Credential });
            }
            catch (ArgumentException) { return NodeProtocolResponse.Error(context, "node_setup_invalid", 400); }
            catch (InvalidOperationException) { return NodeProtocolResponse.Error(context, "node_setup_conflict", 409); }
            catch (IOException) { return NodeProtocolResponse.Error(context, "node_setup_storage_unavailable", 503); }
        }).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.ManageSystem)
            .WithMetadata(new LyWaf.Services.ManagementApi.ManagementNonReplayableResponse());

        app.MapPut("/api/node-setup/peers/{nodeId}", async (HttpContext context, string nodeId, NodeSetupStore store, NodeSetupPeerEnabledRequest request) =>
        {
            try
            {
                await store.SetPeerEnabledAsync(nodeId, request.Enabled, context.RequestAborted);
                RefreshPeerOptions(context);
                Audit(context, "node.setup.peer.enabled:" + nodeId + ":" + request.Enabled);
                return Results.Json(new { success = true });
            }
            catch (KeyNotFoundException) { return NodeProtocolResponse.Error(context, "node_setup_peer_missing", 404); }
            catch (InvalidOperationException) { return NodeProtocolResponse.Error(context, "node_setup_conflict", 409); }
            catch (IOException) { return NodeProtocolResponse.Error(context, "node_setup_storage_unavailable", 503); }
        }).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.ManageSystem);
        app.MapDelete("/api/node-setup/peers/{nodeId}", async (HttpContext context, string nodeId, NodeSetupStore store) =>
        {
            try
            {
                await store.DeletePeerAsync(nodeId, context.RequestAborted);
                RefreshPeerOptions(context);
                if (context.RequestServices.GetService<NodeSocketConnections>() is { } sockets) await sockets.DisconnectAsync(nodeId);
                Audit(context, "node.setup.peer.delete:" + nodeId);
                return Results.Json(new { success = true });
            }
            catch (KeyNotFoundException) { return NodeProtocolResponse.Error(context, "node_setup_peer_missing", 404); }
            catch (InvalidOperationException) { return NodeProtocolResponse.Error(context, "node_setup_conflict", 409); }
            catch (IOException) { return NodeProtocolResponse.Error(context, "node_setup_storage_unavailable", 503); }
        }).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.ManageSystem);

        app.MapPut("/api/node-setup/shared-key", async (HttpContext context, NodeSetupStore store, NodeSetupSharedKeyRequest request) =>
        {
            try
            {
                var changed = await store.SetSharedCommunicationKeyAsync(request.Key, context.RequestAborted);
                RefreshPeerOptions(context);
                if (changed && context.RequestServices.GetService<NodeSocketConnections>() is { } sockets)
                    foreach (var peer in store.Peers()) await sockets.DisconnectAsync(peer.NodeId);
                Audit(context, "node.setup.shared-key.update");
                return Results.Json(new { success = true, settings = store.Status() });
            }
            catch (ArgumentException) { return NodeProtocolResponse.Error(context, "node_setup_invalid", 400); }
            catch (InvalidOperationException) { return NodeProtocolResponse.Error(context, "node_setup_conflict", 409); }
            catch (IOException) { return NodeProtocolResponse.Error(context, "node_setup_storage_unavailable", 503); }
        }).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.ManageSystem);

        app.MapPost("/api/node-setup/peers/{nodeId}/credential", async (HttpContext context, string nodeId, NodeSetupStore store) =>
        {
            try
            {
                var credential = await store.IssuePeerCredentialAsync(nodeId, context.RequestAborted);
                context.Response.Headers.CacheControl = "no-store";
                Audit(context, "node.setup.peer.credential:" + nodeId);
                return Results.Json(new { success = true, credential });
            }
            catch (KeyNotFoundException) { return NodeProtocolResponse.Error(context, "node_setup_peer_missing", 404); }
            catch (InvalidOperationException) { return NodeProtocolResponse.Error(context, "node_setup_conflict", 409); }
            catch (IOException) { return NodeProtocolResponse.Error(context, "node_setup_storage_unavailable", 503); }
        }).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.ManageSystem)
            .WithMetadata(new LyWaf.Services.ManagementApi.ManagementNonReplayableResponse());
    }

    private static void RefreshPeerOptions(HttpContext context)
    {
        context.RequestServices.GetRequiredService<IOptionsMonitorCache<NodeCommunicationOptions>>().TryRemove(Options.DefaultName);
        context.RequestServices.GetRequiredService<IOptionsMonitorCache<LyWaf.Services.Distribution.DistributionOptions>>().TryRemove(Options.DefaultName);
        context.RequestServices.GetRequiredService<IOptionsMonitorCache<LyWaf.Services.RuntimeState.RuntimeStateOptions>>().TryRemove(Options.DefaultName);
    }

    private static void Audit(HttpContext context, string action) => context.RequestServices.GetRequiredService<IAuditLogService>()
        .Log(context.Items["Username"]?.ToString() ?? "unknown", action, context.Connection.RemoteIpAddress?.ToString() ?? "");
}
