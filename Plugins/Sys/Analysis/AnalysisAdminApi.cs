using System.Text.Json;
using LyWaf.Services.Statistic;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Plugins.Sys.Analysis;

/// <summary>流量分析插件的 CC 规则与统计配置 API，仅通过宿主插件代理调用。</summary>
public static class AnalysisAdminApi
{
    public static async Task<IResult> HandleAsync(string path, HttpContext ctx)
    {
        path = path.Trim('/');
        if (HttpMethods.IsGet(ctx.Request.Method) && path == "ban-logs")
            return Results.Ok(new { success = true, logs = CcBanLog.Shared.Read() });

        var statisticService = ctx.RequestServices.GetRequiredService<IStatisticService>();
        switch ((ctx.Request.Method, path))
        {
            case ("GET", "statistic/config"): return GetConfig(ctx, statisticService);
            case ("POST", "statistic/white-paths/add"): return await ReadBodyAsync<PathRequest>(ctx, request => AddWhitePath(ctx, statisticService, request));
            case ("POST", "statistic/white-paths/remove"): return await ReadBodyAsync<PathRequest>(ctx, request => RemoveWhitePath(ctx, statisticService, request));
            case ("POST", "statistic/path-stas/add"): return await ReadBodyAsync<PathRequest>(ctx, request => AddPathSta(ctx, statisticService, request));
            case ("POST", "statistic/path-stas/remove"): return await ReadBodyAsync<PathRequest>(ctx, request => RemovePathSta(ctx, statisticService, request));
            case ("GET", "cc/rules"): return GetRules(ctx, statisticService);
            case ("POST", "cc/rules/add"): return await ReadBodyAsync<AddCcRuleRequest>(ctx, request => AddRule(ctx, statisticService, request));
            case ("POST", "cc/rules/remove"): return await ReadBodyAsync<RemoveCcRuleRequest>(ctx, request => RemoveRule(ctx, statisticService, request));
            case ("GET", "cc/advanced"): return GetAdvancedRules(ctx, statisticService);
            case ("POST", "cc/advanced/add"): return await ReadBodyAsync<AddAdvancedCcRuleRequest>(ctx, request => AddAdvancedRule(ctx, statisticService, request));
            case ("POST", "cc/advanced/update"): return await ReadBodyAsync<UpdateAdvancedCcRuleRequest>(ctx, request => UpdateAdvancedRule(ctx, statisticService, request));
            case ("POST", "cc/advanced/remove"): return await ReadBodyAsync<RemoveAdvancedCcRuleRequest>(ctx, request => RemoveAdvancedRule(ctx, statisticService, request));
            case ("POST", "cc/advanced/toggle"): return await ReadBodyAsync<ToggleAdvancedCcRuleRequest>(ctx, request => ToggleAdvancedRule(ctx, statisticService, request));
            case ("GET", "cc/advanced/enums"): return GetEnums(ctx);
        }
        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            var segments = path.Split('/');
            if (segments is ["cc", "advanced", "type", var type])
                return GetAdvancedRulesByType(ctx, statisticService, type);
            if (segments is ["cc", "advanced", var ruleId])
                return GetAdvancedRule(ctx, statisticService, ruleId);
        }
        return Results.NotFound();
    }

    private static async Task<IResult> ReadBodyAsync<T>(HttpContext ctx, Func<T, IResult> handle) where T : class
    {
        if (!ctx.Request.HasJsonContentType())
            return ManagementApiContract.Error(ctx, 415, "unsupported_media_type", "请求体必须为 JSON");
        T? request;
        try { request = await ctx.Request.ReadFromJsonAsync<T>(ctx.RequestAborted); }
        catch (JsonException)
        {
            return ManagementApiContract.Error(ctx, 400, "validation_failed", "JSON 格式无效");
        }
        var errors = ManagementBodyValidation.ValidateBody(request);
        if (errors.Count != 0)
            return ManagementApiContract.Error(ctx, 400, "validation_failed", "请求字段校验失败", errors);
        return handle(request!);
    }

    private static IResult GetConfig(HttpContext ctx, IStatisticService statisticService)
    {
        return TypedResults.Ok(new
        {
            success = true,
            whitePaths = statisticService.GetWhitePaths(),
            pathStas = statisticService.GetPathStas(),
        });
    }

    private static IResult AddWhitePath(HttpContext ctx, IStatisticService statisticService, PathRequest request)
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
    }

    private static IResult RemoveWhitePath(HttpContext ctx, IStatisticService statisticService, PathRequest request)
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
    }

    private static IResult AddPathSta(HttpContext ctx, IStatisticService statisticService, PathRequest request)
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
    }

    private static IResult RemovePathSta(HttpContext ctx, IStatisticService statisticService, PathRequest request)
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
    }

    private static IResult GetRules(HttpContext ctx, IStatisticService statisticService)
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
    }

    private static IResult AddRule(HttpContext ctx, IStatisticService statisticService, AddCcRuleRequest request)
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
    }

    private static IResult RemoveRule(HttpContext ctx, IStatisticService statisticService, RemoveCcRuleRequest request)
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
    }

    private static IResult GetAdvancedRules(HttpContext ctx, IStatisticService statisticService)
    {
        var rules = statisticService.GetAdvancedCcRules();
        var values = rules.Select(r => new
        {
            id = r.Id,
            name = r.Name,
            enabled = r.Enabled,
            type = r.Type.ToString(),
            groupOperator = r.GroupOperator,
            conditionGroups = r.ConditionGroups,
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
    }

    private static IResult GetAdvancedRulesByType(HttpContext ctx, IStatisticService statisticService, string type)
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
            groupOperator = r.GroupOperator,
            conditionGroups = r.ConditionGroups,
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
    }

    private static IResult GetAdvancedRule(HttpContext ctx, IStatisticService statisticService, string ruleId)
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
                groupOperator = rule.GroupOperator,
                conditionGroups = rule.ConditionGroups,
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
    }

    private static IResult AddAdvancedRule(HttpContext ctx, IStatisticService statisticService, AddAdvancedCcRuleRequest request)
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
            GroupOperator = request.GroupOperator ?? "Or",
            ConditionGroups = request.ConditionGroups,
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
    }

    private static IResult UpdateAdvancedRule(HttpContext ctx, IStatisticService statisticService, UpdateAdvancedCcRuleRequest request)
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
            GroupOperator = request.GroupOperator ?? existingRule.GroupOperator,
            ConditionGroups = request.ConditionGroups ?? (request.Conditions != null ? null : existingRule.ConditionGroups),
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
    }

    private static IResult RemoveAdvancedRule(HttpContext ctx, IStatisticService statisticService, RemoveAdvancedCcRuleRequest request)
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
    }

    private static IResult ToggleAdvancedRule(HttpContext ctx, IStatisticService statisticService, ToggleAdvancedCcRuleRequest request)
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
    }

    private static IResult GetEnums(HttpContext ctx)
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
    }

}

