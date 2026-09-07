using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Services.Distribution;

/// <summary>注册多节点分发服务及控制端、目标节点 API。</summary>
public static class DistributionExtensions
{
    public static IServiceCollection AddLyWafDistribution(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DistributionOptions>(configuration.GetSection("Distribution"));
        services.AddHttpClient("distribution-node");
        services.AddSingleton<DistributionAuthentication>();
        services.AddSingleton<DistributionNodeService>();
        services.AddSingleton<IDistributionNodeClient, DistributionNodeClient>();
        services.AddSingleton<DistributionControllerService>();
        services.AddSingleton<NodeDashboardAgent>();
        services.AddSingleton<MultiNodeDashboardService>();
        return services;
    }

    public static WebApplication MapLyWafDistributionApi(this WebApplication app, int controlPort)
    {
        app.MapPost("/api/distribution/agent/prepare", async (HttpContext context, DistributionAuthentication auth, DistributionNodeService node) =>
        {
            var (request, error) = await auth.ReadAndVerifyAsync<PrepareConfigurationRequest>(context, "prepare", context.RequestAborted);
            if (request == null) return ManagementApiContract.Error(context, 401, "distribution_auth_failed", error ?? "认证失败");
            var result = await node.PrepareAsync(request, context.RequestAborted);
            return Results.Json(result, statusCode: result.Ack ? 200 : 409);
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/distribution/agent/commit", async (HttpContext context, DistributionAuthentication auth, DistributionNodeService node) =>
        {
            var (request, error) = await auth.ReadAndVerifyAsync<DistributionCommandRequest>(context, "commit", context.RequestAborted);
            if (request == null) return ManagementApiContract.Error(context, 401, "distribution_auth_failed", error ?? "认证失败");
            var result = await node.CommitAsync(request.DeploymentId, context.RequestAborted);
            return Results.Json(result, statusCode: result.Ack ? 200 : 409);
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/distribution/agent/rollback", async (HttpContext context, DistributionAuthentication auth, DistributionNodeService node) =>
        {
            var (request, error) = await auth.ReadAndVerifyAsync<DistributionCommandRequest>(context, "rollback", context.RequestAborted);
            if (request == null) return ManagementApiContract.Error(context, 401, "distribution_auth_failed", error ?? "认证失败");
            var result = await node.RollbackAsync(request.DeploymentId, context.RequestAborted);
            return Results.Json(result, statusCode: result.Ack ? 200 : 409);
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/distribution/agent/status", async (HttpContext context, DistributionAuthentication auth, DistributionNodeService node) =>
        {
            var (request, error) = await auth.ReadAndVerifyAsync<DistributionCommandRequest>(context, "status", context.RequestAborted);
            if (request == null) return ManagementApiContract.Error(context, 401, "distribution_auth_failed", error ?? "认证失败");
            return Results.Json(node.GetStatus());
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/distribution/agent/dashboard", async (HttpContext context, DistributionAuthentication auth, NodeDashboardAgent dashboard) =>
        {
            var (request, error) = await auth.ReadAndVerifyAsync<DistributionCommandRequest>(context, "dashboard", context.RequestAborted);
            if (request == null) return ManagementApiContract.Error(context, 401, "distribution_auth_failed", error ?? "认证失败");
            return Results.Json(dashboard.Capture());
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/distribution/dashboard", async (HttpContext context, MultiNodeDashboardService dashboard) =>
        {
            try
            {
                var username = context.Items["Username"]?.ToString() ?? "unknown";
                return Results.Json(new { success = true, dashboard = await dashboard.GetAsync(username, context.RequestAborted) });
            }
            catch (InvalidOperationException ex)
            {
                return ManagementApiContract.Error(context, 409, "distribution_not_controller", ex.Message);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/distribution/deployments", async (HttpContext context, DistributionControllerService controller) =>
            Results.Json(new { success = true, deployments = await controller.ListAsync(context.RequestAborted) }))
            .RequireHost($"*:{controlPort}");

        app.MapGet("/api/distribution/nodes", async (HttpContext context, DistributionControllerService controller) =>
            Results.Json(new { success = true, nodes = await controller.GetNodeStatusesAsync(context.RequestAborted) }))
            .RequireHost($"*:{controlPort}");

        app.MapPost("/api/distribution/deployments", async (HttpContext context, DistributionControllerService controller, MultiNodeDashboardService dashboard) =>
        {
            try
            {
                var request = await context.Request.ReadFromJsonAsync<CreateDistributionRequest>(context.RequestAborted)
                    ?? throw new ArgumentException("请求内容为空");
                var username = context.Items["Username"]?.ToString() ?? "unknown";
                var permitted = request.NodeIds is null || request.NodeIds.Count == 0
                    ? dashboard.CanOperateGlobally(username)
                    : dashboard.CanOperate(username, request.NodeIds);
                if (!permitted)
                {
                    Audit(context, "拒绝创建无权限的配置分发任务");
                    return ManagementApiContract.Error(context, 403, "distribution_forbidden", "没有目标节点的操作权限");
                }
                var result = await controller.CreateAsync(request, context.RequestAborted);
                Audit(context, $"创建配置分发任务: {result.Id}");
                return Results.Json(new { success = true, deployment = result });
            }
            catch (Exception ex) { return ManagementApiContract.Error(context, 400, "distribution_create_failed", ex.Message); }
        }).RequireHost($"*:{controlPort}");

        foreach (var command in new[] { "pause", "resume", "rollback" })
        {
            var operation = command;
            app.MapPost($"/api/distribution/deployments/{{id}}/{operation}", async (string id, HttpContext context,
                DistributionControllerService controller, MultiNodeDashboardService dashboard) =>
            {
                try
                {
                    var deployment = (await controller.ListAsync(context.RequestAborted)).FirstOrDefault(x => x.Id == id)
                        ?? throw new KeyNotFoundException("分发任务不存在");
                    var username = context.Items["Username"]?.ToString() ?? "unknown";
                    if (!dashboard.CanOperate(username, deployment.NodeOrder))
                    {
                        Audit(context, $"拒绝 {operation} 无权限的配置分发任务: {id}");
                        return ManagementApiContract.Error(context, 403, "distribution_forbidden", "没有任务涉及节点的操作权限");
                    }
                    var result = operation switch
                    {
                        "pause" => await controller.PauseAsync(id, context.RequestAborted),
                        "resume" => await controller.ResumeAsync(id, context.RequestAborted),
                        _ => await controller.RollbackAsync(id, context.RequestAborted)
                    };
                    Audit(context, $"{operation} 配置分发任务: {id}");
                    return Results.Json(new { success = true, deployment = result });
                }
                catch (Exception ex) { return ManagementApiContract.Error(context, 400, $"distribution_{operation}_failed", ex.Message); }
            }).RequireHost($"*:{controlPort}");
        }
        return app;
    }

    private static void Audit(HttpContext context, string action) =>
        context.RequestServices.GetRequiredService<IAuditLogService>().Log(
            context.Items["Username"]?.ToString() ?? "unknown", action,
            context.Connection.RemoteIpAddress?.ToString() ?? "");
}
