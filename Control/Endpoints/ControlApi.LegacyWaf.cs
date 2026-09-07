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
using Microsoft.Extensions.Options;
using NLog;

namespace LyWaf.Control;

public static partial class ControlApi
{
    private static void MapLegacyWafEndpoints(WebApplication app, int controlPort)
    {
        // =============== WAF 规则管理 API ===============

        // 获取 WAF 规则列表
        app.MapGet("/api/waf/rules", (HttpContext ctx, IProtectService protectService) =>
        {
            var options = protectService.GetOptions();
            return Results.Json(new
            {
                success = true,
                openArgsCheck = options.OpenArgsCheck,
                openPostCheck = options.OpenPostCheck,
                argsRules = protectService.GetArgsRegexList(),
                postRules = protectService.GetPostRegexList(),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 添加 WAF Args 规则
        app.MapPost("/api/waf/args/add", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.AddArgsRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加 Args WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：正则格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 WAF Args 规则
        app.MapPost("/api/waf/args/remove", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.RemoveArgsRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 Args WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 添加 WAF Post 规则
        app.MapPost("/api/waf/post/add", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<AddWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.AddPostRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已添加 Post WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "添加失败：正则格式无效或已存在" }, statusCode: 400);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"添加失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 移除 WAF Post 规则
        app.MapPost("/api/waf/post/remove", async (HttpContext ctx, IProtectService protectService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<RemoveWafRuleRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.Pattern))
                {
                    return Results.Json(new { success = false, message = "正则表达式不能为空" }, statusCode: 400);
                }

                if (protectService.RemovePostRegex(request.Pattern))
                {
                    return Results.Json(new
                    {
                        success = true,
                        message = $"已移除 Post WAF 规则: {request.Pattern}",
                        pattern = request.Pattern,
                        timestamp = DateTime.Now
                    });
                }
                else
                {
                    return Results.Json(new { success = false, message = "移除失败：规则不存在" }, statusCode: 404);
                }
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"移除失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

    }
}

