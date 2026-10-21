using System.Net;
using LyWaf.Config;
using LyWaf.Services.Param;
using LyWaf.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace LyWaf.Tests;

public sealed class RequestUtilTrustedProxyTests
{
    private static GlobalOptions Trusted() => new()
    {
        TrustedProxies = ["10.0.0.1", "2001:db8::1"],
        TrustedNetworks = ["10.1.0.0/16", "2001:db8:1::/48"]
    };

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Real-IP")]
    [InlineData("Forwarded")]
    public void UntrustedDirectPeerIgnoresForgedHeaders(string header)
    {
        var request = Request("198.51.100.9", header, header == "Forwarded" ? "for=203.0.113.7" : "203.0.113.7");
        Assert.Equal("198.51.100.9", RequestUtil.GetClientIp(request, Trusted()));
    }

    [Fact]
    public void DefaultsTrustNeitherPrivateNorLoopbackPeers()
    {
        foreach (var remote in new[] { "127.0.0.1", "10.0.0.1", "::1" })
            Assert.Equal(remote, RequestUtil.GetClientIp(Request(remote, "X-Forwarded-For", "203.0.113.7"), new GlobalOptions()));
    }

    [Fact]
    public void FirstServerIgnoresEvenTrustedHeadersAndNormalizesMappedAddress()
    {
        var options = Trusted();
        options.IsFirstServer = true;
        Assert.Equal("10.0.0.1", RequestUtil.GetClientIp(Request("::ffff:10.0.0.1", "X-Forwarded-For", "203.0.113.7"), options));
    }

    [Theory]
    [InlineData("203.0.113.7, 10.1.2.3", "203.0.113.7")]
    [InlineData("203.0.113.7, 198.51.100.9, 10.1.2.3", "198.51.100.9")]
    [InlineData("203.0.113.7:4321, 10.1.2.3:443", "203.0.113.7")]
    [InlineData("[2001:db8:2::7]:4321, [2001:db8:1::1]:443", "2001:db8:2::7")]
    [InlineData("2001:db8:2::7, ::ffff:10.1.2.3", "2001:db8:2::7")]
    [InlineData("::ffff:203.0.113.7, 10.1.2.3", "203.0.113.7")]
    public void WalksTrustedChainFromRightToLeft(string header, string expected)
        => Assert.Equal(expected, RequestUtil.GetClientIp(Request("::ffff:10.0.0.1", "X-Forwarded-For", header), Trusted()));

    [Theory]
    [InlineData("X-Forwarded-For", "unknown")]
    [InlineData("X-Forwarded-For", "")]
    [InlineData("X-Forwarded-For", "203.0.113.7, invalid, 10.1.2.3")]
    [InlineData("X-Forwarded-For", "203.0.113.7,,10.1.2.3")]
    [InlineData("X-Forwarded-For", "203.0.113.7:99999")]
    [InlineData("X-Forwarded-For", "[2001:db8::7]:bad")]
    [InlineData("X-Forwarded-For", "fe80::1%3")]
    [InlineData("X-Real-IP", "203.0.113.7, 203.0.113.8")]
    [InlineData("Forwarded", "for=unknown")]
    [InlineData("Forwarded", "for=_hidden")]
    [InlineData("Forwarded", "for=\"unterminated")]
    [InlineData("Forwarded", "for=203.0.113.7;For=203.0.113.8")]
    [InlineData("Forwarded", "for=203.0.113.7;host=\"example,other\"")]
    [InlineData("Forwarded", "for=203.0.113.7;for=203.0.113.8")]
    [InlineData("Forwarded", "proto=https")]
    [InlineData("Forwarded", "for=203.0.113.7,,for=10.1.2.3")]
    public void InvalidSelectedHeaderFailsClosedWithoutFallback(string header, string value)
    {
        var request = Request("10.0.0.1", header, value);
        if (header == "X-Forwarded-For") request.Headers["X-Real-IP"] = "203.0.113.99";
        Assert.Equal("10.0.0.1", RequestUtil.GetClientIp(request, Trusted()));
    }

    [Theory]
    [InlineData("for=203.0.113.7;proto=https, for=10.1.2.3", "203.0.113.7")]
    [InlineData("proto=https;for=\"[2001:db8:2::7]:443\", for=\"[2001:db8:1::1]\"", "2001:db8:2::7")]
    [InlineData("for=203.0.113.7, for=198.51.100.9, for=10.1.2.3", "198.51.100.9")]
    public void ParsesStandardForwardedWithFrameworkHeaderParser(string value, string expected)
        => Assert.Equal(expected, RequestUtil.GetClientIp(Request("10.0.0.1", "Forwarded", value), Trusted()));

    [Fact]
    public void RepeatedListHeadersPreserveWireOrderButRealIpMustBeSingle()
    {
        var request = Request("10.0.0.1", "X-Forwarded-For", new StringValues(["203.0.113.7", "10.1.2.3"]));
        Assert.Equal("203.0.113.7", RequestUtil.GetClientIp(request, Trusted()));
        request = Request("10.0.0.1", "Forwarded", new StringValues(["for=203.0.113.7", "for=10.1.2.3"]));
        Assert.Equal("203.0.113.7", RequestUtil.GetClientIp(request, Trusted()));
        request = Request("10.0.0.1", "X-Real-IP", new StringValues(["203.0.113.7", "203.0.113.8"]));
        Assert.Equal("10.0.0.1", RequestUtil.GetClientIp(request, Trusted()));
    }

