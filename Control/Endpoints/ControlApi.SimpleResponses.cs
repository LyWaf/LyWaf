using System.Diagnostics;
using System.Text;
using LyWaf.Services.ABTest;
using LyWaf.Services.AccessControl;
using LyWaf.Services.Captcha;
using LyWaf.Services.Protect;
using LyWaf.Services.SpeedLimit;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafInfo;
using LyWaf.Config;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Packaging;
using LyWaf.Services.Auth;
using LyWaf.Services.WafRule;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Acme;
using LyWaf.Shared;
using System.Security.Cryptography;
using LyWaf.Utils;
using LyWaf.Services.Param;
using LyWaf.Services.ProxyServer;
using LyWaf.Services.Configuration;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.RuntimeState;
using LyWaf.Services;
using LyWaf.Policy;
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private sealed record SimpleResponseListItem(string ItemId, string Body, string ContentType, int StatusCode,
        string Charset, bool ShowReq, Dictionary<string, string> Headers, string Source);
    private static void MapSimpleResponseEndpoints(WebApplication app, int controlPort)
    {
        // =============== 简单响应配置 API ===============

        // 获取所有简单响应配置
        app.MapGet("/api/simpleres", (HttpContext ctx, IConfiguration config) =>
        {
            var srSection = config.GetSection("SimpleRes:Items");
            var items = new List<SimpleResponseListItem>();
            foreach (var itemSection in srSection.GetChildren())
            {
                var itemId = itemSection.Key;
                var body = itemSection["Body"] ?? "";
                var contentType = itemSection["ContentType"] ?? "text/plain";
                var statusCode = int.TryParse(itemSection["StatusCode"], out var sc) ? sc : 200;
                var charset = itemSection["Charset"] ?? "utf-8";
                var showReq = bool.TryParse(itemSection["ShowReq"], out var sr) && sr;

                var headers = new Dictionary<string, string>();
                var headersSection = itemSection.GetSection("Headers");
                foreach (var h in headersSection.GetChildren())
                {
                    if (h.Value != null) headers[h.Key] = h.Value;
                }

                var source = DetectConfigSource(config, $"SimpleRes:Items:{itemId}");
                items.Add(new(itemId, body, contentType, statusCode, charset, showReq, headers,
                    source == ConfigSource.PatchConfig ? "patch" : "original"));
            }
            return Results.Ok(ManagementLists.From(items).Sort("itemId", item => item.ItemId)
                .Sort("statusCode", item => item.StatusCode).Sort("contentType", item => item.ContentType)
                .Search(item => item.ItemId + " " + item.ContentType).Read(ctx.Request.Query, "itemId"));
        }).Produces<ManagementPage<SimpleResponseListItem>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 新增或更新简单响应配置（通过补丁），存在则自动更新
        app.MapPost("/api/simpleres/add", async (HttpContext ctx, IConfiguration config, AddSimpleResRequest request) =>
        {

            if (request == null || string.IsNullOrEmpty(request.ItemId))
            {
                return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
            }

            return await ModifySimpleResPatch(config, simpleres =>
            {
                Dictionary<string, object> itemPatch;
                // 已存在于补丁中 → 在原有基础上更新
                if (simpleres.TryGetValue(request.ItemId, out var existObj))
                {
                    if (existObj is System.Text.Json.JsonElement je)
                        itemPatch = JsonElementToDict(je);
                    else if (existObj is Dictionary<string, object> dict)
                        itemPatch = dict;
                    else
                        itemPatch = new Dictionary<string, object>();
                }
                // 已存在于原始配置 → 快照到补丁再更新
                else if (config.GetSection($"SimpleRes:Items:{request.ItemId}").Exists())
                {
                    itemPatch = new Dictionary<string, object>();
                    var section = config.GetSection($"SimpleRes:Items:{request.ItemId}");
                    var body = section["Body"]; if (body != null) itemPatch["Body"] = body;
                    var ct = section["ContentType"]; if (ct != null) itemPatch["ContentType"] = ct;
                    var sc = section["StatusCode"]; if (sc != null && int.TryParse(sc, out var scv)) itemPatch["StatusCode"] = scv;
                    var cs = section["Charset"]; if (cs != null) itemPatch["Charset"] = cs;
                    var srq = section["ShowReq"]; if (srq != null) itemPatch["ShowReq"] = bool.TryParse(srq, out var srv) && srv;

                    var headersSection = section.GetSection("Headers");
                    var hDict = new Dictionary<string, object>();
                    foreach (var h in headersSection.GetChildren())
                    {
                        if (h.Value != null) hDict[h.Key] = h.Value;
                    }
                    if (hDict.Count > 0) itemPatch["Headers"] = hDict;
                }
                // 全新项
                else
                {
                    itemPatch = new Dictionary<string, object>();
                }

                // 应用字段
                if (request.Body != null) itemPatch["Body"] = request.Body;
                if (request.ContentType != null) itemPatch["ContentType"] = request.ContentType;
                if (request.StatusCode.HasValue) itemPatch["StatusCode"] = request.StatusCode.Value;
                if (request.Charset != null) itemPatch["Charset"] = request.Charset;
                if (request.ShowReq.HasValue) itemPatch["ShowReq"] = request.ShowReq.Value;
                if (request.Headers != null)
                {
                    if (request.Headers.Count > 0)
                        itemPatch["Headers"] = request.Headers.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                    else
                        itemPatch.Remove("Headers");
                }

                simpleres[request.ItemId] = itemPatch;
                return null;
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 兼容旧接口：/api/simpleres/update 转发到 /api/simpleres/add 同一逻辑
        app.MapPost("/api/simpleres/update", async (HttpContext ctx, IConfiguration config, AddSimpleResRequest request) =>
        {

            if (request == null || string.IsNullOrEmpty(request.ItemId))
            {
                return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
            }

            // 更新接口要求项必须已存在
            var existing = config.GetSection($"SimpleRes:Items:{request.ItemId}");
            if (!existing.Exists())
            {
                return Results.Json(new { success = false, message = $"简单响应 {request.ItemId} 不存在" }, statusCode: 400);
            }

            return await ModifySimpleResPatch(config, simpleres =>
            {
                Dictionary<string, object> itemPatch;
                if (simpleres.TryGetValue(request.ItemId, out var existObj))
                {
                    if (existObj is System.Text.Json.JsonElement je)
                        itemPatch = JsonElementToDict(je);
                    else if (existObj is Dictionary<string, object> dict)
                        itemPatch = dict;
                    else
                        itemPatch = new Dictionary<string, object>();
                }
                else
                {
                    itemPatch = new Dictionary<string, object>();
                    var section = config.GetSection($"SimpleRes:Items:{request.ItemId}");
                    var body = section["Body"]; if (body != null) itemPatch["Body"] = body;
                    var ct = section["ContentType"]; if (ct != null) itemPatch["ContentType"] = ct;
                    var sc = section["StatusCode"]; if (sc != null && int.TryParse(sc, out var scv)) itemPatch["StatusCode"] = scv;
                    var cs = section["Charset"]; if (cs != null) itemPatch["Charset"] = cs;
                    var srq = section["ShowReq"]; if (srq != null) itemPatch["ShowReq"] = bool.TryParse(srq, out var srv) && srv;

                    var headersSection = section.GetSection("Headers");
                    var hDict = new Dictionary<string, object>();
                    foreach (var h in headersSection.GetChildren())
                    {
                        if (h.Value != null) hDict[h.Key] = h.Value;
                    }
                    if (hDict.Count > 0) itemPatch["Headers"] = hDict;
                }

                if (request.Body != null) itemPatch["Body"] = request.Body;
                if (request.ContentType != null) itemPatch["ContentType"] = request.ContentType;
                if (request.StatusCode.HasValue) itemPatch["StatusCode"] = request.StatusCode.Value;
                if (request.Charset != null) itemPatch["Charset"] = request.Charset;
                if (request.ShowReq.HasValue) itemPatch["ShowReq"] = request.ShowReq.Value;
                if (request.Headers != null)
                {
                    if (request.Headers.Count > 0)
                        itemPatch["Headers"] = request.Headers.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                    else
                        itemPatch.Remove("Headers");
                }

                simpleres[request.ItemId] = itemPatch;
                return null;
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 删除简单响应（仅补丁中新增的）
        app.MapPost("/api/simpleres/remove", async (HttpContext ctx, IConfiguration config, RemoveSimpleResRequest request) =>
        {

            if (request == null || string.IsNullOrEmpty(request.ItemId))
            {
                return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
            }

            var source = DetectConfigSource(config, $"SimpleRes:Items:{request.ItemId}");
            if (source != ConfigSource.PatchConfig)
            {
                return Results.Json(new { success = false, message = $"简单响应 {request.ItemId} 来自默认配置，不能删除" }, statusCode: 400);
            }

            return await ModifySimpleResPatch(config, simpleres =>
            {
                if (!simpleres.Remove(request.ItemId))
                    return $"补丁中不存在简单响应 {request.ItemId}";
                return null;
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 删除所有简单响应补丁
        app.MapPost("/api/simpleres/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            return await ModifySimpleResPatch(config, simpleres =>
            {
                simpleres.Clear();
                return null;
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
