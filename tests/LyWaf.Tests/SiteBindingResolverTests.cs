using LyWaf.Services.Sites;
using Xunit;

namespace LyWaf.Tests;

public sealed class SiteBindingResolverTests
{
    [Theory]
    [InlineData("example.com:8443", 8443, true)]
    [InlineData("example.com:8443", 443, false)]
    [InlineData("example.com", 8080, true)]
    [InlineData("[::1]:8080", 8080, true)]
    [InlineData("[::1]:8080", 443, false)]
    public void ResolvesExplicitAndDefaultPorts(string host, int port, bool expected) =>
        Assert.Equal(expected, SiteBindingResolver.UsesPort([host], port));

    [Theory]
    [InlineData("Example.com:443", "example.com", true)]
    [InlineData("api.example.com:8443", "*.example.com", true)]
    [InlineData("a.api.example.com", "*.example.com", false)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("*.example.com", "*.example.com", true)]
    [InlineData("*", "example.com", false)]
    public void ResolvesCertificateNames(string host, string certificate, bool expected) =>
        Assert.Equal(expected, SiteBindingResolver.UsesCertificate([host], certificate));
}
