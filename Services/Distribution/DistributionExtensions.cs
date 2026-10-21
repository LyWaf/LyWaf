using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.Auth;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.Distribution;

/// <summary>注册多节点分发服务及控制端、目标节点 API。</summary>
public static class DistributionExtensions
{
    public static IServiceCollection AddLyWafDistribution(this IServiceCollection services, IConfiguration configuration)
    {
        LyWaf.Services.Configuration.ConfigurationReloadRecoveryExtensions.AddConfigurationReloadRecovery(services);
        services.AddOptions<DistributionOptions>().Bind(configuration.GetSection("Distribution"))
            .Validate(config => !config.Enabled || (config.HeartbeatIntervalSeconds is >= 1 and <= 300
                && config.SuspectFailureThreshold is >= 1 and <= 100
                && config.OfflineTimeoutSeconds is >= 10 and <= 3600
                && config.OfflineTimeoutSeconds >= config.HeartbeatIntervalSeconds),
                "Invalid distribution heartbeat or lifecycle thresholds").ValidateOnStart();
        services.AddOptions<NodeCommunicationOptions>().Bind(configuration.GetSection("NodeCommunication"))
            .Validate(config => config.Mode is "" or "Standalone" or "Controller" or "Node"
                && (config.Mode is not ("Controller" or "Node") || config.Role == config.Mode)
                && (config.Mode != "Standalone" || !config.Enabled), "Invalid node communication mode or role")
            .PostConfigure(config =>
            {
                if (config.Enabled && NodeMessageAuthentication.ValidId(config.ClusterId) && string.IsNullOrEmpty(config.NodeId))
                    config.NodeId = NodeIdentityService.Resolve(config).NodeId;
            })
            .Validate(config => !config.Enabled ||
                (NodeMessageAuthentication.ValidId(config.ClusterId) && NodeMessageAuthentication.ValidId(config.NodeId)
                 && config.Role is "Controller" or "Node"
                 && config.AllowedClockSkewSeconds is >= 5 and <= 300
                 && config.MaxMessageBytes is >= 1024 and <= 16 * 1024 * 1024
                 && config.Peers.All(pair => NodeMessageAuthentication.ValidId(pair.Key) && pair.Key != config.NodeId
                     && pair.Value.Role is "Controller" or "Node"
                     && NodeAdmissionPolicy.IsValid(pair.Value.AdmissionState)
                     && pair.Value.AllowedOperations is not null
                     && pair.Value.AllowedOperations.All(NodeProtocolOperations.IsSupported)
                     && pair.Value.Keys.Count > 0
                     && pair.Value.Keys.All(key => NodeMessageAuthentication.ValidId(key.Key) && !string.IsNullOrEmpty(key.Value)
                         && System.Text.Encoding.UTF8.GetByteCount(key.Value) >= 32))),
                "Invalid NodeCommunication identity, bounds or peer keys")
            .Validate(config => !config.Enabled || NodeAdmissionPolicy.HasSingleConfigurationWriter(config),
                "Only one admitted configuration writer is supported per node").ValidateOnStart();
        services.AddSingleton<NodeMessageAuthentication>();
        services.AddSingleton<NodeSocketConnections>();
        services.AddSingleton<NodeSocketProtocol>();
        services.AddSingleton<NodeIdentityService>();
        services.AddNodeHandshakeServices();
        services.AddHostedService(sp => sp.GetRequiredService<NodeIdentityService>());
        services.AddHostedService<NodeSocketConnector>();
        services.AddOptions<DistributionOptions>().PostConfigure<Microsoft.Extensions.Options.IOptionsMonitor<NodeCommunicationOptions>>((config, communication) =>
        {
            if (!communication.CurrentValue.Enabled) return;
            var identity = communication.CurrentValue.NodeId;
            if (string.IsNullOrEmpty(configuration["Distribution:NodeId"])) config.NodeId = identity;
            else if (config.Enabled && config.NodeId != identity)
                throw new InvalidOperationException("Distribution NodeId must match NodeCommunication NodeId");
        });
        services.AddOptions<LyWaf.Services.RuntimeState.RuntimeStateOptions>().PostConfigure<Microsoft.Extensions.Options.IOptionsMonitor<NodeCommunicationOptions>>((config, communication) =>
        {
            if (!communication.CurrentValue.Enabled) return;
            var identity = communication.CurrentValue.NodeId;
            if (string.IsNullOrEmpty(configuration["RuntimeState:NodeId"])) config.NodeId = identity;
            else if (config.Enabled && config.NodeId != identity)
                throw new InvalidOperationException("RuntimeState NodeId must match NodeCommunication NodeId");
        });
        services.AddSingleton<NodeCommandInbox>();
        services.AddHttpClient("distribution-node").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<DistributionAuthentication>();
        services.AddSingleton<DistributionNodeService>();
        services.AddHostedService<NodeConfigurationMaintenanceService>();
        services.AddSingleton<IDistributionNodeClient, DistributionNodeClient>();
        services.AddHostedService<NodeHandshakeMonitor>();
        services.AddSingleton<NodeReconciliationService>();
        services.AddHostedService<NodeOutboxRecoveryService>();
        services.AddSingleton<DistributionControllerService>();
        services.AddHostedService<DistributionOutboxConfirmationService>();
        services.AddSingleton<NodeTelemetryCollector>();
        services.AddSingleton(LyWaf.Services.Telemetry.NodeCommunicationTelemetry.Shared);
        services.AddHostedService<LyWaf.Services.Telemetry.NodeCommunicationHttpObserver>();
        services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, LyWaf.Services.Telemetry.NodeCommunicationObservationStartup>();
        services.AddSingleton<NodeDashboardDiagnostics>();
        services.AddSingleton<NodeLogicalBanTelemetry>();
        services.AddHostedService(sp => sp.GetRequiredService<NodeLogicalBanTelemetry>());
        services.AddSingleton<NodeDashboardAgent>();
        services.AddSingleton<MultiNodeDashboardService>();
        services.AddHostedService(sp => sp.GetRequiredService<MultiNodeDashboardService>());
        return services;
    }

