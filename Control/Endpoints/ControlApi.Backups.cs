using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Backup;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.ManagementTasks;
using System.ComponentModel.DataAnnotations;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record BackupRequest([property: StringLength(1024)] string Password = "",
        [property: RegularExpression("^(configuration|statistics|instance)$")] string Scope = "configuration");
    private sealed record BackupUploadResponse(bool Success, string Id);
    private static void MapBackupEndpoints(WebApplication app, int controlPort)
    {
        var group = app.MapGroup("/api/backups").RequireHost($"*:{controlPort}")
            .RequireManagementPermission(ManagementPermissions.ManageSystem)
            .WithMetadata(new ManagementStepUpExemption());
        group.MapGet("", (HttpContext context, ConfigurationBackupService backups) =>
        {
            var result = ManagementLists.From(backups.List()).Sort("createdAt", b => b.CreatedAt).Sort("size", b => b.Size).Sort("scope", b => b.Scope)
                .Search(b => b.Id + " " + b.Scope).Timestamp(b => b.CreatedAt).Read(context.Request.Query, "createdAt", true);
            return TypedResults.Ok(new { result.Success, result.Data, result.Total, result.Page, result.PageSize, result.SortBy, result.SortDirection, statisticsRestorePending = backups.StatisticsRestorePending, instanceStatePending = backups.InstanceStatePending });
        });
        group.MapDelete("/instance-pending", async (HttpContext context, ConfigurationBackupService backups, IAuditLogService audit) =>
        {
            await backups.CancelInstanceStateAsync(context.RequestAborted);
            audit.Log(GetIdentityName(context), "取消待启动执行的实例状态任务", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return TypedResults.Ok(new { success = true });
        });
        group.MapDelete("/statistics-pending", async (HttpContext context, ConfigurationBackupService backups, IAuditLogService audit) =>
        {
            await backups.CancelStatisticsRestoreAsync(context.RequestAborted);
            audit.Log(GetIdentityName(context), "取消待重启执行的统计数据恢复", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return TypedResults.Ok(new { success = true });
        });
        group.MapPost("/upload", async (HttpContext context, ConfigurationBackupService backups, IAuditLogService audit) =>
        {
            try
            {
                var scope = context.Request.Query["scope"].ToString();
                if (scope is not "" and not "configuration" and not "statistics" and not "instance") return Results.BadRequest(new { success = false, message = "备份范围无效" });
                var id = await backups.UploadAsync(context.Request.Body, context.RequestAborted, scope == "statistics", scope == "instance");
                audit.Log(GetIdentityName(context), $"上传配置备份: {id}", context.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Ok(new BackupUploadResponse(true, id));
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
            catch (IOException) { return Results.BadRequest(new { success = false, message = "备份上传不完整或不可写" }); }
        }).Produces<BackupUploadResponse>();
        group.MapPost("", (BackupRequest request, HttpContext context, ConfigurationBackupService backups, IAuditLogService audit) =>
        {
            var actor = GetIdentityName(context);
            if (request.Scope is not "configuration" and not "statistics" and not "instance") return (IResult)Results.BadRequest(new { success = false, message = "备份范围无效" });
            var job = request.Scope == "instance" ? backups.CreateInstance(request.Password, actor) : request.Scope == "statistics" ? backups.CreateStatistics(request.Password, actor) : backups.Create(request.Password, actor);
            audit.Log(actor, $"创建配置备份任务: {job.Id}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Accepted($"/api/v1/tasks/{job.Id}", new { success = true, data = job });
        }).Produces<ManagementJobResponse>(202);
        group.MapPost("/{id}/{action}", (string id, string action, BackupRequest request, HttpContext context, ConfigurationBackupService backups, IAuditLogService audit) =>
        {
            if (action is not "preflight" and not "restore") return (IResult)Results.NotFound();
            var actor = GetIdentityName(context);
            var job = backups.Restore(id, request.Password, action == "preflight", actor);
            audit.Log(actor, $"配置备份 {action}: {id}, 任务 {job.Id}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Accepted($"/api/v1/tasks/{job.Id}", new { success = true, data = job });
        }).Produces<ManagementJobResponse>(202);
        group.MapGet("/{id}/download", (string id, HttpContext context, ConfigurationBackupService backups) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var path = backups.GetPath(id);
            return File.Exists(path) ? Results.File(path, "application/octet-stream", id + ".lybackup", enableRangeProcessing: true) : (IResult)Results.NotFound();
        }).Produces<byte[]>(200, "application/octet-stream").Produces<byte[]>(206, "application/octet-stream");
        group.MapDelete("/{id}", async (string id, HttpContext context, ConfigurationBackupService backups, IAuditLogService audit) =>
        {
            await backups.DeleteAsync(id, context.RequestAborted);
            audit.Log(GetIdentityName(context), $"删除配置备份: {id}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return TypedResults.Ok(new { success = true });
        });
    }
}
