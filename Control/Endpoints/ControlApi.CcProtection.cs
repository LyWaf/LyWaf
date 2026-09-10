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
    private sealed record CcMessageResponse(bool Success, string Message);
    private sealed record CcBasicRuleView(string Path, int Period, int LimitNum, string FbTime);
    private sealed record CcBasicAddedResponse(bool Success, string Message, CcBasicRuleView Rule, DateTime Timestamp);
    private sealed record CcBasicRemovedResponse(bool Success, string Message, string Path, DateTime Timestamp);
    private sealed record CcConditionView(string Target, string Operator, List<string> Values);
    private sealed record CcRuleView(string Id, string Name, bool Enabled, string Type, CcConditionView[] Conditions,
        int Period, int Threshold, string Action, int ActionSeconds, int Priority, DateTime CreatedAt);
    private sealed record CcDetailResponse(bool Success, CcRuleView Rule, DateTime Timestamp);
    private sealed record CcMutationResponse(bool Success, string Message, string RuleId, DateTime Timestamp, bool? Enabled = null);
    private static void MapCcProtectionEndpoints(WebApplication app, int controlPort)
    {
        // =============== 统计配置管理 API ===============

        // 获取统计配置（白名单路径 + 路径统计规则）
        app.MapGet("/api/statistic/config", (HttpContext ctx, IStatisticService statisticService) =>
        {
            return TypedResults.Ok(new
            {
                success = true,
                whitePaths = statisticService.GetWhitePaths(),
                pathStas = statisticService.GetPathStas(),
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加白名单路径
        app.MapPost("/api/statistic/white-paths/add", (HttpContext ctx, IStatisticService statisticService, PathRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { success = false, message = "路径不能为空" });

            if (statisticService.AddWhitePath(request.Path))
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, $"添加白名单路径: {request.Path}", clientIp);
                return Results.Json(new { success = true, message = "添加成功" });
            }
            return Results.Json(new { success = false, message = "路径已存在" });
        }).Produces<CcMessageResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 移除白名单路径
        app.MapPost("/api/statistic/white-paths/remove", (HttpContext ctx, IStatisticService statisticService, PathRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { success = false, message = "路径不能为空" });

            if (statisticService.RemoveWhitePath(request.Path))
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, $"移除白名单路径: {request.Path}", clientIp);
                return Results.Json(new { success = true, message = "移除成功" });
            }
            return Results.Json(new { success = false, message = "路径不存在" });
        }).Produces<CcMessageResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加路径统计规则
        app.MapPost("/api/statistic/path-stas/add", (HttpContext ctx, IStatisticService statisticService, PathRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { success = false, message = "路径不能为空" });

            if (statisticService.AddPathSta(request.Path))
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, $"添加路径统计规则: {request.Path}", clientIp);
                return Results.Json(new { success = true, message = "添加成功" });
            }
            return Results.Json(new { success = false, message = "路径已存在" });
        }).Produces<CcMessageResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 移除路径统计规则
        app.MapPost("/api/statistic/path-stas/remove", (HttpContext ctx, IStatisticService statisticService, PathRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return Results.Json(new { success = false, message = "路径不能为空" });

            if (statisticService.RemovePathSta(request.Path))
            {
                var audit = ctx.RequestServices.GetRequiredService<IAuditLogService>();
                var username = ctx.Items["Username"]?.ToString() ?? "unknown";
                var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
                audit.Log(username, $"移除路径统计规则: {request.Path}", clientIp);
                return Results.Json(new { success = true, message = "移除成功" });
            }
            return Results.Json(new { success = false, message = "路径不存在" });
        }).Produces<CcMessageResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== CC 防护规则管理 API ===============

        // 获取 CC 规则列表
        app.MapGet("/api/cc/rules", (HttpContext ctx, IStatisticService statisticService) =>
        {
            var rules = statisticService.GetLimitCcRules().Select(r => new
            {
                path = r.Path,
                period = r.Period,
                limitNum = r.LimitNum,
                fbTime = r.FbTime.ToString()
            });
            return TypedResults.Ok(ManagementLists.From(rules).Sort("path", rule => rule.path)
                .Sort("period", rule => rule.period).Sort("limitNum", rule => rule.limitNum)
                .Search(rule => rule.path).Read(ctx.Request.Query, "path"));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加 CC 规则
        app.MapPost("/api/cc/rules/add", (HttpContext ctx, IStatisticService statisticService, AddCcRuleRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
            }

            var rule = new LimitCcOption
            {
                Path = request.Path,
                Period = request.Period ?? 60,
                LimitNum = request.LimitNum ?? 100,
                FbTime = request.FbTime ?? TimeSpan.FromMinutes(5)
            };

            if (statisticService.AddLimitCcRule(rule))
            {
                return Results.Json(new
                {
                    success = true,
                    message = $"已添加 CC 规则: {request.Path}",
                    rule = new { rule.Path, rule.Period, rule.LimitNum, fbTime = rule.FbTime.ToString() },
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "添加失败：规则已存在" }, statusCode: 400);
            }
        }).Produces<CcBasicAddedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 移除 CC 规则
        app.MapPost("/api/cc/rules/remove", (HttpContext ctx, IStatisticService statisticService, RemoveCcRuleRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.Json(new { success = false, message = "路径不能为空" }, statusCode: 400);
            }

            if (statisticService.RemoveLimitCcRule(request.Path))
            {
                return Results.Json(new
                {
                    success = true,
                    message = $"已移除 CC 规则: {request.Path}",
                    path = request.Path,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "移除失败：规则不存在" }, statusCode: 404);
            }
        }).Produces<CcBasicRemovedResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // =============== 高级 CC 规则管理 API ===============

        // 获取所有高级 CC 规则
        app.MapGet("/api/cc/advanced", (HttpContext ctx, IStatisticService statisticService) =>
        {
            var rules = statisticService.GetAdvancedCcRules();
            var values = rules.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                enabled = r.Enabled,
                type = r.Type.ToString(),
                conditions = r.Conditions.Select(c => new
                {
                    target = c.Target.ToString(),
                    @operator = c.Operator.ToString(),
                    values = c.Values
                }),
                period = r.Period,
                threshold = r.Threshold,
                action = r.Action.ToString(),
                actionSeconds = r.ActionSeconds,
                priority = r.Priority,
                createdAt = r.CreatedAt
            });
            return TypedResults.Ok(ManagementLists.From(values).Sort("priority", rule => rule.priority)
                .Sort("name", rule => rule.name).Sort("createdAt", rule => rule.createdAt)
                .Search(rule => rule.name + " " + rule.type).Timestamp(rule => rule.createdAt)
                .Read(ctx.Request.Query, "priority", true));
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 按类型获取高级 CC 规则
        app.MapGet("/api/cc/advanced/type/{type}", (HttpContext ctx, IStatisticService statisticService, string type) =>
        {
            if (!Enum.TryParse<CcRuleType>(type, true, out var ruleType) || !Enum.IsDefined(ruleType))
            {
                return Results.Json(new { success = false, message = "无效的规则类型" }, statusCode: 400);
            }

            var rules = statisticService.GetAdvancedCcRulesByType(ruleType);
            var values = rules.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                enabled = r.Enabled,
                type = r.Type.ToString(),
                conditions = r.Conditions.Select(c => new
                {
                    target = c.Target.ToString(),
                    @operator = c.Operator.ToString(),
                    values = c.Values
                }),
                period = r.Period,
                threshold = r.Threshold,
                action = r.Action.ToString(),
                actionSeconds = r.ActionSeconds,
                priority = r.Priority,
                createdAt = r.CreatedAt
            });
            return Results.Ok(ManagementLists.From(values).Sort("priority", rule => rule.priority)
                .Sort("name", rule => rule.name).Sort("createdAt", rule => rule.createdAt)
                .Search(rule => rule.name).Timestamp(rule => rule.createdAt).Read(ctx.Request.Query, "priority", true));
        }).Produces<ManagementPage<CcRuleView>>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取单个高级 CC 规则
        app.MapGet("/api/cc/advanced/{ruleId}", (HttpContext ctx, IStatisticService statisticService, string ruleId) =>
        {
            var rule = statisticService.GetAdvancedCcRule(ruleId);
            if (rule == null)
            {
                return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
            }

            return Results.Json(new
            {
                success = true,
                rule = new
                {
                    id = rule.Id,
                    name = rule.Name,
                    enabled = rule.Enabled,
                    type = rule.Type.ToString(),
                    conditions = rule.Conditions.Select(c => new
                    {
                        target = c.Target.ToString(),
                        @operator = c.Operator.ToString(),
                        values = c.Values
                    }),
                    period = rule.Period,
                    threshold = rule.Threshold,
                    action = rule.Action.ToString(),
                    actionSeconds = rule.ActionSeconds,
                    priority = rule.Priority,
                    createdAt = rule.CreatedAt
                },
                timestamp = DateTime.Now
            });
        }).Produces<CcDetailResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 添加高级 CC 规则
        app.MapPost("/api/cc/advanced/add", (HttpContext ctx, IStatisticService statisticService, AddAdvancedCcRuleRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.Json(new { success = false, message = "规则名称不能为空" }, statusCode: 400);
            }

            var rule = new AdvancedCcRule
            {
                Name = request.Name,
                Enabled = request.Enabled ?? true,
                Type = Enum.TryParse<CcRuleType>(request.Type, true, out var t) ? t : CcRuleType.FrequentAccess,
                Period = request.Period ?? 10,
                Threshold = request.Threshold ?? 100,
                Action = Enum.TryParse<CcAction>(request.Action, true, out var a) ? a : CcAction.Captcha,
                ActionSeconds = request.ActionSeconds ?? 600,
                Priority = request.Priority ?? 100,
                Conditions = request.Conditions?.Select(c => new CcCondition
                {
                    Target = Enum.TryParse<CcMatchTarget>(c.Target, true, out var ct) ? ct : CcMatchTarget.UrlPath,
                    Operator = Enum.TryParse<CcMatchOperator>(c.Operator, true, out var co) ? co : CcMatchOperator.Equal,
                    Values = c.Values ?? []
                }).ToList() ?? []
            };

            if (statisticService.AddAdvancedCcRule(rule))
            {
                return Results.Json(new
                {
                    success = true,
                    message = $"已添加高级 CC 规则: {rule.Name}",
                    ruleId = rule.Id,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "添加失败" }, statusCode: 400);
            }
        }).Produces<CcMutationResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 更新高级 CC 规则
        app.MapPost("/api/cc/advanced/update", (HttpContext ctx, IStatisticService statisticService, UpdateAdvancedCcRuleRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.Id))
            {
                return Results.Json(new { success = false, message = "规则 ID 不能为空" }, statusCode: 400);
            }

            var existingRule = statisticService.GetAdvancedCcRule(request.Id);
            if (existingRule == null)
            {
                return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
            }

            var rule = new AdvancedCcRule
            {
                Id = request.Id,
                Name = request.Name ?? existingRule.Name,
                Enabled = request.Enabled ?? existingRule.Enabled,
                Type = request.Type != null && Enum.TryParse<CcRuleType>(request.Type, true, out var t) ? t : existingRule.Type,
                Period = request.Period ?? existingRule.Period,
                Threshold = request.Threshold ?? existingRule.Threshold,
                Action = request.Action != null && Enum.TryParse<CcAction>(request.Action, true, out var a) ? a : existingRule.Action,
                ActionSeconds = request.ActionSeconds ?? existingRule.ActionSeconds,
                Priority = request.Priority ?? existingRule.Priority,
                Conditions = request.Conditions?.Select(c => new CcCondition
                {
                    Target = Enum.TryParse<CcMatchTarget>(c.Target, true, out var ct) ? ct : CcMatchTarget.UrlPath,
                    Operator = Enum.TryParse<CcMatchOperator>(c.Operator, true, out var co) ? co : CcMatchOperator.Equal,
                    Values = c.Values ?? []
                }).ToList() ?? existingRule.Conditions
            };

            if (statisticService.UpdateAdvancedCcRule(rule))
            {
                return Results.Json(new
                {
                    success = true,
                    message = $"已更新高级 CC 规则: {rule.Name}",
                    ruleId = rule.Id,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "更新失败" }, statusCode: 400);
            }
        }).Produces<CcMutationResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 删除高级 CC 规则
        app.MapPost("/api/cc/advanced/remove", (HttpContext ctx, IStatisticService statisticService, RemoveAdvancedCcRuleRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.RuleId))
            {
                return Results.Json(new { success = false, message = "规则 ID 不能为空" }, statusCode: 400);
            }

            if (statisticService.RemoveAdvancedCcRule(request.RuleId))
            {
                return Results.Json(new
                {
                    success = true,
                    message = "已删除高级 CC 规则",
                    ruleId = request.RuleId,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "删除失败：规则不存在" }, statusCode: 404);
            }
        }).Produces<CcMutationResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 启用/禁用高级 CC 规则
        app.MapPost("/api/cc/advanced/toggle", (HttpContext ctx, IStatisticService statisticService, ToggleAdvancedCcRuleRequest request) =>
        {

            if (request == null || string.IsNullOrWhiteSpace(request.RuleId))
            {
                return Results.Json(new { success = false, message = "规则 ID 不能为空" }, statusCode: 400);
            }

            var rule = statisticService.GetAdvancedCcRule(request.RuleId);
            var newState = request.Enabled ?? !(rule?.Enabled ?? false);

            if (statisticService.ToggleAdvancedCcRule(request.RuleId, newState))
            {
                return Results.Json(new
                {
                    success = true,
                    message = newState ? "已启用规则" : "已禁用规则",
                    ruleId = request.RuleId,
                    enabled = newState,
                    timestamp = DateTime.Now
                });
            }
            else
            {
                return Results.Json(new { success = false, message = "操作失败：规则不存在" }, statusCode: 404);
            }
        }).Produces<CcMutationResponse>().RequireHost($"*:{controlPort}").RequireManagementPermission();

        // 获取可用的匹配目标、操作符、动作等枚举值
        app.MapGet("/api/cc/advanced/enums", (HttpContext ctx) =>
        {
            return TypedResults.Ok(new
            {
                success = true,
                matchTargets = Enum.GetNames<CcMatchTarget>().Select(n => new { name = n, value = n }),
                matchOperators = Enum.GetNames<CcMatchOperator>().Select(n => new { name = n, value = n }),
                ruleTypes = Enum.GetNames<CcRuleType>().Select(n => new { name = n, value = n }),
                actions = Enum.GetNames<CcAction>().Select(n => new { name = n, value = n }),
                timestamp = DateTime.Now
            });
        }).RequireHost($"*:{controlPort}").RequireManagementPermission();

    }
}
