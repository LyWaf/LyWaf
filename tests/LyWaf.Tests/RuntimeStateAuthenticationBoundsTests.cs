using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateAuthenticationBoundsTests
{
    private sealed class Monitor : IOptionsMonitor<RuntimeStateOptions>
    {
        public RuntimeStateOptions CurrentValue { get; } = new()
        {
            Enabled = true, SharedSecret = "test-secret", MaxValueBytes = 2048,
            Peers = new() { ["node-1"] = new() { Endpoint = "http://node-1" } }
        };
        public RuntimeStateOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RuntimeStateOptions, string?> listener) => null;
    }

    private static DefaultHttpContext Request(byte[] body)
    {
        var context = new DefaultHttpContext();
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/runtime-state/agent/merge");
        RuntimeStateAuthentication.Sign(request, "node-1", "test-secret", body);
        foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
        context.Request.Body = new MemoryStream(body);
        return context;
    }

    [Fact]
    public async Task SharedSecretDoesNotAuthorizeUnregisteredOrDisabledSender()
    {
        var monitor = new Monitor();
        monitor.CurrentValue.Peers["node-1"].Enabled = false;
        var context = Request("{}"u8.ToArray());
        var auth = new RuntimeStateAuthentication(monitor);
        Assert.Equal("节点未授权或已禁用", (await auth.ReadAndVerifyAsync(context, default)).Error);
        Assert.Equal(0, context.Request.Body.Position);
        monitor.CurrentValue.Peers.Clear();
        Assert.Equal("节点未授权或已禁用", (await auth.ReadAndVerifyAsync(context, default)).Error);
    }

    [Fact]
    public async Task ChunkedBodyStopsAtLimitPlusOneByte()
    {
        var context = Request(new byte[65536]);
        var result = await new RuntimeStateAuthentication(new Monitor()).ReadAndVerifyAsync(context, CancellationToken.None);
        Assert.Null(result.Value);
        Assert.Contains("超过限制", result.Error);
        Assert.Equal(4097, context.Request.Body.Position);
    }

    [Fact]
    public async Task ExtremeTimestampIsRejectedWithoutOverflow()
    {
        var context = Request("{}"u8.ToArray());
        context.Request.Headers["X-LyWaf-Timestamp"] = long.MinValue.ToString();
        var result = await new RuntimeStateAuthentication(new Monitor()).ReadAndVerifyAsync(context, CancellationToken.None);
        Assert.Null(result.Value);
        Assert.Contains("时间戳", result.Error);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task DuplicateSignatureHeadersAreRejectedBeforeReadingBody()
    {
        var context = Request("{}"u8.ToArray());
        context.Request.Headers.Append("X-LyWaf-Nonce", "second-nonce-value");
        var result = await new RuntimeStateAuthentication(new Monitor()).ReadAndVerifyAsync(context, CancellationToken.None);
        Assert.Null(result.Value);
        Assert.Contains("只能出现一次", result.Error);
        Assert.Equal(0, context.Request.Body.Position);
    }
}
