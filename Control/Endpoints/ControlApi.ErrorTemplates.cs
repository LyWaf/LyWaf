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
    private sealed record ErrorTemplateSavedResponse(bool Success, string Message);
    private sealed record ErrorTemplateRequest([property: System.ComponentModel.DataAnnotations.Required,
        System.ComponentModel.DataAnnotations.StringLength(1024 * 1024)] string Content);

    private static async Task<string> ReadManagedTemplate(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length > 4 * 1024 * 1024) throw new ArgumentException("模板文件超过 4 MiB", "content");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static void ValidateTemplateCode(int statusCode)
    {
        if (statusCode is < 400 or > 599) throw new ArgumentException("错误状态码必须在 400 到 599 之间", nameof(statusCode));
    }
    private sealed record ErrorTemplateListItem(int StatusCode, bool HasOriginal, bool HasEdit);
    private static void MapErrorTemplateEndpoints(WebApplication app, int controlPort)
    {
        // =============== 错误模板管理 ===============

        // 默认支持的状态码
        int[] errorTemplateCodes = [403, 404, 429, 500, 502, 503];

        // 列出所有错误模板
        app.MapGet("/api/error-templates", (HttpContext ctx) =>
        {
                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;
                var list = new List<ErrorTemplateListItem>();

                foreach (var code in errorTemplateCodes)
                {
                    var originalPath = service?.GetOriginalFilePath(code) ?? "";
                    var editPath = service?.GetEditFilePath(code) ?? "";
                    list.Add(new(code, File.Exists(originalPath), File.Exists(editPath)));
                }
                return Results.Ok(ManagementLists.From(list).Sort("statusCode", item => item.StatusCode)
                    .Search(item => item.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)).Read(ctx.Request.Query, "statusCode"));
        }).Produces<ManagementPage<ErrorTemplateListItem>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取指定状态码的模板内容
        app.MapGet("/api/error-templates/{statusCode:int}", async (int statusCode, HttpContext ctx) =>
        {
            ValidateTemplateCode(statusCode);
            {
                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;

                var editPath = service?.GetEditFilePath(statusCode) ?? "";
                var originalPath = service?.GetOriginalFilePath(statusCode) ?? "";

                string? editContent = null;
                string? originalContent = null;

                if (File.Exists(editPath))
                    editContent = await ReadManagedTemplate(editPath, ctx.RequestAborted);
                if (File.Exists(originalPath))
                    originalContent = await ReadManagedTemplate(originalPath, ctx.RequestAborted);

                return TypedResults.Ok(new
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
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 保存编辑版模板
        app.MapPost("/api/error-templates/{statusCode:int}", async (int statusCode, HttpContext ctx, ErrorTemplateRequest body) =>
        {
            ValidateTemplateCode(statusCode);
            {

                var content = body.Content;
                if (string.IsNullOrEmpty(content))
                    return Results.Json(new { success = false, message = "模板内容不能为空" }, statusCode: 400);

                var templateService = ctx.RequestServices.GetRequiredService<LyWaf.Services.ErrorTemplate.IErrorTemplateService>();
                var service = templateService as LyWaf.Services.ErrorTemplate.ErrorTemplateService;

                var editPath = service?.GetEditFilePath(statusCode)
                    ?? Path.Combine(Directory.GetCurrentDirectory(), "templates", $".lywaf.{statusCode}.html");

                // 确保 templates 目录存在
                Directory.CreateDirectory(Path.GetDirectoryName(editPath)!);
                var tempPath = editPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, ctx.RequestAborted);
                    File.Move(tempPath, editPath, true);
                }
                finally { if (File.Exists(tempPath)) File.Delete(tempPath); }

                // 清除缓存，下次请求使用新内容
                service?.ClearCache();

                return Results.Json(new { success = true, message = "模板已保存" });
            }
        }).Produces<ErrorTemplateSavedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 删除编辑版模板（恢复为原始版本）
        app.MapPost("/api/error-templates/{statusCode:int}/revert", (int statusCode, HttpContext ctx) =>
        {
            ValidateTemplateCode(statusCode);
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
        }).Produces<ErrorTemplateSavedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // WAF 自定义规则管理
    }
}
