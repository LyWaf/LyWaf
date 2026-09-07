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
    private static void MapParameterEndpoints(WebApplication app, int controlPort)
    {
        // =============== 参数配置 API ===============

        app.MapGet("/api/param", (HttpContext ctx) =>
        {
            var opts = ctx.RequestServices.GetRequiredService<IOptionsMonitor<GlobalOptions>>().CurrentValue;
            return Results.Json(new
            {
                success = true,
                isFirstServer = SharedData.IsFirstServer,
                localAddress = opts.LocalAddress ?? "",
            });
        }).RequireHost($"*:{controlPort}");

        app.MapPost("/api/param", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<ParamUpdateRequest>();
                if (body == null)
                    return Results.Json(new { success = false, message = "无效的请求体" }, statusCode: 400);

                var opts = ctx.RequestServices.GetRequiredService<IOptionsMonitor<GlobalOptions>>().CurrentValue;

                if (body.IsFirstServer.HasValue)
                {
                    SharedData.IsFirstServer = body.IsFirstServer.Value;
                }

                if (body.LocalAddress != null)
                {
                    opts.LocalAddress = string.IsNullOrWhiteSpace(body.LocalAddress) ? null : body.LocalAddress.Trim();
                }

                return Results.Json(new
                {
                    success = true,
                    isFirstServer = SharedData.IsFirstServer,
                    localAddress = opts.LocalAddress ?? "",
                    message = "参数配置已更新"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"更新失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        app.MapGet("/api/param/env", (HttpContext ctx) =>
        {
            var envVars = Environment.GetEnvironmentVariables();
            var list = new List<object>();
            foreach (System.Collections.DictionaryEntry entry in envVars)
            {
                list.Add(new { key = entry.Key?.ToString(), value = entry.Value?.ToString() });
            }
            list.Sort((a, b) => string.Compare(
                ((dynamic)a).key, ((dynamic)b).key, StringComparison.OrdinalIgnoreCase));
            return Results.Json(new { success = true, items = list, total = list.Count });
        }).RequireHost($"*:{controlPort}");

    }
}
