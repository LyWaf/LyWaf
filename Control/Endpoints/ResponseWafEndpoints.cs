using LyWaf.Control.Infrastructure;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ResponseWaf;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace LyWaf.Control.Endpoints;

/// <summary>响应内容检测、阻断和脱敏策略的控制台接口。</summary>
public static class ResponseWafEndpoints
{
    public static IEndpointRouteBuilder MapResponseWafEndpoints(this IEndpointRouteBuilder app, int controlPort)
    {
        var group = app.MapGroup("/api/response-waf").RequireHost($"*:{controlPort}");
        group.MapGet("", (IOptionsMonitor<ResponseWafOptions> options) => Results.Json(new
            { success = true, config = options.CurrentValue }));
        group.MapPut("", UpdateAsync);
        return app;
    }

    private static async Task<IResult> UpdateAsync(ResponseWafOptions request, HttpContext context,
        ControlConfigurationPatchService patches)
    {
        if (request.Action is not ("Observe" or "Block" or "Redact")) return Bad("Action 必须是 Observe、Block 或 Redact");
        if (request.MaxInspectBytes is < 1024 or > 4 * 1024 * 1024) return Bad("响应检查上限必须为 1KiB-4MiB");
        if (request.BlockStatusCode is < 400 or > 599) return Bad("阻断状态码必须为 400-599");
        if (request.Rules.Count > 500) return Bad("自定义响应规则最多 500 条");
        if (request.Rules.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Value.Length > 4096)) return Bad("规则 ID 不能为空且匹配值不能超过 4096 字符");
        if (request.Rules.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Rules.Count)
            return Bad("规则 ID 不能重复");
        if (request.Rules.Any(x => x.Field is not ("Status" or "Header" or "Body")))
            return Bad("规则字段必须是 Status、Header 或 Body");
        if (request.Rules.Any(x => x.Operator is not ("Equal" or "Contains" or "StartsWith" or "EndsWith" or "Regex")))
            return Bad("规则操作符无效");
        foreach (var rule in request.Rules.Where(x => x.Operator == "Regex"))
        {
            try { _ = new Regex(rule.Value, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { return Bad($"规则 {rule.Id} 的正则表达式无效"); }
        }
        var impact = await patches.SetSectionAsync("ResponseWaf", request, "response-waf-update", context.RequestAborted);
        context.RequestServices.GetRequiredService<IAuditLogService>().Log(
            context.Items["Username"]?.ToString() ?? "unknown", "更新响应 WAF 配置",
            context.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Json(new { success = true, impact });
    }

    private static IResult Bad(string message) => Results.Json(new { success = false, message }, statusCode: 400);
}
