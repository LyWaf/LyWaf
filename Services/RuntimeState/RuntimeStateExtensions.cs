using LyWaf.Services.ManagementApi;
using LyWaf.Services.Auth;

namespace LyWaf.Services.RuntimeState;

/// <summary>注册运行状态核心、后台持久化、节点复制及管理 API。</summary>
public static class RuntimeStateExtensions
{
    private sealed record RuntimeStateMergedResponse(bool Success);
    public static IServiceCollection AddLyWafRuntimeState(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LyWaf.Services.Statistic.CcIngressOptions>().Bind(configuration.GetSection("CcIngress"));
        services.AddSingleton<LyWaf.Services.Statistic.CcIngressOwnership>();
        services.AddOptions<RuntimeStateOptions>()
            .Bind(configuration.GetSection("RuntimeState"))
            .Validate(static options => !options.Enabled ||
                (!string.IsNullOrWhiteSpace(options.NodeId)
                 && options.QueueCapacity >= 100
                 && options.MaxValueBytes is >= 1024 and <= 2 * 1024 * 1024
                 && options.MaxJournalBytes >= options.MaxValueBytes
                 && options.Peers.Values.All(peer => Uri.TryCreate(peer.Endpoint, UriKind.Absolute, out var uri)
                     && uri.Scheme is "http" or "https")),
                "RuntimeState 节点、容量或 Peer Endpoint 配置无效")
            .Validate(static options => RuntimeStateReplicationAuthorization.IsValidConfiguration(options),
                "RuntimeState replication policies or protection group key bindings are invalid")
            .Validate(RuntimeStateReplicator.IsValidQueueConfiguration, "RuntimeState replication queue limits are invalid")
            .Validate(RuntimeStateStore.ValidCapacityOptions, "RuntimeState hard capacity limits are invalid")
            .ValidateOnStart();
        services.AddSingleton<RuntimeStateStore>();
        services.AddSingleton<IRuntimeStateStore>(sp => sp.GetRequiredService<RuntimeStateStore>());
        services.AddHostedService(sp => sp.GetRequiredService<RuntimeStateStore>());
        services.AddSingleton<RuntimeStateAuthentication>();
        services.AddSingleton<RuntimeStateSyncSender>();
        services.AddSingleton<CcPropagationTracker>();
        services.AddSingleton<RuntimeStateSyncReceiver>();
        services.AddSingleton<CcCaptchaReleaseIssuer>();
        services.AddSingleton<CcCaptchaReleaseClient>();
        services.AddSingleton(LyWaf.Services.Statistic.CcBanLog.Shared);
        services.AddSingleton<LyWaf.Services.Statistic.CcDecisionService>();
        services.AddSingleton<LyWaf.Services.Statistic.CcCohortState>();
        services.AddSingleton<LyWaf.Services.Statistic.CcPeerQueryClient>();
        services.AddHostedService<LyWaf.Services.Statistic.CcCohortMonitor>();
        services.AddSingleton<LyWaf.Services.Statistic.CcExecutionQueryService>();
        services.AddSingleton<LyWaf.Services.Statistic.CcExecutionRelayService>();
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, LyWaf.Services.Statistic.CcQueryTimingStartupFilter>();
        services.AddHostedService(sp => sp.GetRequiredService<LyWaf.Services.Statistic.CcDecisionService>());
        services.AddSingleton<RuntimeStateReplicator>();
        services.AddHostedService(sp => sp.GetRequiredService<RuntimeStateReplicator>());
        return services;
    }

    public static WebApplication MapLyWafRuntimeStateApi(this WebApplication app, int controlPort)
    {
        app.MapPost("/api/runtime-state/agent/merge", async (
            HttpContext context, RuntimeStateAuthentication auth, IRuntimeStateStore store) =>
        {
            var (mutation, error) = await auth.ReadAndVerifyAsync(context, context.RequestAborted);
            if (mutation is null && error?.StartsWith("runtime_", StringComparison.Ordinal) == true)
                return Results.Json(new { success = false, errorCode = error, messageKey = "node.errors." + error }, statusCode: 403);
            if (mutation is null) return ManagementApiContract.Error(context, 401, "runtime_state_auth_failed", error ?? "认证失败");
            try
            {
                store.Merge(mutation);
                return Results.Json(new { success = true });
            }
            catch (RuntimeStateCapacityException)
            {
                return LyWaf.Services.NodeCommunication.NodeProtocolResponse.Error(context,
                    "node_runtime_store_unavailable", 503);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return ManagementApiContract.Error(context, 400, "runtime_state_invalid", ex.Message);
            }
        }).Accepts<RuntimeStateMutation>("application/json").Produces<RuntimeStateMergedResponse>().RequireHost($"*:{controlPort}");

        app.MapGet("/api/runtime-state/status", (IRuntimeStateStore store, RuntimeStateReplicator replicator) =>
            TypedResults.Ok(new { success = true, status = store.GetStatus(),
                replication = new { droppedMutations = replicator.DroppedMutations,
                    coalescedMutations = replicator.CoalescedMutations, queue = replicator.QueueStatus,
                    authorizationRejectedMutations = replicator.AuthorizationRejectedMutations } }))
            .RequireHost($"*:{controlPort}").RequireManagementPermission();
        app.MapGet("/api/runtime-state/snapshots", (HttpContext context, string? domain, IRuntimeStateStore store) =>
            TypedResults.Ok(ManagementLists.From(store.GetSnapshots(domain)).Sort("domain", item => item.Domain)
                .Sort("key", item => item.Key).Sort("changedAt", item => item.ChangedAt).Sort("sequence", item => item.Sequence)
                .Search(item => $"{item.Domain} {item.Key} {item.NodeId}").Timestamp(item => item.ChangedAt)
                .Read(context.Request.Query, "changedAt", true))).RequireHost($"*:{controlPort}").RequireManagementPermission();
        return app;
    }
}
