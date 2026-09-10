using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using LyWaf.Services.WafRule;
using LyWaf.Services.ManagementApi;

namespace LyWaf.Control.Endpoints;

/// <summary>WAF 自定义规则枚举、查询、创建、更新、删除和启停接口。</summary>
public static class WafRuleEndpoints
{
    public static IEndpointRouteBuilder MapWafRuleEndpoints(this IEndpointRouteBuilder app, int controlPort)
    {
        var group = app.MapGroup("/api/waf-rules").RequireHost($"*:{controlPort}").RequireManagementPermission();
        group.MapGet("/enums", GetEnums).Produces<WafRuleEnumsResponse>();
        group.MapGet("", GetRules).Produces<ManagementPage<WafRuleView>>();
        group.MapGet("/{id}", GetRule).Produces<WafRuleDetailResponse>();
        group.MapPost("", CreateAsync).Produces<WafRuleSavedResponse>();
        group.MapPut("/{id}", UpdateAsync).Produces<WafRuleSavedResponse>();
        group.MapDelete("/{id}", Delete).Produces<WafRuleSavedResponse>();
        group.MapPost("/{id}/toggle", Toggle).Produces<WafRuleToggleResponse>();
        return app;
    }

    private static IResult GetEnums() => TypedResults.Ok(new WafRuleEnumsResponse(true,
        Enum.GetValues<WafMatchField>().Select(x => new WafEnumOption(x.ToString(), FieldLabel(x))).ToArray(),
        Enum.GetValues<WafMatchOperator>().Select(x => new WafEnumOption(x.ToString(), OperatorLabel(x))).ToArray(),
        Enum.GetValues<WafRuleAction>().Select(x => new WafEnumOption(x.ToString(), ActionLabel(x))).ToArray(),
        Enum.GetValues<WafRuleSource>().Select(x => new WafEnumOption(x.ToString(), SourceLabel(x))).ToArray()));

    private static IResult GetRules(HttpContext context, IWafRuleService service, WafRuleSource? source, bool? enabled)
    {
        var rules = service.GetRules().Select((rule, index) => (rule, index))
            .Where(item => (source == null || item.rule.Source == source) && (enabled == null || item.rule.Enabled == enabled));
        var result = ManagementLists.From(rules).Sort("execution", item => item.index).Sort("name", item => item.rule.Name)
            .Sort("priority", item => item.rule.Priority).Sort("createdAt", item => item.rule.CreatedAt)
            .Search(item => item.rule.Name + " " + item.rule.Description + " " + item.rule.Id)
            .Timestamp(item => item.rule.UpdatedAt ?? item.rule.CreatedAt).Read(context.Request.Query, "execution");
        return TypedResults.Ok(new ManagementPage<WafRuleView>(result.Success,
            result.Data.Select(item => WafRuleView.From(item.rule)).ToArray(), result.Total,
            result.Page, result.PageSize, result.SortBy, result.SortDirection));
    }

    private static IResult GetRule(string id, IWafRuleService service) => service.GetRule(id) is { } rule
        ? TypedResults.Ok(new WafRuleDetailResponse(true, WafRuleView.From(rule)))
        : Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);

    private static IResult CreateAsync(HttpContext context, IWafRuleService service, WafCustomRule rule)
    {
        if (rule == null || string.IsNullOrWhiteSpace(rule.Name)) return Bad("规则名称不能为空");
        if (!HasValidConditionGroups(rule)) return Bad("条件组无效：最多 5 组、总计 20 个条件，且每组至少包含一个条件");
        if (!service.AddRule(rule)) return Bad("创建失败");
        Audit(context, $"创建WAF规则: {rule.Name}");
        return TypedResults.Ok(new WafRuleSavedResponse(true, "规则已创建", rule.Id));
    }

    private static IResult UpdateAsync(string id, HttpContext context, IWafRuleService service, WafCustomRule rule)
    {
        var existing = service.GetRule(id);
        if (existing != null && existing.Source != WafRuleSource.User)
            return Results.Json(new { success = false, message = "非用户规则不可编辑" }, statusCode: 403);
        if (rule == null) return Bad("无效的规则数据");
        if (!HasValidConditionGroups(rule)) return Bad("条件组无效：最多 5 组、总计 20 个条件，且每组至少包含一个条件");
        rule.Id = id;
        if (!service.UpdateRule(rule)) return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
        Audit(context, $"更新WAF规则: {rule.Name} ({id})");
        return TypedResults.Ok(new WafRuleSavedResponse(true, "规则已更新"));
    }

    private static IResult Delete(string id, HttpContext context, IWafRuleService service)
    {
        var existing = service.GetRule(id);
        if (existing != null && existing.Source != WafRuleSource.User)
            return Results.Json(new { success = false, message = "非用户规则不可删除" }, statusCode: 403);
        if (!service.DeleteRule(id)) return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
        Audit(context, $"删除WAF规则: {existing?.Name ?? id} ({id})");
        return TypedResults.Ok(new WafRuleSavedResponse(true, "规则已删除"));
    }

    private static IResult Toggle(string id, HttpContext context, IWafRuleService service)
    {
        if (!service.ToggleRule(id)) return Results.Json(new { success = false, message = "规则不存在" }, statusCode: 404);
        var rule = service.GetRule(id);
        Audit(context, $"切换WAF规则: {rule?.Name ?? id} -> {(rule?.Enabled == true ? "启用" : "禁用")}");
        return TypedResults.Ok(new WafRuleToggleResponse(true, rule?.Enabled, rule?.Enabled == true ? "规则已启用" : "规则已禁用"));
    }

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