    [Fact]
    public void UsesExplicitHeaderPrecedenceAndRejectsOversizedLists()
    {
        var request = Request("10.0.0.1", "X-Forwarded-For", "203.0.113.7");
        request.Headers["X-Real-IP"] = "203.0.113.8";
        request.Headers["Forwarded"] = "for=203.0.113.9";
        Assert.Equal("203.0.113.7", RequestUtil.GetClientIp(request, Trusted()));
        request.Headers["X-Forwarded-For"] = string.Join(',', Enumerable.Repeat("10.1.2.3", 65));
        Assert.Equal("10.0.0.1", RequestUtil.GetClientIp(request, Trusted()));
        request.Headers["X-Forwarded-For"] = new string(' ', 16385);
        Assert.Equal("10.0.0.1", RequestUtil.GetClientIp(request, Trusted()));
        request.HttpContext.Connection.RemoteIpAddress = null;
        Assert.Equal("", RequestUtil.GetClientIp(request, Trusted()));
    }

    [Theory]
    [InlineData("bad", "10.0.0.0/8")]
    [InlineData("10.0.0.1:443", "10.0.0.0/8")]
    [InlineData("10.0.0.1", "10.0.0.0/33")]
    [InlineData("10.0.0.1", "10.0.0.1/8")]
    public void InvalidConfigurationIsRejectedAndNeverExpandsTrust(string proxy, string network)
    {
        var options = new GlobalOptions { TrustedProxies = [proxy], TrustedNetworks = [network] };
        Assert.False(RequestUtil.IsValidProxyConfiguration(options));
        Assert.Equal("10.0.0.1", RequestUtil.GetClientIp(Request("10.0.0.1", "X-Real-IP", "203.0.113.7"), options));
    }

    [Fact]
    public void MappedProxyAndMappedCidrMatchNormalizedPeer()
    {
        var options = new GlobalOptions { TrustedProxies = ["::ffff:10.0.0.1"], TrustedNetworks = ["::ffff:10.1.0.0/112"] };
        Assert.True(RequestUtil.IsValidProxyConfiguration(options));
        Assert.Equal("203.0.113.7", RequestUtil.GetClientIp(Request("10.0.0.1", "X-Forwarded-For", "203.0.113.7, ::ffff:10.1.2.3"), options));
    }

    [Fact]
    public void PublicEntryPointWithoutOptionsOrWithInvalidOptionsFailsClosed()
    {
        var request = Request("::ffff:10.0.0.1", "X-Forwarded-For", "203.0.113.7");
        using var empty = new ServiceCollection().BuildServiceProvider();
        request.HttpContext.RequestServices = empty;
        Assert.Equal("10.0.0.1", RequestUtil.GetClientIp(request));
        using var invalid = new ServiceCollection().AddOptions<GlobalOptions>()
            .Configure(x => x.TrustedProxies = ["bad"])
            .Validate(RequestUtil.IsValidProxyConfiguration).Services.BuildServiceProvider();
        request.HttpContext.RequestServices = invalid;
        Assert.Throws<OptionsValidationException>(() => invalid.GetRequiredService<IOptionsMonitor<GlobalOptions>>().CurrentValue);
        Assert.Equal("10.0.0.1", RequestUtil.GetClientIp(request));
    }

    [Fact]
    public void NewOptionsSnapshotRevokesPreviouslyTrustedPeer()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Global:TrustedProxies:0"] = "10.0.0.1" }).Build();
        using var services = new ServiceCollection().AddOptions<GlobalOptions>()
            .Bind(config.GetSection("Global")).Validate(RequestUtil.IsValidProxyConfiguration).Services.BuildServiceProvider();
        var request = Request("10.0.0.1", "X-Real-IP", "203.0.113.7");
        request.HttpContext.RequestServices = services;
        Assert.Equal("203.0.113.7", RequestUtil.GetClientIp(request));
        config["Global:TrustedProxies:0"] = "10.0.0.2";
        config.Reload();
        Assert.Equal("10.0.0.1", RequestUtil.GetClientIp(request));
    }

    [Fact]
    public void GenericLyGlobalBlockBindsWithoutDslChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "trusted-proxies-" + Guid.NewGuid().ToString("N") + ".ly");
        try
        {
            File.WriteAllText(path, """
                Global {
                    IsFirstServer = false
                    TrustedProxies = ["10.0.0.1", "2001:db8::1"]
                    TrustedNetworks = ["10.1.0.0/16", "2001:db8:1::/48"]
                }
                """);
            var config = new ConfigurationBuilder().AddLyConfig(path, optional: false).Build();
            var options = config.GetSection("Global").Get<GlobalOptions>();
            Assert.NotNull(options);
            Assert.Equal(Trusted().TrustedProxies, options.TrustedProxies);
            Assert.Equal(Trusted().TrustedNetworks, options.TrustedNetworks);
            Assert.True(RequestUtil.IsValidProxyConfiguration(options));
            using var services = new ServiceCollection().AddOptions<GlobalOptions>()
                .Bind(config.GetSection("Global")).Services.BuildServiceProvider();
            var request = Request("10.0.0.1", "X-Real-IP", "203.0.113.7");
            request.HttpContext.RequestServices = services;
            Assert.Equal("203.0.113.7", RequestUtil.GetClientIp(request));
        }
        finally { File.Delete(path); }
    }

    private static HttpRequest Request(string remote, string header, StringValues value)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Headers[header] = value;
        return context.Request;
    }
}
