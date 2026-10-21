using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.Statistic;

internal static class CcRequestIdentity
{
    internal static bool ValidConfiguration(AdvancedCcRule rule)
        => Enum.IsDefined(rule.Scope) && Enum.IsDefined(rule.Subject)
            && rule.ExpectedNodes is { Length: <= 256 }
            && rule.ExpectedNodes.All(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 128 && !n.Any(char.IsControl))
            && rule.ExpectedNodes.Distinct(StringComparer.Ordinal).Count() == rule.ExpectedNodes.Length
            && (!rule.RequireCompleteCoverage || rule.Scope == CcScope.Node || rule.ExpectedNodes.Length > 0 || rule.UseManagedMembership)
            && (!rule.UseManagedMembership || rule.Scope != CcScope.Node && rule.RequireCompleteCoverage && rule.ExpectedNodes.Length == 0)
            && (rule.Subject == CcSubjectKind.Ip || (rule.Action != CcAction.Captcha
                && rule.SubjectAuthenticationType is { Length: > 0 and <= 128 }
                && rule.SubjectIssuer is { Length: > 0 and <= 256 }))
            && (rule.Scope == CcScope.SiteGroup || string.IsNullOrWhiteSpace(rule.ProtectionGroup));

    internal static bool TrySubject(HttpContext context, AdvancedCcRule rule, string clientIp, out string subject)
    {
        subject = "";
        if (!ValidConfiguration(rule)) return false;
        if (rule.Subject == CcSubjectKind.Ip)
        {
            if (!IPAddress.TryParse(clientIp, out var ip)) return false;
            subject = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
            return true;
        }
        // Only identities produced by the configured server authentication scheme are eligible. Never read headers.
        if (rule.Subject is not (CcSubjectKind.Account or CcSubjectKind.ApiCredential)
            || string.IsNullOrWhiteSpace(rule.SubjectAuthenticationType) || string.IsNullOrWhiteSpace(rule.SubjectIssuer)
            || rule.Action == CcAction.Captcha) return false;
        var type = rule.Subject == CcSubjectKind.Account ? ClaimTypes.NameIdentifier : "lywaf:api-credential-id";
        var values = context.User.Identities.Where(i => i.IsAuthenticated && i.AuthenticationType == rule.SubjectAuthenticationType)
            .SelectMany(i => i.Claims).Where(c => c.Type == type && c.Issuer == rule.SubjectIssuer)
            .Select(c => c.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0].Length > 1024 || values[0].Any(char.IsControl)) return false;
        subject = (rule.Subject == CcSubjectKind.Account ? "account:" : "credential:")
            + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
                { rule.SubjectAuthenticationType, rule.SubjectIssuer, values[0] })));
        return true;
    }

    internal static bool ValidSubject(AdvancedCcRule rule, string subject)
    {
        if (rule.Subject == CcSubjectKind.Ip) return IPAddress.TryParse(subject, out var ip) && ip.ToString() == subject;
        var prefix = rule.Subject == CcSubjectKind.Account ? "account:" : rule.Subject == CcSubjectKind.ApiCredential ? "credential:" : "";
        return prefix.Length > 0 && subject.StartsWith(prefix, StringComparison.Ordinal)
            && subject.Length == prefix.Length + 64 && subject[prefix.Length..].All(char.IsAsciiHexDigit)
            && rule.Action != CcAction.Captcha && !string.IsNullOrWhiteSpace(rule.SubjectAuthenticationType)
            && !string.IsNullOrWhiteSpace(rule.SubjectIssuer);
    }
}
