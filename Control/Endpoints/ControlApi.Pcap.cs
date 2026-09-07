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
    private static void MapPcapEndpoints(WebApplication app, int controlPort)
    {
        // =============== 抓包管理 API ===============

        // 设置 CRL 分发点 URL（嵌入到 Pcap 签发的证书中，供客户端验证吊销状态）
        // CRL 由代理端口直接提供（ProxyHandler.ServeCrlAsync），使用本机网络 IP
        {
            var pcapProvider = app.Services.GetRequiredService<PcapCertProvider>();
            var proxyOptions = app.Services.GetRequiredService<IOptionsMonitor<ProxyServerOptions>>().CurrentValue;

            // 找到第一个启用了 Pcap 的代理端口
            var pcapPort = proxyOptions.Ports
                .Where(p => p.Value.EnablePcap && (p.Value.EnableHttp || p.Value.EnableHttps))
                .Select(p => {
                    var lastColon = p.Key.LastIndexOf(':');
                    return lastColon > 0 && int.TryParse(p.Key[(lastColon + 1)..], out var port) ? port
                         : int.TryParse(p.Key, out var port2) ? port2 : 0;
                })
                .FirstOrDefault(p => p > 0);

            // 没有 Pcap 端口就用 control 端口作为回退
            if (pcapPort == 0) pcapPort = controlPort;

            var globalOpts = app.Services.GetRequiredService<IOptionsMonitor<GlobalOptions>>().CurrentValue;
            var localIp = GetLocalNetworkIp(globalOpts);
            pcapProvider.CrlUrl = $"http://{localIp}:{pcapPort}{ProxyHandler.CrlPath}";
        }

        // CRL 下载（控制端口回退，不需要认证）
        app.MapGet("/api/pcap/ca.crl", (HttpContext ctx) =>
        {
            var pcapCertProvider = ctx.RequestServices.GetRequiredService<PcapCertProvider>();
            var crlPath = pcapCertProvider.CaCrlPath;
            if (!File.Exists(crlPath))
                return Results.NotFound();

            return Results.File(crlPath, "application/pkix-crl", "proxy-ca.crl");
        }).RequireHost($"*:{controlPort}");

        // 获取抓包状态
        app.MapGet("/api/pcap/status", (HttpContext ctx) =>
        {
            var proxyOptions = ctx.RequestServices.GetRequiredService<IOptionsMonitor<ProxyServerOptions>>().CurrentValue;
            var pcapCertProvider = ctx.RequestServices.GetRequiredService<PcapCertProvider>();
            var globalOpts = ctx.RequestServices.GetRequiredService<IOptionsMonitor<GlobalOptions>>().CurrentValue;

            var ports = proxyOptions.Ports.Select(p => new
            {
                port = p.Key,
                enablePcap = p.Value.EnablePcap,
                enableHttp = p.Value.EnableHttp,
                enableHttps = p.Value.EnableHttps,
                enableSocks5 = p.Value.EnableSocks5,
                pcapHosts = p.Value.PcapHosts,
            }).ToList();

            // 构建 CA 证书通过代理端口的下载链接
            string? certDownloadUrl = null;
            var pcapPort = proxyOptions.Ports
                .Where(p => p.Value.EnablePcap && (p.Value.EnableHttp || p.Value.EnableHttps))
                .Select(p => {
                    var lastColon = p.Key.LastIndexOf(':');
                    return lastColon > 0 && int.TryParse(p.Key[(lastColon + 1)..], out var port) ? port
                         : int.TryParse(p.Key, out var port2) ? port2 : 0;
                })
                .FirstOrDefault(p => p > 0);
            if (pcapPort > 0)
            {
                var localIp = GetLocalNetworkIp(globalOpts);
                certDownloadUrl = $"http://{localIp}:{pcapPort}{ProxyHandler.CaCertPath}";
            }

            return Results.Json(new
            {
                success = true,
                proxyEnabled = proxyOptions.Enabled,
                caCertExists = File.Exists(pcapCertProvider.CaCertPath),
                certDownloadUrl,
                ports,
            });
        }).RequireHost($"*:{controlPort}");

        // 切换抓包开关
        app.MapPost("/api/pcap/toggle", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<PcapToggleRequest>();
                if (body == null || string.IsNullOrEmpty(body.PortKey))
                    return Results.Json(new { success = false, message = "无效的请求" }, statusCode: 400);

                var proxyOptionsMonitor = ctx.RequestServices.GetRequiredService<IOptionsMonitor<ProxyServerOptions>>();
                var options = proxyOptionsMonitor.CurrentValue;
                if (!options.Ports.TryGetValue(body.PortKey, out var portConfig))
                    return Results.Json(new { success = false, message = $"端口 {body.PortKey} 不存在" }, statusCode: 404);

                portConfig.EnablePcap = body.Enabled;

                // 更新 PcapHosts（如果提供）
                if (body.PcapHosts != null)
                {
                    portConfig.PcapHosts = body.PcapHosts
                        .Where(h => !string.IsNullOrWhiteSpace(h))
                        .Select(h => h.Trim())
                        .ToList();
                    if (portConfig.PcapHosts.Count == 0)
                        portConfig.PcapHosts = ["*"];
                }

                // 如果启用 Pcap 且 CA 证书未初始化，初始化证书
                if (body.Enabled)
                {
                    var pcapCertProvider = ctx.RequestServices.GetRequiredService<PcapCertProvider>();
                    pcapCertProvider.Initialize();
                }

                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                audit.Log(ctx.Items["Username"]?.ToString() ?? "unknown",
                    $"切换抓包: 端口 {body.PortKey} → {(body.Enabled ? "启用" : "禁用")}",
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "");

                return Results.Json(new { success = true, enabled = body.Enabled, message = body.Enabled ? "抓包已启用" : "抓包已禁用" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"操作失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 下载 CA 证书
        app.MapGet("/api/pcap/ca-cert", (HttpContext ctx) =>
        {
            var pcapCertProvider = ctx.RequestServices.GetRequiredService<PcapCertProvider>();
            var certPath = pcapCertProvider.CaCertPath;
            if (!File.Exists(certPath))
                return Results.Json(new { success = false, message = "CA 证书不存在，请先启用抓包功能" }, statusCode: 404);

            return Results.File(certPath, "application/x-x509-ca-cert", "proxy-ca.crt");
        }).RequireHost($"*:{controlPort}");

        // 列出抓包日志文件
        app.MapGet("/api/pcap/logs/files", () =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var files = LogUtil.ListLogFiles(dir, "pcap_*.log");
            return Results.Json(new { success = true, files });
        }).RequireHost($"*:{controlPort}");

        // 获取日志文件中所有不重复的 Host
        app.MapGet("/api/pcap/logs/hosts", async (HttpContext ctx) =>
        {
            var fileName = ctx.Request.Query["file"].FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return Results.Json(new { success = false, hosts = Array.Empty<string>() });

            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var filePath = Path.Combine(dir, fileName);
            if (!File.Exists(filePath))
                return Results.Json(new { success = false, hosts = Array.Empty<string>() });

            var hosts = await LogUtil.GetUniqueHostsAsync(filePath);
            return Results.Json(new { success = true, hosts });
        }).RequireHost($"*:{controlPort}");

        // 读取抓包日志条目
        app.MapGet("/api/pcap/logs/entries", async (HttpContext ctx) =>
        {
            var fileName = ctx.Request.Query["file"].FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                return Results.Json(new { success = false, message = "无效的文件名" });

            var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
            var filePath = Path.Combine(dir, fileName);
            if (!File.Exists(filePath))
                return Results.Json(new { success = false, message = $"文件不存在: {fileName}" });

            _ = int.TryParse(ctx.Request.Query["offset"].FirstOrDefault() ?? "0", out var offset);
            _ = int.TryParse(ctx.Request.Query["limit"].FirstOrDefault() ?? "20", out var limit);
            var search = ctx.Request.Query["search"].FirstOrDefault();
            var host = ctx.Request.Query["host"].FirstOrDefault();
            var clientIp = ctx.Request.Query["clientIp"].FirstOrDefault();
            DateTime? startTime = DateTime.TryParse(ctx.Request.Query["startTime"].FirstOrDefault(), out var st3) ? st3 : null;
            DateTime? endTime = DateTime.TryParse(ctx.Request.Query["endTime"].FirstOrDefault(), out var et3) ? et3 : null;

            var (entries, total) = await LogUtil.ParseLogFileAsync(filePath, offset, Math.Clamp(limit, 1, 100), search, startTime, endTime, host, clientIp);
            return Results.Json(new
            {
                success = true,
                entries,
                total,
                offset,
                limit,
                fileName,
            });
        }).RequireHost($"*:{controlPort}");

        // 删除抓包日志文件
        app.MapPost("/api/pcap/logs/delete", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<PcapLogDeleteRequest>();
                if (body == null || string.IsNullOrEmpty(body.File))
                    return Results.Json(new { success = false, message = "无效的请求" }, statusCode: 400);

                var fileName = body.File;
                if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                    return Results.Json(new { success = false, message = "无效的文件名" }, statusCode: 400);

                var dir = Path.Combine(AppContext.BaseDirectory, "logs/request_logger");
                var filePath = Path.Combine(dir, fileName);
                if (!File.Exists(filePath))
                    return Results.Json(new { success = false, message = $"文件不存在: {fileName}" });

                ReverseLogScanner.InvalidateCache(filePath);
                File.Delete(filePath);
                return Results.Json(new { success = true, message = $"已删除: {fileName}" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

    }
}
