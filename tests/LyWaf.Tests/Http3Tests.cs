using LyWaf.Config;
using LyWaf.Services.Protocols;
using LyWaf.Services.WafInfo;
using Xunit;

namespace LyWaf.Tests;

public sealed class Http3Tests
{
    [Theory]
    [InlineData(true, 443, false, true, true)]
    [InlineData(false, 443, false, true, false)]
    [InlineData(false, 80, false, true, false)]
    [InlineData(true, 443, true, true, false)]
    [InlineData(true, 443, false, false, false)]
    public void PolicyRestrictsProtocolToSupportedHttpsBusinessListeners(bool https, int port,
        bool control, bool platformSupported, bool expected)
    {
        var options = new Http3Options { Enabled = true };
        var listen = new OneLinstenInfo { IsHttps = https, Port = port };
        Assert.Equal(expected, Http3Policy.ShouldEnable(options, listen, control, platformSupported));
    }

    [Fact]
    public void PortAllowlistAndControlOptInAreApplied()
    {
        var options = new Http3Options { Enabled = true, Ports = [8443], EnableControl = true };
        Assert.False(Http3Policy.ShouldEnable(options, new OneLinstenInfo { IsHttps = true, Port = 443 }, false, true));
        Assert.True(Http3Policy.ShouldEnable(options, new OneLinstenInfo { IsHttps = true, Port = 8443 }, true, true));
    }

    [Fact]
    public void LyConfigPreservesHttp3Settings()
    {
        var parsed = LyConfigParser.Parse("""
            Http3 { Enabled = true Ports = [443, 8443] EnableControl = false RequireSupport = true }
            """);
        var app = LyToAppSettingsConverter.TransformToAppSettings(parsed);
        var http3 = Assert.IsType<Dictionary<string, object>>(app["Http3"]);
        Assert.Equal(true, http3["Enabled"]);
        Assert.Equal(true, http3["RequireSupport"]);
    }
}
