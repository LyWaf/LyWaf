using LyWaf.Services.Auth;
using LyWaf.Services.Acme;
using LyWaf.Services.ManagementTasks;
using LyWaf.Services.Sites;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;
using System.ComponentModel.DataAnnotations;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record SiteBatch([property: Required, MinLength(1), MaxLength(100)] string[] Ids,
        [property: Required, RegularExpression("^(enable|disable|delete|bind-rules)$")] string Action, string[]? RuleIds);
    private sealed record SiteBatchItem(string Id, bool Success, string? TaskId = null, string? Message = null);
    private sealed record SiteBatchResponse(bool Success, List<SiteBatchItem> Data);
    private static void MapSiteEndpoints(WebApplication app, int controlPort)
    {
        var group = app.MapGroup("/api/sites").RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem);
        group.MapGet("", (ManagedSiteProvider sites, SiteStatisticsService statistics, ManagementJobService jobs, IAcmeService certificates, IConfiguration config, Yarp.ReverseProxy.IProxyStateLookup proxy, HttpContext context) =>
        {
            var certs = certificates.GetCertificateStatuses();
            var tasks = jobs.List();
            var result = ManagementLists.From(sites.List()).Sort("name", s => s.Name).Sort("id", s => s.Id).Sort("enabled", s => s.Enabled)
                .Sort("updatedAt", s => tasks.FirstOrDefault(t => t.Kind == "site-publish" && t.ResourceIds.Contains(s.Id, StringComparer.OrdinalIgnoreCase))?.CreatedAt)
                .Search(s => s.Name + " " + s.Id + " " + string.Join(' ', s.Route.Match.Hosts ?? []))
                .Timestamp(s => tasks.FirstOrDefault(t => t.Kind == "site-publish" && t.ResourceIds.Contains(s.Id, StringComparer.OrdinalIgnoreCase))?.CreatedAt)
                .Read(context.Request.Query, "name");
            var listeners = config.GetSection("WafInfos:Listens").GetChildren().Select(l => new
            {
                host = l["Host"], port = int.TryParse(l["Port"], out var port) ? port : 0,
                https = bool.TryParse(l["IsHttps"], out var https) && https
            }).ToArray();
            var clusters = sites.GetConfig().Clusters.ToDictionary(c => c.ClusterId, StringComparer.OrdinalIgnoreCase);
            var health = proxy.GetClusters().ToDictionary(c => c.ClusterId, c => c.Destinations.Values.Select(d => new
            {
                id = d.DestinationId, active = d.Health.Active.ToString(), passive = d.Health.Passive.ToString(), connections = d.ConcurrentRequestCount,
                activeEnabled = c.Model.Config.HealthCheck?.Active?.Enabled == true,
                passiveEnabled = c.Model.Config.HealthCheck?.Passive?.Enabled == true
            }).ToArray(), StringComparer.OrdinalIgnoreCase);
            return TypedResults.Ok(new
            {
                result.Success, result.Total, result.Page, result.PageSize, result.SortBy, result.SortDirection, version = sites.Version,
                data = result.Data.Select(s => new
                {
                    site = s, traffic = statistics.Get(s.Id),
                    latestPublication = tasks.FirstOrDefault(t => t.Kind == "site-publish" && t.ResourceIds.Contains(s.Id, StringComparer.OrdinalIgnoreCase)),
                    certificates = certs.Where(c => SiteBindingResolver.UsesCertificate(s.Route.Match.Hosts, c.Domain)),
                    listeners = listeners.Where(l => SiteBindingResolver.UsesPort(s.Route.Match.Hosts, l.port)),
                    upstreams = s.Route.ClusterId is { } clusterId && clusters.TryGetValue(clusterId, out var cluster)
                        ? cluster.Destinations?.Select(d => new { id = d.Key, address = d.Value.Address }) : null,
                    upstreamHealth = s.Route.ClusterId is { } id ? health.GetValueOrDefault(id) : null,
                    policies = new { userRuleIds = s.RuleIds, systemRulesEnabled = true }
                }),
                latestPublication = jobs.List().FirstOrDefault(j => j.Kind == "site-publish"),
                clusters = sites.GetConfig().Clusters.Select(c => new { id = c.ClusterId, upstreams = c.Destinations?.Values.Select(d => d.Address) }),
                listeners
            });
        });
        group.MapGet("/export", (ManagedSiteProvider sites) => TypedResults.Ok(new { success = true, data = sites.Export() }));
        group.MapPost("", (ManagedSite request, HttpContext ctx, ManagedSiteProvider sites, ManagementJobService jobs, IAuditLogService audit) =>
            QueueSites([request], ctx, sites, jobs, audit)).Produces<ManagementJobResponse>(202);
        group.MapPost("/import", (SiteDocument request, HttpContext ctx, ManagedSiteProvider sites, ManagementJobService jobs, IAuditLogService audit) =>
            request.Schema != 1 || request.Current?.Sites is not { Length: > 0 and <= 1000 }
                ? Results.BadRequest(new { success = false, message = "不支持的站点导入格式" })
                : QueueSites(request.Current.Sites, ctx, sites, jobs, audit)).Produces<ManagementJobResponse>(202);
        group.MapPost("/batch", (SiteBatch request, HttpContext ctx, ManagedSiteProvider sites, ManagementJobService jobs, IAuditLogService audit) =>
        {
            if (request.Ids is not { Length: > 0 and <= 100 } || request.Action is not ("enable" or "disable" or "delete" or "bind-rules"))
                return Results.BadRequest(new { success = false, message = "批量操作无效，最多处理 100 项" });
            var all = sites.List().ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            var results = new List<SiteBatchItem>();
            foreach (var id in request.Ids.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!all.TryGetValue(id, out var site)) { results.Add(new(id, false, Message: "站点不存在")); continue; }
                var next = request.Action switch
                {
                    "enable" => site with { Enabled = true }, "disable" => site with { Enabled = false },
                    "delete" => site with { Deleted = true }, _ => site with { RuleIds = request.RuleIds }
                };
                try
                {
                    var actor = GetIdentityName(ctx);
                    var job = jobs.Enqueue("site-publish", actor, (log, ct) => sites.PublishAsync([next], actor, log, ct), resourceIds: [next.Id]);
                    results.Add(new(id, true, TaskId: job.Id));
                }
                catch (InvalidOperationException ex) { results.Add(new(id, false, Message: ex.Message)); }
            }
            audit.Log(GetIdentityName(ctx), $"站点批量 {request.Action}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Ok(new SiteBatchResponse(true, results));
        }).Produces<SiteBatchResponse>();
    }

    private static IResult QueueSites(ManagedSite[] changes, HttpContext ctx, ManagedSiteProvider sites, ManagementJobService jobs, IAuditLogService audit)
    {
        if (changes.Length == 0 || changes.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || s.Route is null || s.Route.Match is null))
            return Results.BadRequest(new { success = false, message = "站点和路由不能为空" });
        try
        {
            var actor = GetIdentityName(ctx);
            var version = ctx.Request.Headers.IfMatch.FirstOrDefault();
            var job = jobs.Enqueue("site-publish", actor, (log, ct) => sites.PublishAsync(changes, actor, log, ct, version), resourceIds: changes.Select(s => s.Id).ToArray());
            audit.Log(actor, $"创建站点发布任务: {job.Id}", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Accepted($"/api/v1/tasks/{job.Id}", new ManagementJobResponse(true, job));
        }
        catch (InvalidOperationException ex) { return Results.Conflict(new { success = false, message = ex.Message }); }
    }
}
