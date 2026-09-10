using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Notifications;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapNotificationEndpoints(WebApplication app, int controlPort)
    {
        var group = app.MapGroup("/api/notifications").RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem);
        group.MapGet("/channels", (HttpContext context, NotificationService notifications) => Results.Ok(
            ManagementLists.From(notifications.Channels()).Sort("name", channel => channel.Name).Sort("kind", channel => channel.Kind)
                .Search(channel => channel.Name + " " + channel.Kind + " " + channel.Id).Read(context.Request.Query, "name")))
            .Produces<ManagementPage<NotificationChannel>>();
        group.MapGet("/deliveries", (HttpContext context, NotificationService notifications) => Results.Ok(
            ManagementLists.From(notifications.Deliveries()).Sort("lastSeen", delivery => delivery.Message.LastSeen)
                .Sort("attempts", delivery => delivery.Attempts).Sort("state", delivery => delivery.State)
                .Search(delivery => delivery.ChannelId + " " + delivery.Message.RuleId + " " + delivery.State)
                .Timestamp(delivery => delivery.Message.LastSeen).Read(context.Request.Query, "lastSeen", true)))
            .Produces<ManagementPage<NotificationDelivery>>();
        group.MapPost("/channels", (NotificationChannel channel, NotificationService notifications, HttpContext ctx, IAuditLogService audit) =>
        {
            notifications.SaveChannel(channel);
            audit.Log(GetIdentityName(ctx), $"保存通知渠道: {channel.Id}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return TypedResults.Ok(new { success = true });
        });
        group.MapDelete("/channels/{id}", (string id, NotificationService notifications, HttpContext ctx, IAuditLogService audit) =>
        {
            if (!notifications.DeleteChannel(id)) return Results.NotFound();
            audit.Log(GetIdentityName(ctx), $"删除通知渠道: {id}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Ok(new ManagementIdentitySuccess(true));
        }).Produces<ManagementIdentitySuccess>();
        group.MapPost("/deliveries/{id}/retry", (string id, NotificationService notifications, HttpContext ctx, IAuditLogService audit) =>
        {
            if (!notifications.Retry(id)) return Results.Conflict();
            audit.Log(GetIdentityName(ctx), $"重试通知: {id}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Ok(new ManagementIdentitySuccess(true));
        }).Produces<ManagementIdentitySuccess>();
    }
}
