using System.Collections.Concurrent;
using LyWaf.Config;
using LyWaf.Plugins.Packaging;
using LyWaf.Services.Acme;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Auth;
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
    private static void MapSettingsEndpoints(WebApplication app, int controlPort, WafInfoOptions wafInfos)
    {
        // =============== 通用设置 — 证书管理 ===============

        app.MapGet("/api/settings/certs", (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                // ACME 证书目录
                var acmeOpts = new AcmeOptions();
                config.GetSection("Acme").Bind(acmeOpts);
                var acmeCertDir = Path.GetFullPath(acmeOpts.CertificatePath);

                var certList = new List<object>();
                foreach (var c in wafInfos.Certs)
                {
                    var domain = c.Host;
                    var pemFile = c.PemFile;
                    var certType = "uploaded";
                    var issuer = "";
                    var notAfter = "";

                    // 判断类型：PEM 路径在 ACME 目录下 → acme
                    try
                    {
                        var fullPemPath = Path.GetFullPath(pemFile);
                        if (fullPemPath.StartsWith(acmeCertDir, StringComparison.OrdinalIgnoreCase))
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
                            var pemText = File.ReadAllText(pemFile);
                            using var cert = X509Certificate2.CreateFromPem(pemText);
                            issuer = cert.Issuer;
                            notAfter = cert.NotAfter.ToString("yyyy-MM-dd");
                        }
                    }
                    catch { }

                    certList.Add(new
                    {
                        domain,
                        type = certType,
                        issuer,
                        notAfter,
                        pemFile = Path.GetFileName(pemFile),
                    });
                }

                // 额外扫描 certs 目录中 ACME 签发但不在 wafInfos.Certs 中的证书
                var certsDir = Path.GetFullPath("certs");
                if (Directory.Exists(certsDir))
                {
                    var existingPemFiles = new HashSet<string>(
                        certList.Select(c => ((dynamic)c).pemFile as string ?? ""),
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
                            var pemText = File.ReadAllText(pemFile);
                            using var cert = X509Certificate2.CreateFromPem(pemText);

                            // 从文件名反推域名
                            var domainName = Path.GetFileNameWithoutExtension(fileName)
                                .Replace("_wildcard_", "*");

                            certList.Add(new
                            {
                                domain = domainName,
                                type = "acme",
                                issuer = cert.Issuer,
                                notAfter = cert.NotAfter.ToString("yyyy-MM-dd"),
                                pemFile = fileName,
                            });
                        }
                        catch { }
                    }
                }

                return Results.Json(new { success = true, certs = certList });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取证书列表失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/settings/certs/upload", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<CertUploadRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"上传证书失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/settings/certs/delete", async (HttpContext ctx) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<CertDeleteRequest>();
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除证书失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // ---- ACME 手动申请/续期 ----

        app.MapGet("/api/settings/acme-email", (IAcmeService acmeService) =>
        {
            return Results.Json(new { success = true, email = acmeService.GetSavedEmail() ?? "" });
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/settings/certs/status", (IAcmeService acmeService) => Results.Ok(new
        {
            certificates = acmeService.GetCertificateStatuses(),
            accounts = acmeService.GetAccountStatuses()
        })).RequireHost($"*:{controlPort}");

        app.MapPost("/api/settings/certs/acme-apply", async (HttpContext ctx, IAcmeService acmeService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AcmeApplyRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Domain) || string.IsNullOrWhiteSpace(request.Email))
                {
                    return Results.Json(new { success = false, errorMessage = "域名和邮箱不能为空" }, statusCode: 400);
                }

                var result = await acmeService.ManualRequestCertificateAsync(request.Domain, request.Email, ctx.RequestAborted);

                // 审计日志
                var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                auditLog.Log(username, $"申请免费证书: {request.Domain} ({(result.Success ? "成功" : "失败")})", clientIp);

                return Results.Json(result);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { success = false, errorMessage = "操作已取消" }, statusCode: 499);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, errorMessage = $"申请证书失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/settings/certs/acme-renew", async (HttpContext ctx, IAcmeService acmeService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AcmeRenewRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Domain))
                {
                    return Results.Json(new { success = false, errorMessage = "域名不能为空" }, statusCode: 400);
                }

                // 优先用请求中的邮箱，否则用已保存的
                var email = !string.IsNullOrWhiteSpace(request.Email) ? request.Email : acmeService.GetSavedEmail();
                if (string.IsNullOrWhiteSpace(email))
                {
                    return Results.Json(new { success = false, errorMessage = "请先配置联系邮箱" }, statusCode: 400);
                }

                var result = await acmeService.ManualRequestCertificateAsync(request.Domain, email, ctx.RequestAborted);

                // 审计日志
                var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                auditLog.Log(username, $"续期证书: {request.Domain} ({(result.Success ? "成功" : "失败")})", clientIp);

                return Results.Json(result);
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { success = false, errorMessage = "操作已取消" }, statusCode: 499);
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, errorMessage = $"续期证书失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 通用设置 — 控制台信息 ===============

        app.MapGet("/api/settings/console", (HttpContext ctx) =>
        {
            try
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
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取控制台信息失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 通用设置 — 审计日志 ===============

        app.MapGet("/api/settings/audit-logs", (HttpContext ctx, int offset = 0, int limit = 50) =>
        {
            try
            {
                var auditLog = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var (items, total) = auditLog.GetLogs(offset, limit);

                return Results.Json(new
                {
                    success = true,
                    items = items.Select(e => new
                    {
                        username = e.Username,
                        action = e.Action,
                        ip = e.Ip,
                        timestamp = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")
                    }),
                    total
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取审计日志失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // =============== 通用设置 — 运行日志 ===============

        app.MapGet("/api/settings/runtime-logs", (HttpContext ctx, int offset = 0, int limit = 20) =>
        {
            try
            {
                var runtimeLog = ctx.RequestServices.GetRequiredService<LyWaf.Services.DuckDb.RuntimeLogService>();
                var (items, total) = runtimeLog.GetLogs(offset, limit);

                return Results.Json(new
                {
                    success = true,
                    items = items.Select(e => new
                    {
                        id = e.Id,
                        startTime = e.StartTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        stopTime = e.StopTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        exitReason = e.ExitReason,
                        durationSeconds = e.DurationSeconds,
                        peakMemoryMb = Math.Round(e.PeakMemoryMb, 1),
                        finalMemoryMb = Math.Round(e.FinalMemoryMb, 1),
                        gcGen0 = e.GcGen0,
                        gcGen1 = e.GcGen1,
                        gcGen2 = e.GcGen2,
                        totalRequests = e.TotalRequests,
                        totalIntercepts = e.TotalIntercepts,
                        dotnetVersion = e.DotnetVersion,
                        osDescription = e.OsDescription,
                        processId = e.ProcessId
                    }),
                    total
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取运行日志失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

    }
}
