using LyWaf.Services.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementAuthorizationTests
{
    [Fact]
    public async Task EndpointDeclarationsProtectWritesAndSensitiveReads()
    {
        await using var app = WebApplication.CreateBuilder().Build();
        app.MapGet("/api/routes", () => "ok").RequireManagementPermission();
        app.MapPost("/api/routes", () => "ok").RequireManagementPermission();
        app.MapGet("/api/config", () => "secret").RequireManagementPermission();
        app.MapPost("/api/plugins/install", () => "ok").RequireManagementPermission();
        app.MapPost("/api/upgrade/activate", () => "ok").RequireManagementPermission();
        app.MapPost("/api/lifecycle/drain", () => "ok").RequireManagementPermission();
        app.MapPost("/api/auth/users", () => "ok").RequireManagementPermission(ManagementPermissions.ManageUsers);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        foreach (var endpoint in endpoints)
        {
            var ctx = new DefaultHttpContext();
            ctx.SetEndpoint(endpoint);
            ctx.Request.Path = endpoint.RoutePattern.RawText;
            ctx.Request.Method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0];
            Assert.True(ManagementAuthorization.IsAllowed(ctx, ManagementPermissions.ForRole("admin")));
            Assert.Equal(ctx.Request.Method == "GET" && ctx.Request.Path == "/api/routes",
                ManagementAuthorization.IsAllowed(ctx, ManagementPermissions.ForRole("auditor")));
            Assert.False(ManagementAuthorization.IsAllowed(ctx, []));
            if (ctx.Request.Path.StartsWithSegments("/api/plugins") || ctx.Request.Path.StartsWithSegments("/api/upgrade") || ctx.Request.Path.StartsWithSegments("/api/lifecycle"))
                Assert.False(ManagementAuthorization.IsAllowed(ctx, [ManagementPermissions.Read, ManagementPermissions.Write]));
        }
    }
}
