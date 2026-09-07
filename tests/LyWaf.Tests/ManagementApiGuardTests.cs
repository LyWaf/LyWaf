using LyWaf.Services.ManagementApi;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementApiGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void RecognizesExplicitLoopbackControlHosts(string host) =>
        Assert.True(Services.WafInfo.WafInfoOptions.IsLoopbackAddress(host));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("control.example.com")]
    public void RejectsNonLoopbackControlHostsByDefault(string host) =>
        Assert.False(Services.WafInfo.WafInfoOptions.IsLoopbackAddress(host));

    [Fact]
    public void SeparatesReadAndWriteLimitsAndReturnsRetryAfter()
    {
        var guard = new ManagementApiGuard(new StaticOptionsMonitor<ManagementApiOptions>(new()
        {
            ReadRequestsPerMinute = 2, WriteRequestsPerMinute = 1, MaxTrackedClients = 100
        }));
        var now = DateTimeOffset.Parse("2026-01-01T00:00:30Z");
        Assert.True(guard.TryAcquire("127.0.0.1", false, now, out _));
        Assert.True(guard.TryAcquire("127.0.0.1", false, now, out _));
        Assert.False(guard.TryAcquire("127.0.0.1", false, now, out var readRetry));
        Assert.Equal(30, readRetry);
        Assert.True(guard.TryAcquire("127.0.0.1", true, now, out _));
        Assert.False(guard.TryAcquire("127.0.0.1", true, now, out _));
        Assert.True(guard.TryAcquire("127.0.0.1", true, now.AddMinutes(1), out _));
    }
}
