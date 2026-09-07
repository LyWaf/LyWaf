using System.Diagnostics;
using System.Text;
using LyWaf.Control.Endpoints;
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
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using LyWaf.Utils;
using LyWaf.Services.Param;
using LyWaf.Services.ProxyServer;
using LyWaf.Services.Configuration;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.RuntimeState;
using LyWaf.Services;
using LyWaf.Policy;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using NLog;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapErrorTemplateEndpoints(WebApplication app, int controlPort)
    {
        // =============== 错误模板管理 ===============

        // 默认支持的状态码
        int[] errorTemplateCodes = [403, 404, 429, 500, 502, 503];

        // 列出所有错误模板
        app.MapGet("/api/error-templates", (HttpContext ctx) =>
        {
            try
            {
                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;
                var list = new List<object>();

                foreach (var code in errorTemplateCodes)
                {
                    var originalPath = service?.GetOriginalFilePath(code) ?? "";
                    var editPath = service?.GetEditFilePath(code) ?? "";
                    list.Add(new
                    {
                        statusCode = code,
                        hasOriginal = File.Exists(originalPath),
                        hasEdit = File.Exists(editPath)
                    });
                }
                return Results.Json(new { success = true, templates = list });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取模板列表失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 获取指定状态码的模板内容
        app.MapGet("/api/error-templates/{statusCode:int}", async (int statusCode, HttpContext ctx) =>
        {
            try
            {
                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;

                var editPath = service?.GetEditFilePath(statusCode) ?? "";
                var originalPath = service?.GetOriginalFilePath(statusCode) ?? "";

                string? editContent = null;
                string? originalContent = null;

                if (File.Exists(editPath))
                    editContent = await File.ReadAllTextAsync(editPath, Encoding.UTF8);
                if (File.Exists(originalPath))
                    originalContent = await File.ReadAllTextAsync(originalPath, Encoding.UTF8);

                return Results.Json(new
                {
                    success = true,
                    statusCode,
                    editContent,
                    originalContent,
                    hasEdit = editContent != null,
                    hasOriginal = originalContent != null,
                    // 当前生效内容：优先编辑版
                    activeContent = editContent ?? originalContent ?? ""
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"获取模板失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 保存编辑版模板
        app.MapPost("/api/error-templates/{statusCode:int}", async (int statusCode, HttpContext ctx) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
                var content = body?.GetValueOrDefault("content");
                if (string.IsNullOrEmpty(content))
                    return Results.Json(new { success = false, message = "模板内容不能为空" }, statusCode: 400);

                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;

                var editPath = service?.GetEditFilePath(statusCode)
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "templates", $".lywaf.{statusCode}.html");

                // 确保 templates 目录存在
                Directory.CreateDirectory(Path.GetDirectoryName(editPath)!);
                await File.WriteAllTextAsync(editPath, content, Encoding.UTF8);

                // 清除缓存，下次请求使用新内容
                service?.ClearCache();

                return Results.Json(new { success = true, message = "模板已保存" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"保存模板失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除编辑版模板（恢复为原始版本）
        app.MapPost("/api/error-templates/{statusCode:int}/revert", (int statusCode, HttpContext ctx) =>
        {
            try
            {
                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;

                var editPath = service?.GetEditFilePath(statusCode)
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "templates", $".lywaf.{statusCode}.html");

                if (File.Exists(editPath))
                {
                    File.Delete(editPath);
                    service?.ClearCache();
                    return Results.Json(new { success = true, message = "已恢复为原始模板" });
                }
                return Results.Json(new { success = true, message = "没有编辑版本需要恢复" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"恢复模板失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // WAF 自定义规则管理
    }
}

