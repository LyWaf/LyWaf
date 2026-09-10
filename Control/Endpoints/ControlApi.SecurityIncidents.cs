using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Incidents;
using LyWaf.Services.WafRule;
using LyWaf.Services.ManagementApi;
using System.ComponentModel.DataAnnotations;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record IncidentUpdate(IncidentDisposition State, [property: Required, StringLength(200)] string Note,
        [property: Range(1, int.MaxValue)] int ExpectedVersion);
    private sealed record IncidentExplanation(string Id, string Name, string? Description,
        List<WafConditionGroup> ConditionGroups, WafLogicalOperator GroupOperator);
    private sealed record IncidentDetailResponse(bool Success, SecurityIncident Data, IncidentExplanation? Explanation,
        SecurityIncident[] Related);
    private sealed record SecurityIncidentPage : ManagementPage<SecurityIncident>
    {
        public long Dropped { get; }
        public SecurityIncidentPage(ManagementPage<SecurityIncident> page, long dropped)
            : base(page.Success, page.Data, page.Total, page.Page, page.PageSize, page.SortBy, page.SortDirection)
            => Dropped = dropped;
    }

    private static void MapSecurityIncidentEndpoints(WebApplication app, int controlPort)
    {
        var group = app.MapGroup("/api/security-incidents").RequireHost($"*:{controlPort}").RequireManagementPermission();
        group.MapGet("/{id}/requests", async (string id, string? cursor, int? pageSize, IncidentRequestStore records, HttpContext context) =>
        {
            var page = await records.ReadAsync(id, cursor, pageSize ?? 20, context.RequestAborted);
            return TypedResults.Ok(new { success = true, data = page.Data, total = page.Total, nextCursor = page.NextCursor, dropped = page.Dropped });
        });
        group.MapGet("", (SecurityIncidentService incidents, string? site, string? rule, string? ip, string? attackType,
            IncidentDisposition? state, HttpContext context) =>
        {
            var all = incidents.List().Where(e => (site == null || e.Site.Contains(site, StringComparison.OrdinalIgnoreCase)) &&
                (rule == null || e.RuleId.Contains(rule, StringComparison.OrdinalIgnoreCase)) && (ip == null || e.Ip.Contains(ip, StringComparison.Ordinal)) &&
                (attackType == null || e.AttackType == attackType) && (state == null || e.State == state)).ToArray();
            var result = ManagementLists.From(all).Sort("lastSeen", e => e.LastSeen).Sort("firstSeen", e => e.FirstSeen)
                .Sort("count", e => e.Count).Sort("state", e => e.State)
                .Search(e => e.Site + " " + e.Ip + " " + e.RuleId + " " + e.AttackType)
                .Timestamp(e => e.LastSeen).Read(context.Request.Query, "lastSeen", true);
            return TypedResults.Ok(new SecurityIncidentPage(result, incidents.Dropped));
        });
        group.MapGet("/{id}", (string id, SecurityIncidentService incidents, IWafRuleService rules) =>
        {
            var incident = incidents.Find(id);
            if (incident is null) return Results.NotFound();
            var rule = rules.GetRule(incident.RuleId);
            return Results.Ok(new IncidentDetailResponse(true, incident,
                rule is null ? null : new IncidentExplanation(rule.Id, rule.Name, rule.Description, rule.ConditionGroups, rule.GroupOperator),
                incidents.List().Where(e => e.Id != id && (e.Ip == incident.Ip || e.RuleId == incident.RuleId)).Take(20).ToArray()));
        }).Produces<IncidentDetailResponse>();
        group.MapPut("/{id}/disposition", async (string id, IncidentUpdate request, HttpContext ctx, SecurityIncidentService incidents, IAuditLogService audit) =>
        {
                if (!await incidents.SetDisposition(id, request.State, request.Note, GetIdentityName(ctx), request.ExpectedVersion, ctx.RequestAborted))
                    return Results.Conflict(new { success = false, message = "事件不存在或已被其他人处理，请刷新" });
                audit.Log(GetIdentityName(ctx), $"事件处置 {id}: {request.State}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Ok(new ManagementIdentitySuccess(true));
        }).Produces<ManagementIdentitySuccess>();
        group.MapPost("/{id}/{action}", (string id, string action, HttpContext ctx, SecurityIncidentService incidents, IAccessControlService access, IAuditLogService audit) =>
        {
            var incident = incidents.Find(id);
            if (incident == null) return Results.NotFound();
            if (action is not ("allow-ip" or "block-ip")) return Results.NotFound();
            var success = action == "allow-ip" ? access.AddWhitelist(incident.Ip) : access.AddBlacklist(incident.Ip);
            audit.Log(GetIdentityName(ctx), $"事件 {id} 执行 {action}: {incident.Ip}, 结果 {success}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return success ? Results.Ok(new ManagementIdentitySuccess(true)) : Results.Conflict(new { success = false, message = "IP 已存在或无效" });
        }).Produces<ManagementIdentitySuccess>().RequireManagementPermission(ManagementPermissions.ManageSystem);
    }
}
