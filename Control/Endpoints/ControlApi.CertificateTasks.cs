using System.Net.Mail;
using LyWaf.Services.Acme;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementTasks;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record CertificateTaskRequest(
        [property: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(253)] string Domain,
        [property: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(254), System.ComponentModel.DataAnnotations.EmailAddress] string Email);

    private static void MapCertificateTaskEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/certificate", EnqueueCertificateTask).Produces<ManagementJobResponse>(202);
    }

    private static IResult EnqueueCertificateTask(CertificateTaskRequest request, HttpContext context,
        IAcmeService certificates, ManagementJobService jobs, IAuditLogService audit)
    {
            var domain = request.Domain?.Trim().ToLowerInvariant() ?? "";
            var host = domain.StartsWith("*.", StringComparison.Ordinal) ? domain[2..] : domain;
            if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains(':'))
                throw new ArgumentException("请输入有效的证书域名", nameof(request.Domain));
            var email = request.Email?.Trim() ?? "";
            if (email.Length > 254 || !MailAddress.TryCreate(email, out var address) || address.Address != email)
                throw new ArgumentException("请输入有效的联系邮箱", nameof(request.Email));
            var actor = GetIdentityName(context);
            var job = jobs.Enqueue("certificate-issuance", actor, async (log, token) =>
            {
                log($"开始签发证书: {domain}");
                var result = await certificates.ManualRequestCertificateAsync(domain, email, token);
                if (!result.Success) throw new InvalidDataException(result.ErrorMessage ?? "证书签发失败");
                log($"证书已签发，到期时间: {result.ExpiresAt}");
            }, TimeSpan.FromMinutes(15), [domain]);
            audit.Log(actor, $"创建证书签发任务: {job.Id}, {domain}", context.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Accepted($"/api/v1/tasks/{job.Id}", new { success = true, data = job });
    }
}
