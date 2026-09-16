using System.Reflection;
using System.Text.Json;
using LyWaf.Control;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class AuthenticationChallengeTests
{
    [Fact]
    public async Task MissingCredentialsReturnsUnauthorizedWithoutBrowserBasicChallenge()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        typeof(ControlApi).GetMethod("MapAuthenticationEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, 17030]);
        var pipeline = ((IApplicationBuilder)app).Build();
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Connection.LocalPort = 17030;
        context.Request.Path = "/api/sites";
        using var body = new MemoryStream();
        context.Response.Body = body;

        await pipeline(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.StartsWith("application/json", context.Response.ContentType);
        Assert.True(body.Length > 0);
    }
}
