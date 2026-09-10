using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementErrorFilterTests
{
    [Fact]
    public async Task BusinessValidationUsesJsonFieldCasing()
    {
        var result = await new ManagementErrorFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(new DefaultHttpContext()),
            _ => throw new ArgumentException("Invalid domain", "Domain"));
        var envelope = Assert.IsType<ApiErrorEnvelope>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Contains("domain", Assert.IsType<Dictionary<string, string[]>>(envelope.Error.Details).Keys);
    }

    [Fact]
    public async Task LegacyServerErrorDoesNotExposeExceptionOrCredentialFields()
    {
        var result = await new ManagementErrorFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(new DefaultHttpContext()),
            _ => ValueTask.FromResult<object?>(Results.Json(new { success = false, message = "secret-password", stack = "private-path" }, statusCode: 500)));
        Assert.Equal(500, ((IStatusCodeHttpResult)result!).StatusCode);
        var json = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        Assert.DoesNotContain("secret-password", json);
        Assert.DoesNotContain("private-path", json);
        Assert.Contains("internal_error", json);
    }

    [Theory]
    [InlineData(403, "permission_denied")]
    [InlineData(413, "request_too_large")]
    [InlineData(415, "unsupported_media_type")]
    [InlineData(503, "service_unavailable")]
    public void ErrorCodesAreSharedAcrossMiddlewareAndHandlers(int status, string code) =>
        Assert.Equal(code, ManagementApiContract.StatusCodeToErrorCode(status));

    [Fact]
    public async Task ErrorContractKeepsLoginCooldown()
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = "trace" };
        var invocation = new DefaultEndpointFilterInvocationContext(ctx);
        var result = await new ManagementErrorFilter().InvokeAsync(invocation, _ => ValueTask.FromResult<object?>(
            Results.Json(new { success = false, message = "稍后重试", retryAfterSeconds = 60 }, statusCode: 401)));
        Assert.Equal(401, ((IStatusCodeHttpResult)result!).StatusCode);
        var json = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(60, document.RootElement.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal("trace", document.RootElement.GetProperty("error").GetProperty("traceId").GetString());
    }
}
