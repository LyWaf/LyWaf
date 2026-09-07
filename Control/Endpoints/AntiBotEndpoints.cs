using LyWaf.Control.Infrastructure;
using LyWaf.Services.AntiBot;
using LyWaf.Services.AuditLog;
using Microsoft.Extensions.Options;

namespace LyWaf.Control.Endpoints;

/// <summary>Anti-Bot 风险策略和可信 JA4 入口的控制台接口。</summary>
public static class AntiBotEndpoints
{
    public static IEndpointRouteBuilder MapAntiBotEndpoints(this IEndpointRouteBuilder app, int controlPort)
    {
        var group = app.MapGroup("/api/anti-bot").RequireHost($"*:{controlPort}");
        group.MapGet("", (IOptionsMonitor<AntiBotOptions> options) =>
        {
            var x = options.CurrentValue;
            return Results.Json(new { success = true, config = new
            {
                x.Enabled, x.Mode, x.ChallengeScore, x.BlockScore, x.ChallengePath, x.CookieName,
                secretConfigured = !string.IsNullOrWhiteSpace(x.Secret), x.ChallengeTokenSeconds,
                x.SessionMinutes, x.ProofLeadingZeroNibbles, x.BypassPaths, x.BypassIps,
                x.Ja4Header, x.TrustedFingerprintProxies,
            }});
        });
        group.MapPut("", UpdateAsync);
        return app;
    }

    private static async Task<IResult> UpdateAsync(AntiBotUpdateRequest request, HttpContext context,
        IOptionsMonitor<AntiBotOptions> current, ControlConfigurationPatchService patches)
    {
        if (request.Mode is not ("Observe" or "Challenge" or "Block")) return Bad("Mode 必须是 Observe、Challenge 或 Block");
        if (request.ChallengeScore is < 0 or > 100 || request.BlockScore is < 0 or > 100) return Bad("风险阈值必须为 0-100");
        if (request.ProofLeadingZeroNibbles is < 1 or > 6) return Bad("挑战难度必须为 1-6");
        if (request.SessionMinutes is < 1 or > 10080) return Bad("会话时间必须为 1-10080 分钟");
        if (!request.ChallengePath.StartsWith('/') || request.ChallengePath.StartsWith("//")) return Bad("挑战路径必须是站内绝对路径");
        if (request.Ja4Header.Length is < 1 or > 128 || request.Ja4Header.Any(c => char.IsControl(c) || c is ':' or ' '))
            return Bad("JA4 Header 名称无效");
        if (request.Secret is { Length: > 0 and < 32 }) return Bad("新会话密钥至少需要 32 个字符");
        var existing = current.CurrentValue;
        var next = new AntiBotOptions
        {
            Enabled = request.Enabled, Mode = request.Mode, ChallengeScore = request.ChallengeScore,
            BlockScore = request.BlockScore, ChallengePath = request.ChallengePath,
            CookieName = request.CookieName, Secret = string.IsNullOrWhiteSpace(request.Secret) ? existing.Secret : request.Secret,
            ChallengeTokenSeconds = request.ChallengeTokenSeconds, SessionMinutes = request.SessionMinutes,
            ProofLeadingZeroNibbles = request.ProofLeadingZeroNibbles, BypassPaths = request.BypassPaths,
            BypassIps = request.BypassIps, Ja4Header = request.Ja4Header,
            TrustedFingerprintProxies = request.TrustedFingerprintProxies,
        };
        var impact = await patches.SetSectionAsync("AntiBot", next, "anti-bot-update", context.RequestAborted);
        Audit(context, "更新 Anti-Bot 配置");
        return Results.Json(new { success = true, impact });
    }

    private static IResult Bad(string message) => Results.Json(new { success = false, message }, statusCode: 400);
    private static void Audit(HttpContext context, string action) => context.RequestServices.GetRequiredService<IAuditLogService>()
        .Log(context.Items["Username"]?.ToString() ?? "unknown", action, context.Connection.RemoteIpAddress?.ToString() ?? "");
}

public sealed class AntiBotUpdateRequest
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "Observe";
    public int ChallengeScore { get; set; } = 60;
    public int BlockScore { get; set; } = 100;
    public string ChallengePath { get; set; } = "/__lywaf/antibot/verify";
    public string CookieName { get; set; } = "__lywaf_ab";
    public string? Secret { get; set; }
    public int ChallengeTokenSeconds { get; set; } = 120;
    public int SessionMinutes { get; set; } = 30;
    public int ProofLeadingZeroNibbles { get; set; } = 3;
    public List<string> BypassPaths { get; set; } = [];
    public List<string> BypassIps { get; set; } = [];
    public string Ja4Header { get; set; } = "X-LyWaf-JA4";
    public List<string> TrustedFingerprintProxies { get; set; } = [];
}
