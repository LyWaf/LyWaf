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
    private sealed record PluginPackageResponse(bool Success, string Id, string Version, bool RequiresRestart, string Message);
    private sealed record PluginConfigResponse(bool Success, string PluginId, Dictionary<string, string> Config,
        Dictionary<string, string> Defaults, Dictionary<string, string> Labels, Dictionary<string, string> FieldDefaults,
        IReadOnlyList<PluginConfigurationField> Schema, bool SupportsScopedConfiguration, string Version,
        Dictionary<string, Dictionary<string, string>> Localization);
    private sealed record PluginConfigValidationResponse(bool Success, Dictionary<string, string[]> Errors);

    private static Dictionary<string, string[]> PreparePluginConfiguration(ILyWafPlugin plugin,
        Dictionary<string, string>? body, out Dictionary<string, string> candidate, IConfiguration? config = null)
    {
        candidate = new(StringComparer.OrdinalIgnoreCase);
        if (body == null || plugin.Metadata.DefaultOptions == null)
            return new() { ["$body"] = ["请求体为空或插件未声明通用配置模型"] };
        var options = plugin.Metadata.DefaultOptions;
        var type = options.GetType();
        if (body.Count > 2048) return new() { ["$body"] = ["配置字段最多 2048 项"] };
        var current = new Dictionary<string, string>();
        SerializeOptionsToFlat(options, "", current);
        if (plugin.Metadata.Id == "http-upload" && config != null)
            current["Key"] = config["Plugins:http-upload:Key"] ?? "";
        foreach (var pair in body) candidate[pair.Key] = pair.Value;
        foreach (var key in body.Keys.Where(k => PluginConfigurationSchema.SensitivePath(type, k)))
        {
            if (plugin.Metadata.Id != "http-upload" || !key.Equals("Key", StringComparison.OrdinalIgnoreCase))
                return new() { [key] = ["敏感配置请通过插件专用接口或部署配置修改"] };
            if (!string.IsNullOrEmpty(body[key]) && body[key].Length < 32)
                return new() { [key] = ["新密钥至少需要 32 个字符"] };
        }
        foreach (var pair in current.Where(p => PluginConfigurationSchema.SensitivePath(type, p.Key)))
            if (!candidate.TryGetValue(pair.Key, out var submitted) || string.IsNullOrEmpty(submitted))
                candidate[pair.Key] = pair.Value;
        if (body.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != body.Count)
            return new() { ["$body"] = ["配置字段重复（不区分大小写）"] };
        foreach (var field in PluginConfigurationSchema.Describe(type).Where(f => f.RequiresRestart))
        {
            if (field.Path.Contains('*'))
                return new() { [field.Path] = ["包含需重启的集合配置，请通过部署配置修改并重启"] };
            var before = current.Where(p => p.Key.Equals(field.Path, StringComparison.OrdinalIgnoreCase) || p.Key.StartsWith(field.Path + ":", StringComparison.OrdinalIgnoreCase));
            var after = candidate.Where(p => p.Key.Equals(field.Path, StringComparison.OrdinalIgnoreCase) || p.Key.StartsWith(field.Path + ":", StringComparison.OrdinalIgnoreCase));
            if (!before.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).SequenceEqual(after.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)))
                return new() { [field.Path] = ["此配置需要重启，请通过部署配置修改并重启"] };
        }
        return PluginConfigurationSchema.Validate(type, candidate);
    }
    private sealed record PluginActionView(string Id, string Name, string Type, string? Description);
    private sealed record PluginActionsResponse(bool Success, string PluginId, PluginActionView[] Actions);
    private sealed record PluginListItem(string Id, string Name, string Version, string? Description, string? Author,
        string Priority, string State, bool IsEnabled, bool IsSystem, bool EnabledByDefault, bool HasOptions, List<PluginAction> Actions,
        Dictionary<string, Dictionary<string, string>> Localization);
    private static void MapPluginEndpoints(WebApplication app, int controlPort)
    {
        app.MapGet("/api/plugins/{pluginId}/config/effective", (string pluginId, string? host, string? path,
            HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] PluginScopedConfiguration resolver) =>
        {
            try { return Results.Ok(resolver.Describe(pluginId, host ?? "", path ?? "/")); }
            catch (KeyNotFoundException) { return ManagementApiContract.Error(context, 404, "plugin_scope_unavailable", "插件未支持分层配置"); }
        }).Produces<PluginEffectiveConfiguration>().RequireHost($"*:{controlPort}").RequireManagementPermission();
        app.MapGet("/api/plugins/menus", (IPluginManager manager) =>
        {
            var menus = manager.GetAllPlugins()
                .SelectMany(plugin => PluginMenuCatalog.GetEntries(plugin, manager.GetPluginState(plugin.Metadata.Id)))
                .DistinctBy(item => item.Path)
                .OrderBy(item => item.PluginId).ThenBy(item => item.Id).ToArray();
            return TypedResults.Ok(new PluginMenusResponse(true, menus));
        }).Produces<PluginMenusResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== 插件管理 API ===============

        app.MapPost("/api/plugins/install", async (HttpContext ctx, PluginPackageManager packages) =>
        {
            try
            {
                var result = await packages.InstallAsync(ctx.Request.Body, ctx.Request.ContentLength, ctx.RequestAborted);
                return Results.Json(new { success = true, result.Id, result.Version, result.RequiresRestart, result.Message });
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or CryptographicException)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 400);
            }
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/plugins/{pluginId}/rollback", (string pluginId, PluginPackageManager packages) =>
        {
            try
            {
                var result = packages.Rollback(pluginId);
                return Results.Json(new { success = true, result.Id, result.Version, result.RequiresRestart, result.Message });
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                return Results.Json(new { success = false, message = ex.Message }, statusCode: 400);
            }
        }).Produces<PluginPackageResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapGet("/api/plugins", (HttpContext context, IPluginManager pluginManager) =>
        {
            var plugins = pluginManager.GetAllPlugins().Select(p =>
            {
                var meta = p.Metadata;
                var pluginId = meta.Id;
                var state = pluginManager.GetPluginState(pluginId);
                return new PluginListItem(pluginId, meta.Name, meta.Version, meta.Description, meta.Author,
                    meta.Priority.ToString(), state.ToString(), state is PluginState.Running or PluginState.Initialized,
                    pluginManager.IsSystemPlugin(pluginId), meta.EnabledByDefault, meta.DefaultOptions != null, p.GetActions().ToList(), PluginLocalization.Load(p));
            }).OrderBy(p => p.IsSystem ? 0 : 1).ThenBy(p => p.Name).ToList();

            return Results.Ok(ManagementLists.From(plugins).Sort("name", item => item.Name).Sort("id", item => item.Id)
                .Sort("state", item => item.State).Sort("isSystem", item => item.IsSystem)
                .Search(item => $"{item.Id} {item.Name} {item.Description} {item.Author} {item.State}").Read(context.Request.Query, "name"));
        }).Produces<ManagementPage<PluginListItem>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/plugins/toggle", async (HttpContext ctx, IConfiguration config, IPluginManager pluginManager, PluginToggleRequest request) =>
        {

            if (request == null || string.IsNullOrEmpty(request.PluginId))
            {
                return Results.Json(new { success = false, message = "PluginId 不能为空" }, statusCode: 400);
            }

            var plugin = pluginManager.GetPlugin(request.PluginId);
            if (plugin == null)
            {
                return Results.Json(new { success = false, message = $"插件 {request.PluginId} 不存在" }, statusCode: 404);
            }

            await _lbPatchLock.WaitAsync(ctx.RequestAborted);
            try
            {
            var currentState = pluginManager.GetPluginState(request.PluginId);
            var isRunning = currentState == PluginState.Running || currentState == PluginState.Initialized;

            if (isRunning)
            {
                await pluginManager.DisablePluginAsync(request.PluginId);
            }
            else
            {
                await pluginManager.EnablePluginAsync(request.PluginId);
            }

            var patch = await ReadPatchFile();
            var states = EnsurePatchSection(patch, "PluginEnabledStates");
            states[request.PluginId] = !isRunning;
            UpdatePatchSourceTracking(patch);
            await SavePatchAndReload(patch, config);

            // Wait for any in-flight reload transition before returning the actual activation state.
            if (isRunning) await pluginManager.DisablePluginAsync(request.PluginId);
            else await pluginManager.EnablePluginAsync(request.PluginId);

            var newState = pluginManager.GetPluginState(request.PluginId);
            return Results.Json(new
            {
                success = true,
                message = isRunning ? "插件已禁用并保存" : "插件已启用并保存",
                state = newState.ToString(),
                isEnabled = newState == PluginState.Running || newState == PluginState.Initialized,
            });
            }
            finally { _lbPatchLock.Release(); }
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== 插件配置读取 ===============

        app.MapGet("/api/plugins/{pluginId}/config", async (string pluginId, IConfiguration config, IPluginManager pluginManager,
            [Microsoft.AspNetCore.Mvc.FromServices] PluginScopedConfiguration resolver,
            [Microsoft.AspNetCore.Mvc.FromServices] PluginConfigurationPublisher publisher) =>
        {
            await ConfigurationMutationGate.Patch.WaitAsync();
            try
            {
            var plugin = pluginManager.GetPlugin(pluginId);
            if (plugin == null)
            {
                return Results.Json(new { success = false, message = $"插件 {pluginId} 不存在" }, statusCode: 404);
            }

            // 1. 创建一个新的 Options 实例来获取原始默认值
            //    （因为 Metadata.DefaultOptions 是运行时实例，已被 BindOptionsFromConfig 覆盖）
            var defaults = new Dictionary<string, string>();
            var defaultOptions = plugin.Metadata.DefaultOptions;
            if (defaultOptions != null)
            {
                var freshDefaults = Activator.CreateInstance(defaultOptions.GetType());
                if (freshDefaults != null)
                {
                    SerializeOptionsToFlat(freshDefaults, "", defaults);
                }
            }

            // 2. 从运行时 DefaultOptions 实例序列化当前配置
            //    （它就是运行时 Options，已被 BindOptionsFromConfig 绑定过）
            var current = new Dictionary<string, string>();
            if (defaultOptions != null)
            {
                SerializeOptionsToFlat(defaultOptions, "", current);
            }
            if (plugin.Metadata.SupportsScopedConfiguration)
                current = resolver.Describe(pluginId, "", "/").Config;
            else if (defaultOptions != null)
            {
                // Unmigrated plugins show persisted settings, which may still await restart.
                var prefix = $"Plugins:{pluginId}:";
                var saved = config.GetSection($"Plugins:{pluginId}").AsEnumerable()
                    .Where(p => p.Value != null && p.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(p => p.Key[prefix.Length..], p => p.Value!, StringComparer.OrdinalIgnoreCase);
                if (saved.Count > 0)
                {
                    current.Clear();
                    SerializeOptionsToFlat(PluginConfigurationSchema.Bind(defaultOptions.GetType(), saved), "", current);
                }
            }

            // 3. 提取属性的中文标签
            var labels = defaultOptions != null
                ? ExtractPropertyLabels(defaultOptions.GetType())
                : new Dictionary<string, string>();

            // 4. 提取 List<T> 元素类型的字段默认值（用于前端推断字段类型和初始值）
            var fieldDefaults = defaultOptions != null
                ? ExtractListElementDefaults(defaultOptions.GetType())
                : new Dictionary<string, string>();

            var localization = PluginLocalization.Load(plugin);
            var schema = defaultOptions == null ? [] : PluginConfigurationSchema.Describe(defaultOptions.GetType())
                .Select(field => PluginLocalization.Localize(field, localization)).ToArray();
            if (defaultOptions != null)
            {
                foreach (var values in new[] { current, defaults, fieldDefaults })
                    foreach (var key in values.Keys.Where(k => PluginConfigurationSchema.SensitivePath(defaultOptions.GetType(), k)).ToArray())
                        values.Remove(key);
            }
            return Results.Json(new { success = true, pluginId, config = current, defaults, labels, fieldDefaults, schema, localization,
                supportsScopedConfiguration = plugin.Metadata.SupportsScopedConfiguration, version = publisher.Version() });
            }
            finally { ConfigurationMutationGate.Patch.Release(); }
        }).Produces<PluginConfigResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        app.MapPost("/api/plugins/{pluginId}/config/validate", async (string pluginId, HttpContext ctx,
            IPluginManager pluginManager, IConfiguration config, Dictionary<string, string> body,
            [Microsoft.AspNetCore.Mvc.FromServices] PluginConfigurationPublisher publisher) =>
        {
            await ConfigurationMutationGate.Patch.WaitAsync();
            try
            {
            var plugin = pluginManager.GetPlugin(pluginId);
            if (plugin == null) return Results.NotFound();
            var errors = PreparePluginConfiguration(plugin, body, out var candidate, config);
            if (errors.Count == 0)
            {
                try { publisher.Validate(plugin, candidate); }
                catch (InvalidOperationException ex) { errors["$scopes"] = [ex.Message]; }
            }
            return errors.Count == 0
                ? Results.Ok(new PluginConfigValidationResponse(true, errors))
                : ManagementApiContract.Error(ctx, 400, "validation_failed", "插件配置校验失败", errors);
            }
            finally { ConfigurationMutationGate.Patch.Release(); }
        }).Produces<PluginConfigValidationResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== 插件配置更新 ===============

        app.MapPost("/api/plugins/{pluginId}/config", async (string pluginId, HttpContext ctx, IConfiguration config, IPluginManager pluginManager, Dictionary<string, string> body,
            [Microsoft.AspNetCore.Mvc.FromServices] PluginConfigurationPublisher publisher) =>
        {
            var plugin = pluginManager.GetPlugin(pluginId);
            if (plugin == null)
            {
                return Results.Json(new { success = false, message = $"插件 {pluginId} 不存在" }, statusCode: 404);
            }


            if (body == null)
            {
                return Results.Json(new { success = false, message = "请求体不能为空" }, statusCode: 400);
            }

            var errors = PreparePluginConfiguration(plugin, body, out var candidate, config);
            if (errors.Count != 0)
                return ManagementApiContract.Error(ctx, 400, "validation_failed", "插件配置校验失败", errors);

            var result = await publisher.PublishAsync(plugin, ctx.Request.Headers.IfMatch.ToString(), candidate,
                validate => PersistPluginConfigPatch(config, pluginId, candidate, plugin.Metadata.SupportsScopedConfiguration, validate));
            return Results.Json(result, statusCode: result.Success ? 200 : result.Status == "version_conflict" ? 409 : result.Status == "validation_failed" ? 400 : 500);
        }).Produces<PluginPublicationResult>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== 插件操作（统一框架） ===============

        // 获取插件支持的操作列表
        app.MapGet("/api/plugins/{pluginId}/actions", (string pluginId, IPluginManager pluginManager) =>
        {
            var plugin = pluginManager.GetPlugin(pluginId);
            if (plugin == null)
                return Results.Json(new { success = false, message = $"插件 {pluginId} 不存在" }, statusCode: 404);

            var actions = plugin.GetActions().Select(a => new
            {
                id = a.Id,
                name = a.Name,
                type = a.Type,
                description = a.Description,
            }).ToList();

            return Results.Json(new { success = true, pluginId, actions });
        }).Produces<PluginActionsResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 执行插件操作
        app.MapPost("/api/plugins/{pluginId}/actions/{actionId}", async (string pluginId, string actionId, HttpContext ctx, IPluginManager pluginManager, Dictionary<string, string>? parameters) =>
        {
            var plugin = pluginManager.GetPlugin(pluginId);
            if (plugin == null)
                return Results.Json(new { success = false, message = $"插件 {pluginId} 不存在" }, statusCode: 404);

            var actions = plugin.GetActions();
            if (!actions.Any(a => a.Id == actionId))
                return Results.Json(new { success = false, message = $"插件 {pluginId} 不支持操作: {actionId}" }, statusCode: 404);

            var result = await plugin.ExecuteActionAsync(actionId, parameters);
            return Results.Json(new { success = result.Success, message = result.Message, data = result.Data });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
