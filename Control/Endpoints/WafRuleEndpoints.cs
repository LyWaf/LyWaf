using LyWaf.Services.AuditLog;
using LyWaf.Services.WafRule;

namespace LyWaf.Control.Endpoints;

/// <summary>WAF 自定义规则枚举、查询、创建、更新、删除和启停接口。</summary>
public static class WafRuleEndpoints
{
    public static IEndpointRouteBuilder MapWafRuleEndpoints(this IEndpointRouteBuilder app, int controlPort)
    {
        var group = app.MapGroup("/api/waf-rules").RequireHost($"*:{controlPort}");
        group.MapGet("/enums", GetEnums);
        group.MapGet("", GetRules);
        group.MapGet("/{id}", GetRule);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", Delete);
        group.MapPost("/{id}/toggle", Toggle);
        return app;
    }

    private static IResult GetEnums() => Results.Json(new
    {
        success = true,
        fields = Enum.GetValues<WafMatchField>().Select(x => new { name = x.ToString(), label = FieldLabel(x) }),
        operators = Enum.GetValues<WafMatchOperator>().Select(x => new { name = x.ToString(), label = OperatorLabel(x) }),
        actions = Enum.GetValues<WafRuleAction>().Select(x => new { name = x.ToString(), label = ActionLabel(x) }),
        sources = Enum.GetValues<WafRuleSource>().Select(x => new { name = x.ToString(), label = SourceLabel(x) }),
    });

    private static IResult GetRules(IWafRuleService service)
    {
        var rules = service.GetRules();
        return Results.Json(new { success = true, count = rules.Count, rules = rules.Select(ToResponse) });
    }

