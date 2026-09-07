using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.AntiBot;

public interface IAntiBotAnalyzer
{
    AntiBotAssessment Assess(HttpContext context);
}

public sealed class AntiBotAnalyzer(IOptionsMonitor<AntiBotOptions> options) : IAntiBotAnalyzer
{
    private static readonly Regex Scanner = new(@"(?:sqlmap|nikto|nuclei|masscan|zgrab|dirsearch|gobuster|ffuf|wfuzz|acunetix|nessus)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SearchCrawler = new(@"(?:Googlebot|Bingbot|Baiduspider|YandexBot|DuckDuckBot)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ApiClient = new(@"(?:curl|wget|PostmanRuntime|Insomnia|Go-http-client|python-requests|okhttp|Apache-HttpClient)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Automation = new(@"(?:HeadlessChrome|PhantomJS|Selenium|Playwright|Puppeteer)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public AntiBotAssessment Assess(HttpContext context)
    {
        var current = options.CurrentValue;
        if (IsBypassed(context, current))
            return new(AntiBotClientClass.Unknown, 0, Fingerprint(context), ["configured_bypass"], true);

        var request = context.Request;
        var ua = request.Headers.UserAgent.ToString();
        var reasons = new List<string>();
        var ja4 = TrustedJa4(context, current);
        var score = 0;
        AntiBotClientClass clientClass;
        if (Scanner.IsMatch(ua)) { clientClass = AntiBotClientClass.Scanner; score = 100; reasons.Add("known_scanner_user_agent"); }
        else if (Automation.IsMatch(ua)) { clientClass = AntiBotClientClass.Automation; score = 75; reasons.Add("automation_user_agent"); }
        else if (SearchCrawler.IsMatch(ua)) { clientClass = AntiBotClientClass.SearchCrawler; score = 20; reasons.Add("unverified_search_crawler_user_agent"); }
        else if (ApiClient.IsMatch(ua) || request.Headers.Accept.Any(v => v?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
        { clientClass = AntiBotClientClass.ApiClient; score = 10; reasons.Add("api_client_profile"); }
        else if (ua.Contains("Mozilla/", StringComparison.OrdinalIgnoreCase)) clientClass = AntiBotClientClass.Browser;
        else { clientClass = AntiBotClientClass.Unknown; score = 35; reasons.Add("unknown_client_profile"); }

        if (string.IsNullOrWhiteSpace(ua)) { score += 35; reasons.Add("missing_user_agent"); }
        if (clientClass == AntiBotClientClass.Browser)
        {
            if (!request.Headers.ContainsKey("Accept-Language")) { score += 10; reasons.Add("browser_missing_accept_language"); }
            if (!request.Headers.Keys.Any(k => k.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase))) { score += 15; reasons.Add("browser_missing_fetch_metadata"); }
        }
        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var fetchSite) &&
            fetchSite.Any(v => v is not ("none" or "same-origin" or "same-site" or "cross-site")))
        { score += 20; reasons.Add("invalid_fetch_metadata"); }
        if (ja4 != null) reasons.Add("trusted_ja4_available");

        return new(clientClass, Math.Clamp(score, 0, 100), Fingerprint(context), reasons, false, ja4);
    }

    private static bool IsBypassed(HttpContext context, AntiBotOptions options)
    {
        var path = context.Request.Path.Value ?? "/";
        if (options.BypassPaths.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        var ip = context.Connection.RemoteIpAddress?.ToString();
        return ip is not null && options.BypassIps.Contains(ip, StringComparer.OrdinalIgnoreCase);
    }

    private static string Fingerprint(HttpContext context)
    {
        var tls = context.Features.Get<ITlsHandshakeFeature>();
        var source = string.Join('|',
            context.Request.Protocol,
            tls?.Protocol.ToString() ?? "clear",
            tls?.NegotiatedCipherSuite.ToString() ?? "none",
            context.Request.Headers.UserAgent.ToString(),
            context.Request.Headers.Accept.ToString(),
            context.Request.Headers.AcceptLanguage.ToString(),
            context.Request.Headers.AcceptEncoding.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    }

    private static string? TrustedJa4(HttpContext context, AntiBotOptions options)
    {
        var remote = context.Connection.RemoteIpAddress?.ToString();
        if (remote == null || !options.TrustedFingerprintProxies.Contains(remote, StringComparer.OrdinalIgnoreCase)) return null;
        var value = context.Request.Headers[options.Ja4Header].ToString().Trim();
        return Regex.IsMatch(value, "^[a-z0-9]{1,16}_[a-z0-9]{1,32}_[a-z0-9]{1,32}$",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(20)) ? value.ToLowerInvariant() : null;
    }
}
