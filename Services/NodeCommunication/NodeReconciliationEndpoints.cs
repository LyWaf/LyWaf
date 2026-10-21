using LyWaf.Services.Auth;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Services.NodeCommunication;

public static class NodeReconciliationEndpoints
{
    public static void MapNodeReconciliation(this WebApplication app, int controlPort)
    {
        const string prefix = "/api/distribution/reconciliation/{nodeId}";
        app.MapGet(prefix, (HttpContext context, NodeReconciliationService service, string nodeId, int? offset, int? limit) =>
            Handle(context, () => Task.FromResult<IResult>(Results.Json(service.List(Actor(context), nodeId, offset ?? 0, limit ?? 50)))))
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem)
            .Produces<IReadOnlyList<NodeReconciliationView>>();
        app.MapGet(prefix + "/{messageId}", (HttpContext context, NodeReconciliationService service, string nodeId, string messageId) =>
            Handle(context, () => Task.FromResult<IResult>(Results.Json(service.Get(Actor(context), nodeId, messageId)))))
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem)
            .Produces<NodeReconciliationView>();
        app.MapPost(prefix + "/{messageId}/query", (HttpContext context, NodeReconciliationService service,
            string nodeId, string messageId, NodeReconciliationRequest request) => Handle(context, async () => Results.Json(
                await service.QueryAsync(Actor(context), context.Connection.RemoteIpAddress?.ToString() ?? "unknown", nodeId,
                    messageId, request, context.RequestAborted))))
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem)
            .Produces<NodeReconciliationAction>();
        app.MapPost(prefix + "/{messageId}/assessment", (HttpContext context, NodeReconciliationService service,
            string nodeId, string messageId, NodeReconciliationRequest request) => Handle(context, async () => Results.Json(
                await service.AssessAsync(Actor(context), context.Connection.RemoteIpAddress?.ToString() ?? "unknown", nodeId,
                    messageId, request, context.RequestAborted))))
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem)
            .Produces<NodeReconciliationAction>();
    }

    private static string Actor(HttpContext context) => context.Items["Username"] as string ?? "";

    private static async Task<IResult> Handle(HttpContext context, Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (NodeReconciliationAccessDeniedException) { return ManagementApiContract.Error(context, 403, "distribution_forbidden", "没有目标节点的核对权限"); }
        catch (KeyNotFoundException) { return ManagementApiContract.Error(context, 404, "not_found", "核对记录不存在"); }
        catch (ArgumentException) { return ManagementApiContract.Error(context, 400, "invalid_request", "核对请求无效"); }
        catch (InvalidOperationException) { return ManagementApiContract.Error(context, 409, "conflict", "核对状态、请求标识或查询预算冲突"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return ManagementApiContract.Error(context, 503, "service_unavailable", "持久核对记录暂不可用，未确认结果不能重放"); }
    }
}
