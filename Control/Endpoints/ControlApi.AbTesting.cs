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
    private static void MapAbTestEndpoints(WebApplication app, int controlPort)
    {
        // =============== A/B 测试管理 API ===============

        // 获取所有 A/B 测试配置
        app.MapGet("/api/abtest/configs", (HttpContext ctx, IABTestService abTestService) =>
        {
            var configs = abTestService.GetAllConfigs();
            return Results.Json(new
            {
                success = true,
                count = configs.Count,
                configs = configs.Select(c => new
                {
                    testId = c.Key,
                    name = c.Value.Name,
                    enabled = c.Value.Enabled,
                    mode = c.Value.Mode.ToString(),
                    cookieName = c.Value.CookieName,
                    variants = c.Value.Variants,
                    variantTargets = c.Value.VariantTargets,
                    matchPaths = c.Value.MatchPaths,
                    excludePaths = c.Value.ExcludePaths
                }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 获取单个 A/B 测试配置
        app.MapGet("/api/abtest/configs/{testId}", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var config = abTestService.GetConfig(testId);
            if (config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            return Results.Json(new
            {
                success = true,
                testId = testId,
                config = new
                {
                    name = config.Name,
                    enabled = config.Enabled,
                    mode = config.Mode.ToString(),
                    cookieName = config.CookieName,
                    cookieExpireDays = config.CookieExpireDays,
                    variants = config.Variants,
                    variantTargets = config.VariantTargets,
                    matchPaths = config.MatchPaths,
                    excludePaths = config.ExcludePaths
                },
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 创建或更新 A/B 测试配置
        app.MapPost("/api/abtest/configs", async (HttpContext ctx, IABTestService abTestService) =>
        {
            try
            {
                var request = await ctx.Request.ReadFromJsonAsync<CreateABTestRequest>();
                if (request == null || string.IsNullOrWhiteSpace(request.TestId))
                {
                    return Results.Json(new { success = false, message = "测试 ID 不能为空" }, statusCode: 400);
                }

                if (request.Variants == null || request.Variants.Count < 2)
                {
                    return Results.Json(new { success = false, message = "至少需要 2 个变体" }, statusCode: 400);
                }

                // 验证权重总和
                var totalWeight = request.Variants.Values.Sum();
                if (totalWeight <= 0)
                {
                    return Results.Json(new { success = false, message = "权重总和必须大于 0" }, statusCode: 400);
                }

                var config = new ABTestConfig
                {
                    Name = request.Name ?? request.TestId,
                    Enabled = request.Enabled ?? true,
                    Mode = Enum.TryParse<ABTestMode>(request.Mode, true, out var mode) ? mode : ABTestMode.CookieSticky,
                    CookieName = request.CookieName ?? $"ab_{request.TestId}",
                    CookieExpireDays = request.CookieExpireDays ?? 30,
                    Variants = request.Variants,
                    VariantTargets = request.VariantTargets ?? new Dictionary<string, string>(),
                    MatchPaths = request.MatchPaths ?? new List<string>(),
                    ExcludePaths = request.ExcludePaths ?? new List<string>()
                };

                abTestService.SetConfig(request.TestId, config);

                return Results.Json(new
                {
                    success = true,
                    message = $"已创建/更新 A/B 测试: {request.TestId}",
                    testId = request.TestId,
                    config = new
                    {
                        name = config.Name,
                        enabled = config.Enabled,
                        mode = config.Mode.ToString(),
                        variants = config.Variants,
                        totalWeight = totalWeight
                    },
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"创建失败: {ex.Message}" }, statusCode: 500);
            }
        }).RequireHost($"*:{controlPort}");

        // 删除 A/B 测试配置
        app.MapDelete("/api/abtest/configs/{testId}", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            if (abTestService.RemoveConfig(testId))
            {
                return Results.Json(new
                {
                    success = true,
                    message = $"已删除 A/B 测试: {testId}",
                    testId = testId,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }
        }).RequireHost($"*:{controlPort}");

        // 启用/禁用 A/B 测试
        app.MapPost("/api/abtest/configs/{testId}/toggle", async (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var config = abTestService.GetConfig(testId);
            if (config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            var request = await ctx.Request.ReadFromJsonAsync<ToggleABTestRequest>();
            config.Enabled = request?.Enabled ?? !config.Enabled;
            abTestService.SetConfig(testId, config);

            return Results.Json(new
            {
                success = true,
                message = config.Enabled ? "已启用 A/B 测试" : "已禁用 A/B 测试",
                testId = testId,
                enabled = config.Enabled,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 获取 A/B 测试统计
        app.MapGet("/api/abtest/stats/{testId}", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var stats = abTestService.GetStats(testId);
            var config = abTestService.GetConfig(testId);
            
            if (stats == null || config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            // 计算各变体的实际百分比
            var variantPercentages = stats.VariantHits.ToDictionary(
                v => v.Key,
                v => stats.TotalRequests > 0 ? Math.Round((double)v.Value / stats.TotalRequests * 100, 2) : 0
            );

            return Results.Json(new
            {
                success = true,
                testId = testId,
                stats = new
                {
                    totalRequests = stats.TotalRequests,
                    variantHits = stats.VariantHits,
                    variantPercentages = variantPercentages,
                    configuredWeights = config.Variants,
                    startTime = stats.StartTime,
                    lastRequestTime = stats.LastRequestTime
                },
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

        // 重置 A/B 测试统计
        app.MapPost("/api/abtest/stats/{testId}/reset", (HttpContext ctx, IABTestService abTestService, string testId) =>
        {
            var config = abTestService.GetConfig(testId);
            if (config == null)
            {
                return Results.Json(new { success = false, message = "A/B 测试配置不存在" }, statusCode: 404);
            }

            abTestService.ResetStats(testId);

            return Results.Json(new
            {
                success = true,
                message = "已重置 A/B 测试统计",
                testId = testId,
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}");

    }
}

