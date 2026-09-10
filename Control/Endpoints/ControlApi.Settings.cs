using System.Collections.Concurrent;
using LyWaf.Config;
using LyWaf.Plugins.Packaging;
using LyWaf.Services.Acme;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Auth;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.Configuration;
using LyWaf.Services.Param;
using LyWaf.Services.ProxyServer;
using LyWaf.Shared;
using LyWaf.Utils;

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LyWaf.Middleware;
using LyWaf.Plugins;
using LyWaf.Plugins.Core;
using LyWaf.Services.WafInfo;
using LyWaf.Services;
using LyWaf.Services.Metrics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record CertificateUploadedResponse(bool Success, string Message, string PemFile, string KeyFile);
    private sealed record CertificateDeletedResponse(bool Success, string Message);
    private sealed record ConsoleListenView(string Host, int Port);
    private sealed record ConsoleSettingsResponse(bool Success, string Username, DateTime? LastLoginAt, ConsoleListenView ControlListen);
    private sealed record CertificateListItem(string Domain, string Type, string Issuer, string NotAfter, string PemFile, DateTimeOffset? ExpiresAt);

    private static string ReadCertificatePem(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("证书文件超过 1 MiB");
        return File.ReadAllText(path);
    }
    private static void MapSettingsEndpoints(WebApplication app, int controlPort, WafInfoOptions wafInfos)
    {
        // =============== 通用设置 — 证书管理 ===============

        app.MapGet("/api/settings/certs", (HttpContext ctx, IConfiguration config) =>
        {
            // ACME 证书目录
            var acmeOpts = new AcmeOptions();
            config.GetSection("Acme").Bind(acmeOpts);
            var acmeCertDir = Path.GetFullPath(acmeOpts.CertificatePath);

            var certList = new List<CertificateListItem>();
            foreach (var c in wafInfos.Certs)
            {
                var domain = c.Host;
                var pemFile = c.PemFile;
                var certType = "uploaded";
                var issuer = "";
                var notAfter = "";
                DateTimeOffset? expiresAt = null;

                // 判断类型：PEM 路径在 ACME 目录下 → acme
                try
                {
                    var fullPemPath = Path.GetFullPath(pemFile);
                    if (fullPemPath.StartsWith(Path.TrimEndingDirectorySeparator(acmeCertDir) + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        certType = "acme";
                    }
                }
                catch { }

                // 解析证书详细信息
                try
                {
                    if (File.Exists(pemFile))
                    {
                        var pemText = ReadCertificatePem(pemFile);
                        using var cert = X509Certificate2.CreateFromPem(pemText);
                        issuer = cert.Issuer;
                        notAfter = cert.NotAfter.ToString("yyyy-MM-dd");
                        expiresAt = cert.NotAfter.ToUniversalTime();
                    }
                }
                catch { }

                certList.Add(new(domain, certType, issuer, notAfter, Path.GetFileName(pemFile), expiresAt));
            }

            // 额外扫描 certs 目录中 ACME 签发但不在 wafInfos.Certs 中的证书
            var certsDir = acmeCertDir;
            if (Directory.Exists(certsDir))
            {
                var existingPemFiles = new HashSet<string>(
                    certList.Select(c => c.PemFile),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var pemFile in Directory.GetFiles(certsDir, "*.pem"))
                {
                    var fileName = Path.GetFileName(pemFile);
                    if (existingPemFiles.Contains(fileName)) continue;
                    if (fileName == "account.pem") continue;

                    var keyFile = Path.ChangeExtension(pemFile, ".key");
                    if (!File.Exists(keyFile)) continue;

                    try
                    {
                        var pemText = ReadCertificatePem(pemFile);
                        using var cert = X509Certificate2.CreateFromPem(pemText);

                        // 从文件名反推域名
                        var domainName = Path.GetFileNameWithoutExtension(fileName)
                            .Replace("_wildcard_", "*");

                        certList.Add(new(domainName, "acme", cert.Issuer, cert.NotAfter.ToString("yyyy-MM-dd"), fileName, cert.NotAfter.ToUniversalTime()));
                    }
                    catch { }
                }
            }

            return Results.Ok(ManagementLists.From(certList).Sort("domain", item => item.Domain).Sort("type", item => item.Type)
                .Sort("expiresAt", item => item.ExpiresAt).Timestamp(item => item.ExpiresAt)
                .Search(item => $"{item.Domain} {item.Type} {item.Issuer} {item.PemFile}").Read(ctx.Request.Query, "domain"));
        }).Produces<ManagementPage<CertificateListItem>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/settings/certs/upload", async (HttpContext ctx, CertUploadRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.Domain)
                || string.IsNullOrWhiteSpace(request.PemContent) || string.IsNullOrWhiteSpace(request.KeyContent))
            {
                return Results.Json(new { success = false, message = "域名、证书内容和密钥内容不能为空" }, statusCode: 400);
            }

            // 验证 PEM 格式
            try
            {
                using var _ = X509Certificate2.CreateFromPem(request.PemContent, request.KeyContent);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"证书格式无效: {ex.Message}" }, statusCode: 400);
            }

            // 保存文件
            var certsDir = Path.Combine(Directory.GetCurrentDirectory(), "certs");
            if (!Directory.Exists(certsDir)) Directory.CreateDirectory(certsDir);

            var safeDomain = request.Domain.Replace("*", "_wildcard_").Replace(":", "_");
            var pemPath = Path.Combine(certsDir, $"{safeDomain}.pem");
            var keyPath = Path.Combine(certsDir, $"{safeDomain}.key");

            await File.WriteAllTextAsync(pemPath, request.PemContent);
            await File.WriteAllTextAsync(keyPath, request.KeyContent);

            // 记审计日志
            var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            var username = ctx.Items["Username"]?.ToString() ?? "unknown";
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            auditLog.Log(username, $"上传证书: {request.Domain}", clientIp);

            return Results.Json(new { success = true, message = $"证书已保存，需要重载配置后生效", pemFile = $"{safeDomain}.pem", keyFile = $"{safeDomain}.key" });
        }).Produces<CertificateUploadedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/settings/certs/delete", (HttpContext ctx, CertDeleteRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.PemFile))
            {
                return Results.Json(new { success = false, message = "证书文件名不能为空" }, statusCode: 400);
            }

            // 查找对应证书（先查 wafInfos，再查 certs 目录）
            var cert = wafInfos.Certs.FirstOrDefault(c => Path.GetFileName(c.PemFile) == request.PemFile);
            var domainForLog = "";
            if (cert != null)
            {
                domainForLog = cert.Host;
                if (File.Exists(cert.PemFile)) File.Delete(cert.PemFile);
                if (!string.IsNullOrEmpty(cert.KeyFile) && File.Exists(cert.KeyFile)) File.Delete(cert.KeyFile);
            }
            else
            {
                // 尝试从 certs 目录直接删除（ACME 签发的独立证书）
                var pemPath = Path.Combine("certs", request.PemFile);
                var keyPath = Path.ChangeExtension(pemPath, ".key");
                if (!File.Exists(pemPath))
                    return Results.Json(new { success = false, message = "未找到该证书" }, statusCode: 404);

                domainForLog = Path.GetFileNameWithoutExtension(request.PemFile).Replace("_wildcard_", "*");
                File.Delete(pemPath);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }

            // 记审计日志
            var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
            var username = ctx.Items["Username"]?.ToString() ?? "unknown";
            var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            auditLog.Log(username, $"删除证书: {domainForLog} ({request.PemFile})", clientIp);

            return Results.Json(new { success = true, message = "证书已删除，需要重载配置后生效" });
        }).Produces<CertificateDeletedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // ---- ACME 手动申请/续期 ----

        app.MapGet("/api/settings/acme-email", (IAcmeService acmeService) =>
        {
            return TypedResults.Ok(new { success = true, email = acmeService.GetSavedEmail() ?? "" });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/settings/certs/status", (IAcmeService acmeService) => TypedResults.Ok(new
        {
            certificates = acmeService.GetCertificateStatuses(),
            accounts = acmeService.GetAccountStatuses()
        })).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/settings/certs/acme-apply", (HttpContext ctx, IAcmeService acmeService, AcmeApplyRequest request) =>
        {
            return EnqueueCertificateTask(new(request.Domain, request.Email), ctx, acmeService,
                ctx.RequestServices.GetRequiredService<LyWaf.Services.ManagementTasks.ManagementJobService>(),
                ctx.RequestServices.GetRequiredService<IAuditLogService>());
        }).Produces<LyWaf.Services.ManagementTasks.ManagementJobResponse>(202).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/settings/certs/acme-renew", (HttpContext ctx, IAcmeService acmeService, AcmeRenewRequest request) =>
        {
            var email = !string.IsNullOrWhiteSpace(request.Email) ? request.Email : acmeService.GetSavedEmail();
            if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("请先配置联系邮箱", nameof(request.Email));
            return EnqueueCertificateTask(new(request.Domain, email), ctx, acmeService,
                ctx.RequestServices.GetRequiredService<LyWaf.Services.ManagementTasks.ManagementJobService>(),
                ctx.RequestServices.GetRequiredService<IAuditLogService>());
        }).Produces<LyWaf.Services.ManagementTasks.ManagementJobResponse>(202).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== 通用设置 — 控制台信息 ===============

        app.MapGet("/api/settings/console", (HttpContext ctx) =>
        {
            var authService = ctx.RequestServices.GetRequiredService<IAuthService>();
            var cl = wafInfos.GetControlListen();

            return Results.Json(new
            {
                success = true,
                username = authService.GetCurrentUsername(),
                lastLoginAt = authService.GetLastLoginAt(),
                controlListen = new { host = cl.Host, port = cl.Port }
            });
        }).Produces<ConsoleSettingsResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== 通用设置 — 审计日志 ===============

        app.MapGet("/api/settings/audit-logs", (HttpContext ctx, IAuditLogService auditLog) =>
            TypedResults.Ok(auditLog.Query(ManagementListQuery.Parse(ctx.Request.Query, AuditLogService.SortFields.Keys, "timestamp", true))))
            .RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== 通用设置 — 运行日志 ===============

        app.MapGet("/api/settings/runtime-logs", (HttpContext ctx, LyWaf.Services.DuckDb.RuntimeLogService runtimeLog) =>
            TypedResults.Ok(runtimeLog.Query(ManagementListQuery.Parse(ctx.Request.Query,
                LyWaf.Services.DuckDb.RuntimeLogService.SortFields.Keys, "startTime", true))))
            .RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
