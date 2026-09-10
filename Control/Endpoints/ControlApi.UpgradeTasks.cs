using LyWaf.Services.AuditLog;
using LyWaf.Services.Lifecycle;
using LyWaf.Services.ManagementTasks;
using LyWaf.Shared;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapUpgradeTaskEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/upgrade-stage", EnqueueUpgradeStage).Produces<ManagementJobResponse>(202);
    }

    private static IResult EnqueueUpgradeStage(StageUpgradeRequest request, HttpContext context,
        UpgradePackageService upgrades, ManagementJobService jobs, IAuditLogService audit)
    {
            if (string.IsNullOrWhiteSpace(request.PackageName) || request.PackageName.Length > 255 ||
                Path.GetFileName(request.PackageName) != request.PackageName)
                throw new ArgumentException("请选择升级目录中的包文件名", nameof(request.PackageName));
            if (string.IsNullOrWhiteSpace(request.Version) || request.Version.Length > 100)
                throw new ArgumentException("版本号不能为空且不能超过 100 字符", nameof(request.Version));
            if (request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit))
                throw new ArgumentException("SHA256 必须是 64 位十六进制摘要", nameof(request.Sha256));
            var actor = GetIdentityName(context);
            var config = SharedData.ConfigFilePath;
            var job = jobs.Enqueue("upgrade-stage", actor, async (log, token) =>
            {
                log($"开始校验候选版本: {request.Version}");
                var release = await upgrades.StageAsync(request.PackageName, request.Version, request.Sha256, config, token);
                log($"候选版本 {release.Version} 已校验入库，尚未切换运行版本");
            }, TimeSpan.FromMinutes(15), [request.Version]);
            audit.Log(actor, $"创建升级准备任务: {job.Id}, {request.Version}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Accepted($"/api/v1/tasks/{job.Id}", new { success = true, data = job });
    }
}
