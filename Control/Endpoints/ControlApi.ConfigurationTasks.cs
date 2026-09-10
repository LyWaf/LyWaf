using LyWaf.Services.AuditLog;
using LyWaf.Services.Configuration;
using LyWaf.Services.ManagementTasks;

namespace LyWaf.Control;

public static partial class ControlApi
{
    /// <summary>受理主配置发布或历史回滚；后台执行器不持有请求上下文。</summary>
    internal static IResult QueueConfigurationChange(HttpContext context, IConfiguration configuration,
        string? content, string? versionId)
    {
        if (configuration is not IConfigurationRoot root) throw new InvalidDataException("当前配置不支持重载");
        if (content != null && (string.IsNullOrWhiteSpace(content) || System.Text.Encoding.UTF8.GetByteCount(content) > 8 * 1024 * 1024))
            throw new ArgumentException("配置内容不能为空或超过 8 MiB", nameof(content));
        var versions = context.RequestServices.GetRequiredService<ConfigVersionService>();
        var lifecycle = context.RequestServices.GetRequiredService<ConfigurationLifecycleService>();
        var jobs = context.RequestServices.GetRequiredService<ManagementJobService>();
        var actor = GetIdentityName(context);
        var job = jobs.Enqueue(versionId == null ? "configuration-publish" : "configuration-rollback", actor, async (log, token) =>
        {
            var before = lifecycle.Capture(root);
            var version = versionId == null
                ? await versions.WriteDraftAsync(content!, "save-and-reload", token, actor)
                : await versions.RollbackAsync(versionId, token, actor) ?? throw new InvalidDataException("配置版本不存在");
            // Once a version is written, validation or rollback must finish even after cancellation.
            try
            {
                var impact = lifecycle.ReloadAndValidate(root, before);
                log($"配置已发布，版本: {version.Id}");
                if (impact.RequiresRestart) log("以下配置需重启后生效: " + string.Join(", ", impact.RestartRequired));
            }
            catch
            {
                if (version.PreviousVersionId != null)
                    await versions.RollbackAsync(version.PreviousVersionId, CancellationToken.None, actor);
                else File.Delete(version.TargetPath);
                root.Reload();
                log("发布校验失败，已恢复发布前配置");
                throw;
            }
        }, TimeSpan.FromMinutes(5), versionId == null ? [] : [versionId]);
        context.RequestServices.GetRequiredService<IAuditLogService>().Log(actor,
            $"创建配置变更任务: {job.Id}", context.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Accepted($"/api/v1/tasks/{job.Id}", new ManagementJobResponse(true, job));
    }
}
