using System.Diagnostics;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Auth;
using LyWaf.Services.Lifecycle;
using LyWaf.Services.ManagementTasks;
using LyWaf.Shared;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapUpgradeActivationEndpoints(WebApplication app, int port)
    {
        app.MapPost("/api/upgrade/activate", (ActivateUpgradeRequest request, HttpContext context,
            UpgradePackageService upgrades, ManagementJobService jobs, GracefulShutdownCoordinator drain,
            IHostApplicationLifetime lifetime, IConfiguration config, IAuditLogService audit) =>
                QueueUpgradeHandoff(request.Version, false, context, upgrades, jobs, drain, lifetime, config, audit))
            .Produces<ManagementJobResponse>(202).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.ManageSystem);
        app.MapPost("/api/upgrade/rollback", (HttpContext context, UpgradePackageService upgrades,
            ManagementJobService jobs, GracefulShutdownCoordinator drain, IHostApplicationLifetime lifetime,
            IConfiguration config, IAuditLogService audit) =>
                QueueUpgradeHandoff(null, true, context, upgrades, jobs, drain, lifetime, config, audit))
            .Produces<ManagementJobResponse>(202).RequireHost($"*:{port}").RequireManagementPermission(ManagementPermissions.ManageSystem);
    }

    internal static IResult QueueUpgradeHandoff(string? version, bool rollback, HttpContext context,
        UpgradePackageService upgrades, ManagementJobService jobs, GracefulShutdownCoordinator drain,
        IHostApplicationLifetime lifetime, IConfiguration config, IAuditLogService audit)
    {
        if (!rollback && (string.IsNullOrWhiteSpace(version) || version.Length > 64 ||
            version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')))
            throw new ArgumentException("请选择有效的候选版本", nameof(version));
        var actor = GetIdentityName(context);
        var configPath = SharedData.ConfigFilePath;
        var currentEntry = GetRunningEntryPoint();
        var currentVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "current";
        var job = jobs.Enqueue(rollback ? "upgrade-rollback" : "upgrade-activate", actor, async (log, token) =>
        {
            UpgradeReleaseInfo release;
            if (rollback)
            {
                var pointers = await upgrades.GetPointersAsync(token);
                if (string.IsNullOrWhiteSpace(pointers.Previous) || string.IsNullOrWhiteSpace(pointers.PreviousEntryPoint) || !File.Exists(pointers.PreviousEntryPoint))
                    throw new InvalidDataException("没有可用的上一程序版本");
                release = new(pointers.Previous, Path.GetDirectoryName(pointers.PreviousEntryPoint)!, pointers.PreviousEntryPoint,
                    "rollback", DateTimeOffset.UtcNow, "validated", "previous-version");
            }
            else release = (await upgrades.ListAsync(token)).SingleOrDefault(item => item.Version == version)
                ?? throw new InvalidDataException("候选版本不存在");
            if (SharedData.UpgradeRequested || drain.Snapshot().Draining) throw new InvalidDataException("实例已进入接管或排空流程");
            log($"开始候选就绪检查: {release.Version}");
            var probe = await upgrades.StartReadinessProbeAsync(release, config, configPath, token);
            try
            {
                if (rollback) await upgrades.MarkPreviousPendingAsync(token);
                else await upgrades.MarkPendingAsync(release.Version, currentVersion, currentEntry, token);
            }
            catch
            {
                try { using var process = Process.GetProcessById(probe.ProcessId); process.Kill(entireProcessTree: true); }
                catch (ArgumentException) { }
                catch (Exception cleanupError) when (cleanupError is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    log($"候选进程清理未完成 (PID {probe.ProcessId})，请检查该进程；原始接管错误保留");
                }
                throw;
            }
            // The durable pending pointer is committed. Cancellation no longer implies rollback.
            SharedData.PendingUpgradeVersion = release.Version;
            SharedData.PendingUpgradeEntryPoint = release.EntryPoint;
            SharedData.PendingUpgradeProbeProcessId = probe.ProcessId;
            SharedData.UpgradeRequested = true;
            log("候选已就绪并提交接管，开始排空；最终运行版本以版本列表为准");
            drain.BeginDrain();
            var seconds = Math.Clamp(config.GetValue("Lifecycle:DrainTimeoutSeconds", 30), 1, 600);
            if (!await drain.WaitForDrainAsync(TimeSpan.FromSeconds(seconds))) log("排空等待超时，按生命周期策略继续接管");
            jobs.StopAcceptingForShutdown();
            log("当前进程即将退出，任务记录不会跨进程保留");
            lifetime.StopApplication();
        }, TimeSpan.FromMinutes(15), version is null ? [] : [version]);
        audit.Log(actor, $"创建程序{(rollback ? "回退" : "激活")}任务: {job.Id}", context.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Accepted($"/api/v1/tasks/{job.Id}", new ManagementJobResponse(true, job));
    }
}
