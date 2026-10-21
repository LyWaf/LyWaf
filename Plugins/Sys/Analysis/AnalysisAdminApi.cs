using System.Text.Json;
using LyWaf.Services.Statistic;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.Auth;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Options;
using System.Net;
using Yarp.ReverseProxy.Configuration;

namespace LyWaf.Plugins.Sys.Analysis;

/// <summary>流量分析插件的 CC 规则与统计配置 API，仅通过宿主插件代理调用。</summary>
public static class AnalysisAdminApi
{
    public static async Task<IResult> HandleAsync(string path, HttpContext ctx)
    {
        path = path.Trim('/');
        if (HttpMethods.IsPost(ctx.Request.Method) && path is "cc/release" or "cc/release/status")
        {
            if (ctx.Items["ManagementScopes"] is not IEnumerable<string> scopes
                || !scopes.Any(s => s is ManagementPermissions.Write or "admin"))
                return ReleaseError(403, "forbidden");
            return await ReadBodyAsync<ReleaseCcRequest>(ctx, request => ReleaseCc(ctx, request, path == "cc/release/status"));
        }
        if (HttpMethods.IsGet(ctx.Request.Method) && path == "ban-logs")
        {
            var tracker = ctx.RequestServices.GetService<CcPropagationTracker>();
            var confirmations = tracker?.Capture();
            var decisions = ctx.RequestServices.GetService<CcDecisionService>();
            var statistics = ctx.RequestServices.GetService<IStatisticService>();
            var logs = CcBanLog.Shared.Read().Select(log =>
            {
                if (log.DecisionId is null) return log;
                var propagation = confirmations?.Invoke(m => m.Domain == CcDecisionService.DecisionDomain && !m.Deleted
                    && m.Value?.Deserialize<CcDecision>(new JsonSerializerOptions(JsonSerializerDefaults.Web))?.DecisionId == log.DecisionId);
                var rule = log.Policy is null ? null : statistics?.GetAdvancedCcRule(log.Policy.RuleId);
                return log with { PropagationStatus = propagation?.Status ?? "pending", Propagation = propagation,
                    Execution = log.Policy is null ? null : decisions?.InspectExecution(log.Policy, log.DecisionId),
                    Coverage = rule is null ? null : decisions?.Coverage(rule, log.Policy),
                    ExecutionPropagation = log.Policy is null ? null : decisions?.InspectClusterExecution(log.Policy, log.DecisionId) };
            }).ToArray();
            return Results.Ok(new { success = true, logs });
        }

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

    private static IResult ReleaseError(int status, string code) => Results.Json(new
    { success = false, errorCode = "cc_release_" + code, messageKey = "cc.release." + code }, statusCode: status);

    private static IResult ReleaseCc(HttpContext ctx, ReleaseCcRequest request, bool statusOnly = false)
    {
        var rule = ctx.RequestServices.GetRequiredService<IStatisticService>().GetAdvancedCcRule(request.RuleId);
        if (rule is null) return ReleaseError(404, "ruleMissing");
        if (!IPAddress.TryParse(request.Ip, out var ip) || ip.ToString() != request.Ip)
            return ReleaseError(400, "invalidIp");
        var scope = request.Scope.Trim();
        if (rule.Subject != CcSubjectKind.Ip) return ReleaseError(409, "scopeMismatch");
        if (rule.Scope == CcScope.Node)
        {
            if (request.ScopeType != "node" || scope != ctx.RequestServices.GetRequiredService<IRuntimeStateStore>().GetStatus().NodeId)
                return ReleaseError(409, "scopeMismatch");
        }
        else if (rule.Scope == CcScope.Cluster)
        {
            if (request.ScopeType != "cluster" || scope != "shared") return ReleaseError(409, "scopeMismatch");
        }
        else if (!string.IsNullOrWhiteSpace(rule.ProtectionGroup))
        {
            if (request.ScopeType != "group" || scope != rule.ProtectionGroup.Trim())
                return ReleaseError(409, "scopeMismatch");
        }
        else if (request.ScopeType == "group") return ReleaseError(409, "scopeMismatch");
        else if (request.ScopeType == "host")
        {
            scope = scope.TrimEnd('.').ToLowerInvariant();
            if (Uri.CheckHostName(scope) == UriHostNameType.Unknown) return ReleaseError(400, "scopeMismatch");
        }
        else if (request.ScopeType != "route" || ctx.RequestServices.GetService<IProxyConfigProvider>()?.GetConfig().Routes
            .Any(r => r.RouteId == scope) != true) return ReleaseError(400, "scopeMismatch");

        var options = ctx.RequestServices.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
        if (statusOnly)
        {
            if (request.PolicyGeneration is not { Version: > 0 } generation
                || !Guid.TryParseExact(generation.ReleaseId, "N", out _)) return ReleaseError(400, "scopeMismatch");
            var key = CcRuleStateKey.Create(rule, request.Ip, request.ScopeType + ":" + scope);
            var propagation = ctx.RequestServices.GetRequiredService<CcPropagationTracker>().Read(m =>
                m.Domain == CcPolicyGeneration.Domain && m.Key == key && !m.Deleted
                && m.Value?.Deserialize<CcPolicyGeneration>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) == generation);
            var execution = ctx.RequestServices.GetService<CcDecisionService>()?.InspectRelease(rule,
                request.ScopeType + ":" + scope, request.Ip, generation);
            return Results.Json(new { success = true, policyGeneration = generation, propagation, execution });
        }
        if (options.Enabled && string.IsNullOrWhiteSpace(options.CcReleasePrivateKeyPem))
            return ReleaseError(503, "authorityUnavailable");
        try
        {
            var generation = ctx.RequestServices.GetRequiredService<ICcRuleChecker>()
                .ReleaseRule(request.ScopeType + ":" + scope, rule, request.Ip);
            ctx.RequestServices.GetRequiredService<IAuditLogService>().Log(
                ctx.Items["Username"]?.ToString() ?? "unknown",
                $"CC release rule={rule.Id} scope={request.ScopeType}:{scope} ip={request.Ip} generation={generation.Version}/{generation.ReleaseId}",
                ctx.Connection.RemoteIpAddress?.ToString() ?? "");
            return Results.Json(new
            {
                success = true, ruleId = rule.Id, scopeType = request.ScopeType, scope, ip = request.Ip,
                policyGeneration = generation, localApplied = true,
                propagation = new { status = "pending", replicationEnabled = options.Enabled,
                    acknowledgedNodes = Array.Empty<string>(), allNodesConfirmed = false },
                messageKey = "cc.release.pending"
            }, statusCode: StatusCodes.Status202Accepted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { return ReleaseError(503, "authorityUnavailable"); }
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
            protectionGroup = r.ProtectionGroup,
            scope = r.Scope.ToString(),
            subject = r.Subject.ToString(),
            subjectAuthenticationType = r.SubjectAuthenticationType,
            subjectIssuer = r.SubjectIssuer,
            expectedNodes = r.ExpectedNodes,
            useManagedMembership = r.UseManagedMembership,
            requireCompleteCoverage = r.RequireCompleteCoverage,
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
            protectionGroup = r.ProtectionGroup,
            scope = r.Scope.ToString(),
            subject = r.Subject.ToString(),
            subjectAuthenticationType = r.SubjectAuthenticationType,
            subjectIssuer = r.SubjectIssuer,
            expectedNodes = r.ExpectedNodes,
            useManagedMembership = r.UseManagedMembership,
            requireCompleteCoverage = r.RequireCompleteCoverage,
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
                protectionGroup = rule.ProtectionGroup,
                scope = rule.Scope.ToString(),
                subject = rule.Subject.ToString(),
                subjectAuthenticationType = rule.SubjectAuthenticationType,
                subjectIssuer = rule.SubjectIssuer,
                expectedNodes = rule.ExpectedNodes,
                useManagedMembership = rule.UseManagedMembership,
                requireCompleteCoverage = rule.RequireCompleteCoverage,
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
            ProtectionGroup = request.ProtectionGroup?.Trim() ?? "",
            Scope = Enum.TryParse<CcScope>(request.Scope, true, out var scope) ? scope : CcScope.SiteGroup,
            Subject = Enum.TryParse<CcSubjectKind>(request.Subject, true, out var subject) ? subject : CcSubjectKind.Ip,
            SubjectAuthenticationType = request.SubjectAuthenticationType ?? "",
            SubjectIssuer = request.SubjectIssuer ?? "",
            ExpectedNodes = request.ExpectedNodes ?? [],
            UseManagedMembership = request.UseManagedMembership ?? false,
            RequireCompleteCoverage = request.RequireCompleteCoverage ?? request.UseManagedMembership ?? false,
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

        if (CcRequestIdentity.ValidConfiguration(rule) && statisticService.AddAdvancedCcRule(rule))
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
            ProtectionGroup = request.ProtectionGroup?.Trim() ?? existingRule.ProtectionGroup,
            Scope = Enum.TryParse<CcScope>(request.Scope, true, out var scope) ? scope : existingRule.Scope,
            Subject = Enum.TryParse<CcSubjectKind>(request.Subject, true, out var subject) ? subject : existingRule.Subject,
            SubjectAuthenticationType = request.SubjectAuthenticationType ?? existingRule.SubjectAuthenticationType,
            SubjectIssuer = request.SubjectIssuer ?? existingRule.SubjectIssuer,
            ExpectedNodes = request.ExpectedNodes ?? (request.UseManagedMembership == true ? [] : existingRule.ExpectedNodes),
            UseManagedMembership = request.UseManagedMembership ?? existingRule.UseManagedMembership,
            RequireCompleteCoverage = request.RequireCompleteCoverage ?? (request.UseManagedMembership == true || existingRule.RequireCompleteCoverage),
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

        if (CcRequestIdentity.ValidConfiguration(rule) && statisticService.UpdateAdvancedCcRule(rule))
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

