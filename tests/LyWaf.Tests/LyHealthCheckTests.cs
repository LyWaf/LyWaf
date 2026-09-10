using LyWaf.Config;
using LyWaf.Policy;
using Xunit;

namespace LyWaf.Tests;

public sealed class LyHealthCheckTests
{
    [Theory]
    [InlineData("20x", 209, true)]
    [InlineData("20x", 210, false)]
    [InlineData("30x", 302, true)]
    [InlineData("30x", 310, false)]
    [InlineData("2xx", 299, true)]
    [InlineData("x0x", 503, true)]
    [InlineData("x0x", 519, false)]
    [InlineData("200,30x", 304, true)]
    public void WildcardsMatchIndependentDigits(string pattern, int code, bool expected)
        => Assert.Equal(expected, HealthStatusCodes.Matches(pattern, code));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsesSiteAndProxyBlock(bool nested)
    {
        var health = """
            health_check {
                enabled = true
                path = "/ready"
                method = POST
                content_type = "application/json"
                body = "{\"probe\":true}"
                status_codes = [200, "20x", "30x"]
                interval = 30
                timeout = 5
                passes = 2
                fails = 3
            }
            """;
        var config = nested ? ":8080 { proxy { to = \"http://localhost:9000\" " + health + " } }"
            : ":8080 { proxy = [\"http://localhost:9000\"] " + health + " }";
        var app = LyToAppSettingsConverter.TransformToAppSettings(LyConfigParser.Parse(config));
        var proxy = Assert.IsType<Dictionary<string, object>>(app["ReverseProxy"]);
        var clusters = Assert.IsType<Dictionary<string, object>>(proxy["Clusters"]);
        var cluster = Assert.IsType<Dictionary<string, object>>(clusters.First(p => p.Key != "cluster_unuse").Value);
        var active = Assert.IsType<Dictionary<string, object>>(Assert.IsType<Dictionary<string, object>>(cluster["HealthCheck"])["Active"]);
        Assert.Equal("/ready", active["Path"]);
        Assert.Equal("00:00:30", active["Interval"]);
        var metadata = Assert.IsType<Dictionary<string, object>>(cluster["Metadata"]);
        Assert.Equal("POST", metadata["LyxActiveHealth.Method"]);
        Assert.Equal("200,20x,30x", metadata["LyxActiveHealth.AvalidCode"]);
        Assert.Equal("{\"probe\":true}", metadata["LyxActiveHealth.Body"]);
    }
}
