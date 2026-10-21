using System.ComponentModel.DataAnnotations;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Auth;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeSetupUpdateRequest(
    [property: Required, StringLength(16)] string Mode,
    [property: StringLength(2048)] string? ControllerUrl = null,
    [property: StringLength(4096)] string? Credential = null,
    bool? SynchronizeRuntimeState = null);
public sealed record NodeSetupCreatePeerRequest([property: Required, StringLength(128)] string Name);
public sealed record NodeSetupPeerEnabledRequest(bool Enabled);

public static class NodeSetupEndpoints
{
    public static void MapNodeSetup(this WebApplication app, int port)
    {
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
                    request.SynchronizeRuntimeState);
                Audit(context, "node.setup.configure:" + request.Mode);
                return Results.Json(new { success = true, settings });
            }
            catch (ArgumentException) { return NodeProtocolResponse.Error(context, "node_setup_invalid", 400); }
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
