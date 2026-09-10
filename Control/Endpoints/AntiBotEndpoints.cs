using LyWaf.Services.Auth;
using LyWaf.Control.Infrastructure;
using LyWaf.Services.AntiBot;
using LyWaf.Services.AuditLog;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace LyWaf.Control.Endpoints;

/// <summary>Anti-Bot 风险策略和可信 JA4 入口的控制台接口。</summary>
public static class AntiBotEndpoints
{
    public static IEndpointRouteBuilder MapAntiBotEndpoints(this IEndpointRouteBuilder app, int controlPort)
    {
        var group = app.MapGroup("/api/anti-bot").RequireHost($"*:{controlPort}").RequireManagementPermission();
        group.MapGet("", (IOptionsMonitor<AntiBotOptions> options) =>
        {
            var x = options.CurrentValue;
            return TypedResults.Ok(new { success = true, config = new
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

public sealed class AntiBotUpdateRequest : IValidatableObject
{
    public bool Enabled { get; set; }
    [Required, RegularExpression("^(Observe|Challenge|Block)$")]
    public string Mode { get; set; } = "Observe";
    [Range(0, 100)]
    public int ChallengeScore { get; set; } = 60;
    [Range(0, 100)]
    public int BlockScore { get; set; } = 100;
    [Required, StringLength(8192), RegularExpression(@"^/(?!/)[^\r\n\\]*$")]
    public string ChallengePath { get; set; } = "/__lywaf/antibot/verify";
    [Required, StringLength(128), RegularExpression("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    public string CookieName { get; set; } = "__lywaf_ab";
    [StringLength(4096)]
    public string? Secret { get; set; }
    [Range(1, 3600)]
    public int ChallengeTokenSeconds { get; set; } = 120;
    [Range(1, 10080)]
    public int SessionMinutes { get; set; } = 30;
    [Range(1, 6)]
    public int ProofLeadingZeroNibbles { get; set; } = 3;
    [Required, MaxLength(1000)]
    public List<string> BypassPaths { get; set; } = [];
    [Required, MaxLength(1000)]
    public List<string> BypassIps { get; set; } = [];
    [Required, StringLength(128), RegularExpression("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    public string Ja4Header { get; set; } = "X-LyWaf-JA4";
    [Required, MaxLength(1000)]
    public List<string> TrustedFingerprintProxies { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!string.IsNullOrWhiteSpace(Secret) && Secret.Length < 32)
            yield return new ValidationResult("Secret must contain at least 32 characters", [nameof(Secret)]);
        if (BlockScore < ChallengeScore)
            yield return new ValidationResult("Block threshold must not be below challenge threshold", [nameof(BlockScore)]);
        foreach (var (name, values) in new[] { (nameof(BypassIps), BypassIps), (nameof(TrustedFingerprintProxies), TrustedFingerprintProxies) })
            if (values != null)
                for (var i = 0; i < values.Count; i++)
                    if (!new IpAddressAttribute().IsValid(values[i]) || string.IsNullOrWhiteSpace(values[i]))
                        yield return new ValidationResult("Expected an individual IP address", [$"{name}[{i}]"]);
        if (BypassPaths != null)
            for (var i = 0; i < BypassPaths.Count; i++)
                if (string.IsNullOrEmpty(BypassPaths[i]) || BypassPaths[i].Length > 8192 || !BypassPaths[i].StartsWith('/') ||
                    BypassPaths[i].StartsWith("//") || BypassPaths[i].Any(c => char.IsControl(c) || c == '\\'))
                    yield return new ValidationResult("Expected a local absolute path prefix", [$"{nameof(BypassPaths)}[{i}]"]);
    }
}
