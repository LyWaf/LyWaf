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
    private static void MapPatchEndpoints(WebApplication app, int controlPort)
    {
        // =============== 清空所有补丁 ===============

        app.MapPost("/api/patch/clear", async (HttpContext ctx, IConfiguration config) =>
        {
                var patchPath = LbPatchConfig.GetPatchFilePath();
                var versionService = ctx.RequestServices.GetRequiredService<ConfigVersionService>();
                var version = await versionService.WritePatchAsync(patchPath, "{}", "clear-all-patches", ctx.RequestAborted);

                if (config is IConfigurationRoot configRoot)
                {
                    var lifecycle = ctx.RequestServices.GetRequiredService<ConfigurationLifecycleService>();
                    await ApplyVersionReloadOrRollback(version, configRoot, lifecycle.Capture(configRoot));
                }

                return TypedResults.Ok(new { success = true, message = "所有补丁已清除" });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
