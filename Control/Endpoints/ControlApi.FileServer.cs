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
    private sealed record FileServerListItem(string ItemId, string Prefix, string BasePath, bool Browse,
        bool PreCompressed, long MaxFileSize, List<string> TryFiles, List<string> DefaultFiles, string Source);
    private static void MapFileServerEndpoints(WebApplication app, int controlPort)
    {
        // =============== 文件服务配置 API ===============

        // 获取所有文件服务配置
        app.MapGet("/api/fileserver", (HttpContext ctx, IConfiguration config) =>
        {
            var fsSection = config.GetSection("FileServer:Items");
            var items = new List<FileServerListItem>();
            foreach (var itemSection in fsSection.GetChildren())
            {
                var itemId = itemSection.Key;
                var prefix = itemSection["Prefix"] ?? "/";
                var basePath = itemSection["BasePath"] ?? "wwwroot";
                var browse = bool.TryParse(itemSection["Browse"], out var b) && b;
                var preCompressed = bool.TryParse(itemSection["PreCompressed"], out var pc) && pc;
                var maxFileSize = long.TryParse(itemSection["MaxFileSize"], out var mfs) ? mfs : 100L * 1024 * 1024;

                var tryFiles = new List<string>();
                var tryFilesSection = itemSection.GetSection("TryFiles");
                foreach (var tf in tryFilesSection.GetChildren())
                {
                    if (tf.Value != null) tryFiles.Add(tf.Value);
                }

                var defaultFiles = new List<string>();
                var defaultSection = itemSection.GetSection("Default");
                foreach (var df in defaultSection.GetChildren())
                {
                    if (df.Value != null) defaultFiles.Add(df.Value);
                }

                var source = DetectConfigSource(config, $"FileServer:Items:{itemId}");
                items.Add(new(itemId, prefix, basePath, browse, preCompressed, maxFileSize, tryFiles, defaultFiles,
                    source == ConfigSource.PatchConfig ? "patch" : "original"));
            }
            return Results.Ok(ManagementLists.From(items).Sort("itemId", item => item.ItemId)
                .Sort("prefix", item => item.Prefix).Sort("maxFileSize", item => item.MaxFileSize)
                .Search(item => item.ItemId + " " + item.Prefix + " " + item.BasePath).Read(ctx.Request.Query, "itemId"));
        }).Produces<ManagementPage<FileServerListItem>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 新增文件服务配置（通过补丁）
        app.MapPost("/api/fileserver/add", async (HttpContext ctx, IConfiguration config, AddFileServerRequest request) =>
        {

            if (request == null || string.IsNullOrEmpty(request.ItemId))
            {
                return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
            }

            var existing = config.GetSection($"FileServer:Items:{request.ItemId}");
            if (existing.Exists())
            {
                return Results.Json(new { success = false, message = $"文件服务 {request.ItemId} 已存在" }, statusCode: 400);
            }

            return await ModifyFileServerPatch(config, fileservers =>
            {
                if (fileservers.ContainsKey(request.ItemId))
                    return $"文件服务 {request.ItemId} 已存在于补丁中";

                var item = new Dictionary<string, object>();
                if (request.Prefix != null) item["Prefix"] = request.Prefix;
                if (request.BasePath != null) item["BasePath"] = request.BasePath;
                if (request.Browse.HasValue) item["Browse"] = request.Browse.Value;
                if (request.PreCompressed.HasValue) item["PreCompressed"] = request.PreCompressed.Value;
                if (request.MaxFileSize.HasValue) item["MaxFileSize"] = request.MaxFileSize.Value;
                if (request.TryFiles != null && request.TryFiles.Count > 0)
                    item["TryFiles"] = request.TryFiles.ToList<object>();
                if (request.DefaultFiles != null && request.DefaultFiles.Count > 0)
                    item["Default"] = request.DefaultFiles.ToList<object>();

                fileservers[request.ItemId] = item;
                return null;
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 更新文件服务配置（通过补丁）
        app.MapPost("/api/fileserver/update", async (HttpContext ctx, IConfiguration config, UpdateFileServerRequest request) =>
        {

            if (request == null || string.IsNullOrEmpty(request.ItemId))
            {
                return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
            }

            var itemSection = config.GetSection($"FileServer:Items:{request.ItemId}");
            if (!itemSection.Exists())
            {
                return Results.Json(new { success = false, message = $"文件服务 {request.ItemId} 不存在" }, statusCode: 400);
            }

            return await ModifyFileServerPatch(config, fileservers =>
            {
                Dictionary<string, object> itemPatch;
                if (fileservers.TryGetValue(request.ItemId, out var existObj))
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
                    // 快照当前配置到补丁
                    itemPatch = new Dictionary<string, object>();
                    var section = config.GetSection($"FileServer:Items:{request.ItemId}");
                    var prefix = section["Prefix"]; if (prefix != null) itemPatch["Prefix"] = prefix;
                    var basePath = section["BasePath"]; if (basePath != null) itemPatch["BasePath"] = basePath;
                    var browse = section["Browse"]; if (browse != null) itemPatch["Browse"] = bool.TryParse(browse, out var bv) && bv;
                    var preCompressed = section["PreCompressed"]; if (preCompressed != null) itemPatch["PreCompressed"] = bool.TryParse(preCompressed, out var pcv) && pcv;
                    var maxFileSize = section["MaxFileSize"]; if (maxFileSize != null && long.TryParse(maxFileSize, out var mfv)) itemPatch["MaxFileSize"] = mfv;

                    var tryFilesSection = section.GetSection("TryFiles");
                    var tfList = tryFilesSection.GetChildren().Select(c => c.Value).Where(v => v != null).Cast<object>().ToList();
                    if (tfList.Count > 0) itemPatch["TryFiles"] = tfList;

                    var defaultSection = section.GetSection("Default");
                    var dfList = defaultSection.GetChildren().Select(c => c.Value).Where(v => v != null).Cast<object>().ToList();
                    if (dfList.Count > 0) itemPatch["Default"] = dfList;
                }

                // 应用更新
                if (request.Prefix != null) itemPatch["Prefix"] = request.Prefix;
                if (request.BasePath != null) itemPatch["BasePath"] = request.BasePath;
                if (request.Browse.HasValue) itemPatch["Browse"] = request.Browse.Value;
                if (request.PreCompressed.HasValue) itemPatch["PreCompressed"] = request.PreCompressed.Value;
                if (request.MaxFileSize.HasValue) itemPatch["MaxFileSize"] = request.MaxFileSize.Value;
                if (request.TryFiles != null)
                {
                    if (request.TryFiles.Count > 0)
                        itemPatch["TryFiles"] = request.TryFiles.ToList<object>();
                    else
                        itemPatch.Remove("TryFiles");
                }
                if (request.DefaultFiles != null)
                {
                    if (request.DefaultFiles.Count > 0)
                        itemPatch["Default"] = request.DefaultFiles.ToList<object>();
                    else
                        itemPatch.Remove("Default");
                }

                fileservers[request.ItemId] = itemPatch;
                return null;
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 删除文件服务（仅补丁中新增的）
        app.MapPost("/api/fileserver/remove", async (HttpContext ctx, IConfiguration config, RemoveFileServerRequest request) =>
        {

            if (request == null || string.IsNullOrEmpty(request.ItemId))
            {
                return Results.Json(new { success = false, message = "ItemId 不能为空" }, statusCode: 400);
            }

            var source = DetectConfigSource(config, $"FileServer:Items:{request.ItemId}");
            if (source != ConfigSource.PatchConfig)
            {
                return Results.Json(new { success = false, message = $"文件服务 {request.ItemId} 来自默认配置，不能删除" }, statusCode: 400);
            }

            return await ModifyFileServerPatch(config, fileservers =>
            {
                if (!fileservers.Remove(request.ItemId))
                    return $"补丁中不存在文件服务 {request.ItemId}";
                return null;
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 删除所有文件服务补丁
        app.MapPost("/api/fileserver/patch/remove", async (HttpContext ctx, IConfiguration config) =>
        {
            return await ModifyFileServerPatch(config, fileservers =>
            {
                fileservers.Clear();
                return null;
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
