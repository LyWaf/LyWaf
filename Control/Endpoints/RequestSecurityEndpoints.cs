using LyWaf.Control.Infrastructure;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Protect;
using Microsoft.Extensions.Options;

namespace LyWaf.Control.Endpoints;

/// <summary>统一请求规范化、协议异常和语义漏洞检测的控制台接口。</summary>
public static class RequestSecurityEndpoints
{
    public static IEndpointRouteBuilder MapRequestSecurityEndpoints(this IEndpointRouteBuilder app, int controlPort)
    {
        var group = app.MapGroup("/api/request-security").RequireHost($"*:{controlPort}");
        group.MapGet("", (IOptionsMonitor<ProtectOptions> options) =>
        {
            var x = options.CurrentValue;
            return Results.Json(new { success = true, config = new
            {
                x.InspectionMaxBodyBytes, x.InspectionMaxValueLength, x.InspectionMaxCandidates,
                x.InspectionDecodeDepth, x.RejectProtocolAnomalies,
                x.SemanticDetectionEnabled, x.SemanticDetectionAction,
            }});
        });
        group.MapPut("", UpdateAsync);
        return app;
    }

    private static async Task<IResult> UpdateAsync(RequestSecurityUpdateRequest request, HttpContext context,
        ControlConfigurationPatchService patches)
    {
        if (request.InspectionMaxBodyBytes is < 1024 or > 16 * 1024 * 1024) return Bad("Body 检查上限必须为 1KiB-16MiB");
        if (request.InspectionMaxValueLength is < 256 or > 1024 * 1024) return Bad("单值长度必须为 256B-1MiB");
        if (request.InspectionMaxCandidates is < 1 or > 2048) return Bad("候选值数量必须为 1-2048");
        if (request.InspectionDecodeDepth is < 0 or > 5) return Bad("递归解码深度必须为 0-5");
        if (request.SemanticDetectionAction is not ("Observe" or "Reject")) return Bad("语义检测动作必须为 Observe 或 Reject");
        var values = new Dictionary<string, object?>
        {
            [nameof(ProtectOptions.InspectionMaxBodyBytes)] = request.InspectionMaxBodyBytes,
            [nameof(ProtectOptions.InspectionMaxValueLength)] = request.InspectionMaxValueLength,
            [nameof(ProtectOptions.InspectionMaxCandidates)] = request.InspectionMaxCandidates,
            [nameof(ProtectOptions.InspectionDecodeDepth)] = request.InspectionDecodeDepth,
            [nameof(ProtectOptions.RejectProtocolAnomalies)] = request.RejectProtocolAnomalies,
            [nameof(ProtectOptions.SemanticDetectionEnabled)] = request.SemanticDetectionEnabled,
            [nameof(ProtectOptions.SemanticDetectionAction)] = request.SemanticDetectionAction,
        };
        var impact = await patches.MergePathAsync(["Protect"], values, "request-security-update", context.RequestAborted);
        context.RequestServices.GetRequiredService<IAuditLogService>().Log(
            context.Items["Username"]?.ToString() ?? "unknown", "更新请求安全检测配置",
            context.Connection.RemoteIpAddress?.ToString() ?? "");
        return Results.Json(new { success = true, impact });
    }

    private static IResult Bad(string message) => Results.Json(new { success = false, message }, statusCode: 400);
}

public sealed class RequestSecurityUpdateRequest
{
    public int InspectionMaxBodyBytes { get; set; } = 1024 * 1024;
    public int InspectionMaxValueLength { get; set; } = 64 * 1024;
    public int InspectionMaxCandidates { get; set; } = 128;
    public int InspectionDecodeDepth { get; set; } = 3;
    public bool RejectProtocolAnomalies { get; set; } = true;
    public bool SemanticDetectionEnabled { get; set; } = true;
    public string SemanticDetectionAction { get; set; } = "Observe";
}