    private static IResult GetRule(string id, IWafRuleService service) => service.GetRule(id) is { } rule
        ? Results.Json(new { success = true, rule = ToResponse(rule) })
        : Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);

    private static async Task<IResult> CreateAsync(HttpContext context, IWafRuleService service)
    {
        try
        {
            var rule = await context.Request.ReadFromJsonAsync<WafCustomRule>(context.RequestAborted);
            if (rule == null || string.IsNullOrWhiteSpace(rule.Name)) return Bad("规则名称不能为空");
            if (!HasValidConditionGroups(rule)) return Bad("条件组无效：最多 5 组、总计 20 个条件，且每组至少包含一个条件");
            if (!service.AddRule(rule)) return Bad("创建失败");
            Audit(context, $"创建WAF规则: {rule.Name}");
            return Results.Json(new { success = true, message = "规则已创建", id = rule.Id });
        }
        catch (Exception ex) { return Results.Json(new { success = false, message = $"创建失败: {ex.Message}" }, statusCode: 500); }
    }

    private static async Task<IResult> UpdateAsync(string id, HttpContext context, IWafRuleService service)
    {
        try
        {
            var existing = service.GetRule(id);
            if (existing != null && existing.Source != WafRuleSource.User)
                return Results.Json(new { success = false, message = "非用户规则不可编辑" }, statusCode: 403);
            var rule = await context.Request.ReadFromJsonAsync<WafCustomRule>(context.RequestAborted);
            if (rule == null) return Bad("无效的规则数据");
            if (!HasValidConditionGroups(rule)) return Bad("条件组无效：最多 5 组、总计 20 个条件，且每组至少包含一个条件");
            rule.Id = id;
            if (!service.UpdateRule(rule)) return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
            Audit(context, $"更新WAF规则: {rule.Name} ({id})");
            return Results.Json(new { success = true, message = "规则已更新" });
        }
        catch (Exception ex) { return Results.Json(new { success = false, message = $"更新失败: {ex.Message}" }, statusCode: 500); }
    }

    private static IResult Delete(string id, HttpContext context, IWafRuleService service)
    {
        var existing = service.GetRule(id);
        if (existing != null && existing.Source != WafRuleSource.User)
            return Results.Json(new { success = false, message = "非用户规则不可删除" }, statusCode: 403);
        if (!service.DeleteRule(id)) return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
        Audit(context, $"删除WAF规则: {existing?.Name ?? id} ({id})");
        return Results.Json(new { success = true, message = "规则已删除" });
    }

    private static IResult Toggle(string id, HttpContext context, IWafRuleService service)
    {
        if (!service.ToggleRule(id)) return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
        var rule = service.GetRule(id);
        Audit(context, $"切换WAF规则: {rule?.Name ?? id} -> {(rule?.Enabled == true ? "启用" : "禁用")}");
        return Results.Json(new { success = true, enabled = rule?.Enabled, message = rule?.Enabled == true ? "规则已启用" : "规则已禁用" });
    }

    private static object ToResponse(WafCustomRule rule) => new
    {
        id = rule.Id, name = rule.Name, description = rule.Description, enabled = rule.Enabled,
        priority = rule.Priority, source = rule.Source.ToString(),
        groupOperator = rule.GroupOperator.ToString(),
        conditionGroups = (rule.ConditionGroups ?? []).Select(group => new
        {
            @operator = group.Operator.ToString(),
            conditions = (group.Conditions ?? []).Select(x => new { field = x.Field.ToString(), x.FieldName,
                @operator = x.Operator.ToString(), x.Value, x.IgnoreCase })
        }),
        action = rule.Action.ToString(), rule.ActionSeconds, rule.ResponseCode, rule.CreatedAt, rule.UpdatedAt,
    };

    private static IResult Bad(string message) => Results.Json(new { success = false, message }, statusCode: 400);
    private static bool HasValidConditionGroups(WafCustomRule rule) =>
        rule.ConditionGroups is { Count: > 0 and <= 5 } &&
        rule.ConditionGroups.All(group => group.Conditions is { Count: > 0 }) &&
        rule.ConditionGroups.Sum(group => group.Conditions?.Count ?? 0) <= 20;

    private static void Audit(HttpContext context, string action) => context.RequestServices.GetRequiredService<IAuditLogService>()
        .Log(context.Items["Username"]?.ToString() ?? "unknown", action, context.Connection.RemoteIpAddress?.ToString() ?? "");

    private static string FieldLabel(WafMatchField field) => field switch
    {
        WafMatchField.UriPath => "URL 路径", WafMatchField.FullUrl => "完整 URL", WafMatchField.QueryString => "查询字符串",
        WafMatchField.Method => "HTTP 方法", WafMatchField.ClientIp => "客户端 IP", WafMatchField.XForwardedFor => "X-Forwarded-For",
        WafMatchField.UserAgent => "User-Agent", WafMatchField.Referer => "Referer", WafMatchField.ContentType => "Content-Type",
        WafMatchField.ContentLength => "Content-Length", WafMatchField.Cookie => "Cookie", WafMatchField.Header => "请求头",
        WafMatchField.QueryParam => "查询参数", WafMatchField.Body => "请求体", WafMatchField.ServerPort => "服务端口", _ => field.ToString(),
    };
    private static string OperatorLabel(WafMatchOperator op) => op switch
    {
        WafMatchOperator.Equal => "等于", WafMatchOperator.NotEqual => "不等于", WafMatchOperator.Contains => "包含",
        WafMatchOperator.NotContains => "不包含", WafMatchOperator.StartsWith => "前缀匹配", WafMatchOperator.EndsWith => "后缀匹配",
        WafMatchOperator.Regex => "正则匹配", WafMatchOperator.Exists => "存在", WafMatchOperator.NotExists => "不存在",
        WafMatchOperator.LengthGreaterThan => "长度大于", WafMatchOperator.LengthLessThan => "长度小于", _ => op.ToString(),
    };
    private static string ActionLabel(WafRuleAction action) => action switch
    { WafRuleAction.Observe => "观察", WafRuleAction.Block => "封禁 IP", WafRuleAction.Reject => "拦截", WafRuleAction.Captcha => "人机验证", _ => action.ToString() };
    private static string SourceLabel(WafRuleSource source) => source switch
    { WafRuleSource.User => "用户规则", WafRuleSource.Config => "配置规则", WafRuleSource.System => "系统规则", _ => source.ToString() };
}
