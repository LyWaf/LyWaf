using System.Reflection;
using System.Text.Json;
using LyWaf.Control;
using LyWaf.Services.Auth;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementIdentityContractTests
{
    [Fact]
    public async Task ActualIdentityEndpointsDeclareSuccessModelsAndUserListIsRedacted()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IAuthService>(_ => throw new InvalidOperationException("Schema generation must not instantiate auth"));
        builder.Services.AddSingleton<IAuditLogService>(_ => throw new InvalidOperationException());
        builder.Services.AddSingleton<IManagementTokenService>(_ => throw new InvalidOperationException());
        await using var app = builder.Build();
        typeof(ControlApi).GetMethod("MapManagementIdentityEndpoints", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [app, 12345]);
        typeof(ControlApi).GetMethod("MapAuthenticationEndpoints", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [app, 12345]);
        typeof(ControlApi).GetMethod("MapManagementRoleEndpoints", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [app, 12345]);
        typeof(ControlApi).GetMethod("MapManagementMfaEndpoints", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [app, 12345]);
        var sources = ((IEndpointRouteBuilder)app).DataSources;
        using var source = new CompositeEndpointDataSource(sources);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ManagementApiContract.BuildOpenApiDocument(source)));
        var paths = document.RootElement.GetProperty("paths");
        foreach (var path in paths.EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
                Assert.True(operation.Value.GetProperty("responses").GetProperty("200").TryGetProperty("content", out _), path.Name);
        var users = paths.GetProperty("/api/v1/auth/users").GetProperty("get");
        var schema = users.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        var properties = schema.GetProperty("properties").GetProperty("data").GetProperty("items").GetProperty("properties");
        Assert.True(properties.TryGetProperty("permissions", out _));
        Assert.False(properties.TryGetProperty("passwordHash", out _));
        Assert.False(properties.TryGetProperty("totpSecret", out _));
        Assert.Equal(7, users.GetProperty("parameters").GetArrayLength());
        var password = paths.GetProperty("/api/v1/auth/users").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("properties").GetProperty("password");
        Assert.Equal(8, password.GetProperty("minLength").GetInt32());
        Assert.Equal(1024, password.GetProperty("maxLength").GetInt32());
        var mfa = paths.GetProperty("/api/v1/auth/mfa/{action}").GetProperty("post").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("anyOf");
        Assert.Equal(4, mfa.GetArrayLength());
        Assert.Contains(mfa.EnumerateArray(), schema => schema.GetProperty("properties").TryGetProperty("enrollment", out _));
        Assert.Contains(mfa.EnumerateArray(), schema => schema.GetProperty("properties").TryGetProperty("recoveryCodes", out _));
    }
}
