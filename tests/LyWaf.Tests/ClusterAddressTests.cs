using LyWaf.Control;
using Xunit;

namespace LyWaf.Tests;

public sealed class ClusterAddressTests
{
    [Theory]
    [InlineData("127.0.0.1", "http://127.0.0.1/", 80)]
    [InlineData(" example.com ", "http://example.com/", 80)]
    [InlineData("localhost:8080/api", "http://localhost:8080/api", 8080)]
    [InlineData("http://localhost", "http://localhost/", 80)]
    [InlineData("https://localhost", "https://localhost:80/", 80)]
    [InlineData("https://localhost:443", "https://localhost/", 443)]
    [InlineData("[::1]", "http://[::1]/", 80)]
    [InlineData("[::1]:8080", "http://[::1]:8080/", 8080)]
    public void NormalizesAddress(string input, string expected, int port)
    {
        Assert.True(ControlApi.TryNormalizeClusterAddress(input, out var uri));
        Assert.Equal(expected, uri.AbsoluteUri);
        Assert.Equal(port, uri.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    [InlineData("ftp://localhost")]
    [InlineData("localhost:")]
    [InlineData("localhost:65536")]
    [InlineData("http://user:password@localhost")]
    [InlineData("localhost?x=1")]
    public void RejectsInvalidAddress(string input)
    {
        Assert.False(ControlApi.TryNormalizeClusterAddress(input, out _));
    }
}
