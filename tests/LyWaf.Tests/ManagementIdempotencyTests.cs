using System.Net;
using System.Net.Http.Json;
using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementIdempotencyTests
{
    [Theory]
    [InlineData("/api/mutate")]
    [InlineData("/api/auth/users")]
    [InlineData("/api/auth/roles")]
    [InlineData("/api/auth/tokens")]
    public async Task DuplicateRequestIsReplayedAndConflictingBodyIsRejected(string path)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ManagementIdempotency>();
        await using var app = builder.Build();
        app.Use(async (ctx, next) => { ctx.Connection.LocalPort = 12345; ctx.Items["Username"] = "test"; await next(); });
        app.UseManagementIdempotency(12345);
        var executions = 0;
        app.MapPost(path, async (HttpContext ctx) =>
        {
            _ = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
            return Results.Ok(new { success = true, execution = Interlocked.Increment(ref executions) });
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Add("Idempotency-Key", "12345678-test-key");
        var first = await client.PostAsJsonAsync(path, new { value = "one" });
        var second = await client.PostAsJsonAsync(path, new { value = "one" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.True(second.Headers.Contains("Idempotency-Replayed"));
        var conflict = await client.PostAsJsonAsync(path, new { value = "two" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, executions);
        await app.StopAsync();
    }
}
