using System.Text;
using System.Text.Json;
using LyWaf.Services.Protect;
using LyWaf.Services.WafRule;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class XssProtectionTests
{
    public static IEnumerable<object[]> Attacks()
    {
        string[] payloads = ["<script>alert(1)</script>", "<img src=x onerror=alert(1)>",
            "<svg onload=alert(1)>", "javascript:alert(1)",
            "<input autofocus onfocus=alert(1)>", "<iframe src=javascript:alert(1)>"];
        foreach (var payload in payloads)
        foreach (var field in new[] { "query", "header", "cookie", "body" })
        foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE" })
            yield return [payload, field, method];
    }

    [Theory]
    [MemberData(nameof(Attacks))]
    public async Task RejectsAcrossMethodsAndNormalizedFields(string payload, string field, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        switch (field)
        {
            case "query": context.Request.QueryString = new QueryString("?q=" + Uri.EscapeDataString(payload)); break;
            case "header": context.Request.Headers["X-Probe"] = Uri.EscapeDataString(payload); break;
            case "cookie": context.Request.Headers.Cookie = "q=" + Uri.EscapeDataString(payload); break;
            case "body":
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { q = payload }));
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                context.Request.ContentType = "application/json";
                break;
        }
        var snapshot = await new RequestInspectionService(new StaticOptionsMonitor<ProtectOptions>(new()))
            .InspectAsync(context);
        var rule = SystemWafRules.GetRules().Single(rule => rule.Id == "sys-xss");
        Assert.Equal(WafRuleAction.Reject, rule.Action);
        Assert.True(WafRuleService.MatchRule(context, rule, "127.0.0.1", snapshot));
        Assert.Equal("XSS", new SemanticThreatDetector().Detect(snapshot, context.Request.Path)?.Category);
    }

    [Theory]
    [InlineData("ordinary search")]
    [InlineData("JavaScript documentation")]
    [InlineData("<img src=logo.png alt=logo>")]
    [InlineData("<input name=once value=yes>")]
    [InlineData("<svg><title>Chart</title></svg>")]
    public void AllowsBenignValues(string value)
    {
        var snapshot = new RequestInspectionSnapshot([value], [value], [value], false, []);
        var context = new DefaultHttpContext();
        var rule = SystemWafRules.GetRules().Single(rule => rule.Id == "sys-xss");
        Assert.False(WafRuleService.MatchRule(context, rule, "127.0.0.1", snapshot));
        Assert.Null(new SemanticThreatDetector().Detect(snapshot, context.Request.Path));
    }
}
