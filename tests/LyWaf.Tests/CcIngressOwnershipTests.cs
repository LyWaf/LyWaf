using System.Net;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcIngressOwnershipTests
{
    private const string Secret = "data-plane-only-pairwise-key-32-bytes-long";
    private static CcIngressOwnership Node(string id, string peer, string destination) => new(
        new StaticOptionsMonitor<CcIngressOptions>(new() { Enabled = true, Peers = new() { [peer] = new()
        { Secret = Secret, DestinationAuthority = destination, RemoteAddresses = ["127.0.0.1"], RuleIds = ["rule"], Scopes = ["group:g"] } } }),
        new StaticOptionsMonitor<RuntimeStateOptions>(new() { NodeId = id }));
    private static DefaultHttpContext Request(string? token = null)
    {
        var context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Method = "GET";
        context.Request.Path = "/api"; context.Connection.RemoteIpAddress = IPAddress.Loopback;
        if (token is not null) context.Request.Headers[CcIngressOwnership.Header] = token;
        return context;
    }
    private static (AdvancedCcRule Rule, CcPolicyDescriptor Policy) Policy()
    {
        var rule = new AdvancedCcRule { Id = "rule", ProtectionGroup = "g", Action = CcAction.Block };
        return (rule, new(rule.Id, CcRuleStateKey.Revision(rule), "group:g", "192.0.2.1",
            CcRuleStateKey.Create(rule, "192.0.2.1", "group:g"), CcPolicyGeneration.Initial));
    }

    [Fact]
    public void ThreeInternalRetriesCountOnceButThreeClientRequestsCountThreeTimes()
    {
        var a = Node("a", "b", "https://b"); var b = Node("b", "a", "https://a");
        var (rule, policy) = Policy(); int counts = 0;
        for (var client = 0; client < 3; client++)
        {
            var ingress = Request("forged-external-header");
            if (a.ShouldCount(ingress, rule, policy)) counts++;
            Assert.False(a.ShouldCount(ingress, rule, policy));
            using var outbound = new HttpRequestMessage(HttpMethod.Get, "https://b/api");
            outbound.Headers.TryAddWithoutValidation(CcIngressOwnership.Header, "forged");
            a.Forward(ingress, outbound);
            var token = Assert.Single(outbound.Headers.GetValues(CcIngressOwnership.Header));
            for (var retry = 0; retry < 3; retry++) Assert.False(b.ShouldCount(Request(token), rule, policy));
        }
        Assert.Equal(3, counts);
    }

    [Theory]
    [InlineData("address")]
    [InlineData("path")]
    [InlineData("method")]
    [InlineData("signature")]
    [InlineData("generation")]
    [InlineData("tls")]
    public void UntrustedOrUnboundMarkersNeverSuppressCount(string fault)
    {
        var a = Node("a", "b", "https://b"); var b = Node("b", "a", "https://a"); var (rule, policy) = Policy();
        var first = Request(); Assert.True(a.ShouldCount(first, rule, policy));
        using var outbound = new HttpRequestMessage(HttpMethod.Get, "https://b/api"); a.Forward(first, outbound);
        var token = Assert.Single(outbound.Headers.GetValues(CcIngressOwnership.Header));
        if (fault == "signature") token = token[..^1] + (token[^1] == '0' ? "1" : "0");
        var request = Request(token);
        if (fault == "address") request.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.99");
        if (fault == "path") request.Request.Path = "/other";
        if (fault == "method") request.Request.Method = "POST";
        if (fault == "tls") request.Request.Scheme = "http";
        if (fault == "generation") policy = policy with { PolicyGeneration = new(1, new string('a', 32)) };
        Assert.True(b.ShouldCount(request, rule, policy));
    }

    [Fact]
    public void UnconfiguredDestinationNeverReceivesClientMarker()
    {
        var a = Node("a", "b", "https://b");
        using var outbound = new HttpRequestMessage(HttpMethod.Get, "https://untrusted/api");
        outbound.Headers.TryAddWithoutValidation(CcIngressOwnership.Header, "forged");
        a.Forward(Request(), outbound);
        Assert.False(outbound.Headers.Contains(CcIngressOwnership.Header));
    }
}
