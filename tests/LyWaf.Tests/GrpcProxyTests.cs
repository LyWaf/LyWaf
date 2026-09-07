using LyWaf.Config;
using LyWaf.Services.Protocols;
using LyWaf.Services.WafInfo;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Xunit;

namespace LyWaf.Tests;

public sealed class GrpcProxyTests
{
    [Fact]
    public void H2cRequiresExplicitNonControlPort()
    {
        var options = new GrpcOptions { EnableH2c = true, H2cPorts = [8080] };
        var listen = new OneLinstenInfo { Port = 8080, IsHttps = false };
        Assert.Equal(HttpProtocols.Http2, GrpcProtocolPolicy.ForHttpListener(options, listen, false));
        Assert.Equal(HttpProtocols.Http1AndHttp2, GrpcProtocolPolicy.ForHttpListener(options, listen, true));
        Assert.Equal(HttpProtocols.Http1AndHttp2, GrpcProtocolPolicy.ForHttpListener(options,
            new OneLinstenInfo { Port = 8081, IsHttps = false }, false));
    }

    [Fact]
    public void LyConfigForcesGrpcClusterToHttp2()
    {
        var parsed = LyConfigParser.Parse("""
            Grpc { EnableH2c = true H2cPorts = [8080] }
            :8080 {
                proxy { to = "http://127.0.0.1:5000" grpc = true }
            }
            """);
        var app = LyToAppSettingsConverter.TransformToAppSettings(parsed);
        var grpc = Assert.IsType<Dictionary<string, object>>(app["Grpc"]);
        Assert.Equal(true, grpc["EnableH2c"]);
        var reverseProxy = Assert.IsType<Dictionary<string, object>>(app["ReverseProxy"]);
        var clusters = Assert.IsType<Dictionary<string, object>>(reverseProxy["Clusters"]);
        var cluster = clusters.Values.Select(Assert.IsType<Dictionary<string, object>>)
            .Single(item => item.ContainsKey("HttpRequest"));
        var httpRequest = Assert.IsType<Dictionary<string, object>>(cluster["HttpRequest"]);
        Assert.Equal("2.0", httpRequest["Version"]);
        Assert.Equal("RequestVersionExact", httpRequest["VersionPolicy"]);
    }
}
