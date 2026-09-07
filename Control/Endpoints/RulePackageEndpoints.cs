using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.AuditLog;
using LyWaf.Services.WafRule;

namespace LyWaf.Control.Endpoints;

/// <summary>安全规则包的安装、发布、回滚、解释和反馈管理接口。</summary>
public static class RulePackageEndpoints
{
    public static IEndpointRouteBuilder MapRulePackageEndpoints(this IEndpointRouteBuilder app, int controlPort)
    {
        var group = app.MapGroup("/api/rule-packages").RequireHost($"*:{controlPort}");

        group.MapGet("", (IRulePackageService packages) => Results.Json(new
        {
            success = true, state = packages.GetState(), packages = packages.List(),
        }));

        group.MapPost("/install", InstallAsync);
        group.MapPost("/download", DownloadAsync);
        group.MapPost("/{version}/activate", Activate);
        group.MapPost("/rollback", Rollback);
        group.MapGet("/{version}/rules/{ruleId}", Explain);
        group.MapPost("/feedback", SubmitFeedbackAsync);
        return app;
    }

    private static async Task<IResult> InstallAsync(HttpContext context, IRulePackageService packages)
    {
        try
        {
            var installed = await packages.InstallAsync(context.Request.Body, context.RequestAborted);
            Audit(context, $"安装安全规则包: {installed.PackageId} {installed.Version}");
            return Results.Json(new { success = true, package = installed });
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or CryptographicException)
        {
            return Results.Json(new { success = false, message = ex.Message }, statusCode: 400);
        }
    }

    private static async Task<IResult> DownloadAsync(RulePackageDownloadRequest request,
        HttpContext context, IRulePackageService packages)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            return Results.Json(new { success = false, message = "规则包 URL 无效" }, statusCode: 400);
        try
        {
            var installed = await packages.DownloadAndInstallAsync(uri, context.RequestAborted);
            Audit(context, $"下载安全规则包: {installed.PackageId} {installed.Version}");
            return Results.Json(new { success = true, package = installed });
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or TaskCanceledException)
        {
            return Results.Json(new { success = false,
                message = ex is TaskCanceledException ? "规则包下载超时" : ex.Message }, statusCode: 400);
        }
    }

    private static IResult Activate(string version, RulePackageActivateRequest request,
        HttpContext context, IRulePackageService packages)
    {
        try
        {
            packages.Activate(version, request.CanaryPercentage);
            Audit(context, $"激活安全规则包: {version}, 灰度 {request.CanaryPercentage}%");
            return Results.Json(new { success = true, state = packages.GetState() });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentOutOfRangeException)
        {
            return Results.Json(new { success = false, message = ex.Message }, statusCode: 400);
        }
    }

    private static IResult Rollback(HttpContext context, IRulePackageService packages)
    {
        try
        {
            packages.Rollback();
            Audit(context, $"回滚安全规则包至: {packages.GetState().StableVersion}");
            return Results.Json(new { success = true, state = packages.GetState() });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { success = false, message = ex.Message }, statusCode: 409);
        }
    }

    private static IResult Explain(string version, string ruleId, IRulePackageService packages) =>
        packages.Explain(version, ruleId) is { } explanation
            ? Results.Json(new { success = true, rule = explanation })
            : Results.Json(new { success = false, message = "规则说明不存在" }, statusCode: 404);

    private static async Task<IResult> SubmitFeedbackAsync(RuleFeedback feedback,
        HttpContext context, IRulePackageService packages)
    {
        await packages.SubmitFeedbackAsync(feedback with { CreatedAt = DateTimeOffset.UtcNow }, context.RequestAborted);
        Audit(context, $"提交规则反馈: {feedback.PackageId}/{feedback.Version}/{feedback.RuleId}");
        return Results.Json(new { success = true });
    }

    private static void Audit(HttpContext context, string action) =>
        context.RequestServices.GetRequiredService<IAuditLogService>().Log(
            context.Items["Username"]?.ToString() ?? "unknown", action,
            context.Connection.RemoteIpAddress?.ToString() ?? "");
}

public sealed class RulePackageDownloadRequest
{
    public string Url { get; set; } = "";
}

public sealed class RulePackageActivateRequest
{
    public int CanaryPercentage { get; set; }
}
