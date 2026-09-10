using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementTasks;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapManagementTaskEndpoints(WebApplication app, int controlPort)
    {
        app.MapGet("/api/system/health", (LyWaf.Services.ManagementTasks.ManagementHealthService health) => TypedResults.Ok(new { success = true, data = health.Capture() }))
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem);
        var group = app.MapGroup("/api/tasks").RequireHost($"*:{controlPort}")
            .RequireManagementPermission(ManagementPermissions.ManageSystem);
        MapCertificateTaskEndpoints(group);
        MapUpgradeTaskEndpoints(group);
        group.MapGet("", (HttpContext context, ManagementJobService jobs, string? state, string? kind) =>
        {
            var all = jobs.List().Where(j => (state == null || j.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase)) &&
                (kind == null || j.Kind == kind)).ToArray();
            return Results.Ok(ManagementLists.From(all).Sort("createdAt", j => j.CreatedAt).Sort("kind", j => j.Kind)
                .Sort("state", j => j.State).Search(j => j.Id + " " + j.Kind + " " + j.Operator)
                .Timestamp(j => j.CreatedAt).Read(context.Request.Query, "createdAt", true));
        }).Produces<ManagementPage<ManagementJobSnapshot>>();
        group.MapGet("/{id}", (string id, ManagementJobService jobs) => jobs.Find(id) is { } job
            ? Results.Ok(new ManagementJobResponse(true, job)) : Results.NotFound())
            .Produces<ManagementJobResponse>();
        group.MapPost("/{id}/{action}", (string id, string action, HttpContext ctx, ManagementJobService jobs, IAuditLogService audit) =>
        {
            var actor = GetIdentityName(ctx);
            try
            {
                var result = action switch
                {
                    "cancel" => jobs.Cancel(id) ? Results.Ok(new ManagementIdentitySuccess(true)) : Results.Conflict(),
                    "retry" => jobs.Retry(id, actor) is { } job ? Results.Accepted($"/api/v1/tasks/{job.Id}", new ManagementJobResponse(true, job)) : Results.Conflict(),
                    _ => (IResult)Results.NotFound()
                };
                audit.Log(actor, $"管理任务 {action}: {id}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                return result;
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { success = false, message = ex.Message }); }
        }).Produces<ManagementIdentitySuccess>().Produces<ManagementJobResponse>(202);
    }
}
