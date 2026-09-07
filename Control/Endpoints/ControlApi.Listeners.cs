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
    private static void MapListenerEndpoints(WebApplication app, int controlPort, WafInfoOptions wafInfos)
    {
        // =============== 监听端口管理 ===============

        // 添加监听端口
        app.MapPost("/api/listen/add", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);

                var host = request.TryGetProperty("host", out var hostEl) ? hostEl.GetString() ?? "0.0.0.0" : "0.0.0.0";
                var port = request.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
                var isHttps = request.TryGetProperty("isHttps", out var httpsEl) && httpsEl.GetBoolean();
                int? autoHttpsPort = request.TryGetProperty("autoHttpsPort", out var ahpEl) && ahpEl.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? ahpEl.GetInt32() : null;

                if (port <= 0 || port > 65535)
                {
                    return Results.Json(new { success = false, message = "端口号无效（1-65535）" }, statusCode: 400);
                }

                // 检查端口是否已存在
                if (wafInfos.Listens.Any(l => l.Port == port && l.Host == host))
                {
                    return Results.Json(new { success = false, message = $"监听 {host}:{port} 已存在" }, statusCode: 400);
                }

                // 计算下一个索引
                var nextIndex = wafInfos.Listens.Count;

                return await ModifyListenPatch(config, listens =>
                {
                    // 检查补丁中是否已有更大的索引
                    var maxIdx = nextIndex - 1;
                    foreach (var key in listens.Keys)
                    {
                        if (int.TryParse(key, out var idx) && idx > maxIdx) maxIdx = idx;
                    }
                    var newIdx = (maxIdx + 1).ToString();

                    var listenData = new Dictionary<string, object>
                    {
                        ["Host"] = host,
                        ["Port"] = port,
                        ["IsHttps"] = isHttps
                    };
                    if (autoHttpsPort.HasValue)
                    {
                        listenData["AutoHttpsPort"] = autoHttpsPort.Value;
                    }
                    listens[newIdx] = listenData;
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加监听端口失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除监听端口（仅限补丁添加的）
        app.MapPost("/api/listen/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                var request = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);

                var port = request.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
                var host = request.TryGetProperty("host", out var hostEl) ? hostEl.GetString() ?? "" : "";

                if (port <= 0)
                {
                    return Results.Json(new { success = false, message = "端口号无效" }, statusCode: 400);
                }

                return await ModifyListenPatch(config, listens =>
                {
                    string? removeKey = null;
                    foreach (var (key, val) in listens)
                    {
                        Dictionary<string, object>? listenDict = null;
                        if (val is System.Text.Json.JsonElement je)
                            listenDict = JsonElementToDict(je);
                        else if (val is Dictionary<string, object> d)
                            listenDict = d;

                        if (listenDict == null) continue;

                        var lPort = listenDict.TryGetValue("Port", out var pObj) ? Convert.ToInt32(pObj) : 0;
                        var lHost = listenDict.TryGetValue("Host", out var hObj) ? hObj?.ToString() ?? "" : "";

                        if (lPort == port && (string.IsNullOrEmpty(host) || lHost == host))
                        {
                            removeKey = key;
                            break;
                        }
                    }

                    if (removeKey == null)
                    {
                        return $"补丁中不存在监听 {host}:{port}，原始配置中的监听不能在此删除";
                    }

                    listens.Remove(removeKey);
                    return null;
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"删除监听端口失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

    }
}
