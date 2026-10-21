using System.Text.Json;
using LyWaf.Services.Auth;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeIdentityConflictRequest(string RequestId, string ObservationId, string AcceptedBootId,
    string RetiredBootId, string Reason);

/// <summary>Management-only boundary. Agent credentials and write-only users cannot retire node identities.</summary>
public sealed class NodeIdentityConflictResolutionService(NodeRegistrationObserver observer)
{
    public NodeRegistrationObservation Confirm(HttpContext context, string peer, NodeIdentityConflictRequest request)
    {
        var actor = Actor(context);
        context.RequestAborted.ThrowIfCancellationRequested();
        if (!NodeMessageAuthentication.ValidId(peer) || !Guid.TryParseExact(request.RequestId, "D", out _)
            || !Guid.TryParseExact(request.ObservationId, "N", out _) || !Guid.TryParseExact(request.AcceptedBootId, "N", out _)
            || !Guid.TryParseExact(request.RetiredBootId, "N", out _) || request.AcceptedBootId == request.RetiredBootId
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 512 || request.Reason.Any(char.IsControl))
            throw new ArgumentException("Invalid identity conflict resolution");
        return observer.ConfirmConflict(peer, request, actor, context.Connection.RemoteIpAddress?.ToString() ?? "");
    }

    public IResult Read(HttpContext context, string peer)
    {
        IResult Error(int status, string code) => NodeProtocolResponse.Error(context, code, status);
        try
        {
            Actor(context);
            if (!NodeMessageAuthentication.ValidId(peer)) return Error(400, "node_identity_resolution_invalid");
            var registration = observer.Get(peer);
            return registration is null ? Error(404, "node_identity_observation_missing") : Results.Json(new
            {
                success = true, registration,
                tasksAllowed = observer.CanStartTasks(peer, registration.ObservationId)
            }, statusCode: StatusCodes.Status200OK);
        }
        catch (UnauthorizedAccessException) { return Error(403, "node_identity_admin_required"); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        { return Error(503, "node_identity_resolution_unavailable"); }
    }

    public async Task<IResult> HandleAsync(HttpContext context, string peer)
    {
        IResult Error(int status, string code) => NodeProtocolResponse.Error(context, code, status);
        try
        {
            Actor(context);
            if (!context.Request.HasJsonContentType()) return Error(415, "node_identity_content_type_required");
            if (context.Request.ContentLength > 4096) return Error(413, "node_message_too_large");
            using var bounded = new MemoryStream();
            var buffer = new byte[1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (bounded.Length + read > 4096) return Error(413, "node_message_too_large");
                bounded.Write(buffer, 0, read);
            }
            NodeIdentityConflictRequest request;
            try { request = JsonSerializer.Deserialize<NodeIdentityConflictRequest>(bounded.ToArray(), NodeMessageAuthentication.JsonOptions)
                ?? throw new ArgumentException("Missing identity resolution request"); }
            catch (JsonException) { return Error(400, "node_identity_resolution_invalid"); }
            context.RequestAborted.ThrowIfCancellationRequested();
            return Results.Json(new { success = true, registration = Confirm(context, peer, request), tasksAllowed = false }, statusCode: StatusCodes.Status200OK);
        }
        catch (UnauthorizedAccessException) { return Error(403, "node_identity_admin_required"); }
        catch (ArgumentException) { return Error(400, "node_identity_resolution_invalid"); }
        catch (InvalidOperationException) { return Error(409, "node_identity_resolution_conflict"); }
        catch (Exception ex) when (ex is IOException or JsonException)
        { return Error(503, "node_identity_resolution_unavailable"); }
    }

    private static string Actor(HttpContext context)
        => context.Items["ManagementScopes"] is IEnumerable<string> scopes && scopes.Contains("admin", StringComparer.Ordinal)
            && context.Items["Username"] is string actor && !string.IsNullOrWhiteSpace(actor) && actor.Length <= 128 && !actor.Any(char.IsControl)
            ? actor : throw new UnauthorizedAccessException("Administrator identity confirmation required");

}

public static class NodeIdentityConflictManagementEndpoints
{
    public static void MapNodeIdentityConflictManagement(this WebApplication app, int controlPort)
    {
        const string path = "/api/distribution/nodes/{nodeId}/identity";
        app.MapGet(path, (HttpContext context, string nodeId, NodeIdentityConflictResolutionService service) => service.Read(context, nodeId))
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem);
        app.MapPost(path + "/confirm", (HttpContext context, string nodeId, NodeIdentityConflictResolutionService service) => service.HandleAsync(context, nodeId))
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem);
    }
}