    public static WebApplication MapLyWafDistributionApi(this WebApplication app, int controlPort)
    {
        if (app.Services.GetService<IServiceProviderIsService>()?.IsService(typeof(NodeSetupStore)) == true)
            app.MapNodeSetup(controlPort);
        app.MapNodeCommunication(controlPort);
        app.MapNodeSocket(controlPort);
        app.MapNodeIdentityConflictManagement(controlPort);
        app.MapNodeReconciliation(controlPort);
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
        }).RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.Read);

        app.MapGet("/api/distribution/deployments", async (HttpContext context, DistributionControllerService controller) =>
            Results.Json(new { success = true, deployments = await controller.ListAsync(context.RequestAborted) }))
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.Read);

        app.MapGet("/api/distribution/nodes", async (HttpContext context, DistributionControllerService controller) =>
            Results.Json(new { success = true, nodes = await controller.GetNodeStatusesAsync(context.RequestAborted) }))
            .RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.Read);

        app.MapPost("/api/distribution/deployments", async (HttpContext context, DistributionControllerService controller, MultiNodeDashboardService dashboard, CreateDistributionRequest request) =>
        {
            try
            {
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
            catch (KeyNotFoundException ex) { return ManagementApiContract.Error(context, 404, "not_found", ex.Message); }
            catch (InvalidOperationException ex) { return ManagementApiContract.Error(context, 409, "conflict", ex.Message); }
        }).RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem);

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
                catch (KeyNotFoundException ex) { return ManagementApiContract.Error(context, 404, "not_found", ex.Message); }
                catch (InvalidOperationException ex) { return ManagementApiContract.Error(context, 409, "conflict", ex.Message); }
            }).RequireHost($"*:{controlPort}").RequireManagementPermission(ManagementPermissions.ManageSystem);
        }
        return app;
    }

    private static void Audit(HttpContext context, string action) =>
        context.RequestServices.GetRequiredService<IAuditLogService>().Log(
            context.Items["Username"]?.ToString() ?? "unknown", action,
            context.Connection.RemoteIpAddress?.ToString() ?? "");
}
