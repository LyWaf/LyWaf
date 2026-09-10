using System.Collections.Concurrent;
using System.Net;
using LyWaf.Services.AuditLog;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementAuditMiddlewareTests
{
    [Theory]
    [InlineData(true, 413)]
    [InlineData(false, 429)]
    public async Task EarlyRejectedWritesHaveAuditAndTraceHeaders(bool oversized, int status)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<ManagementApiOptions>(options =>
        {
            options.MaxRequestBodyBytes = 1024;
            options.WriteRequestsPerMinute = 1;
        });
        builder.Services.Configure<MetricsOptions>(options => options.Enabled = false);
        builder.Services.AddSingleton<PrometheusMetrics>();
        builder.Services.AddSingleton<ManagementApiGuard>();
        var audit = new Audit();
        builder.Services.AddSingleton<IAuditLogService>(audit);
        await using var app = builder.Build();
        app.Use((context, next) => { context.Connection.LocalPort = 12345; return next(); });
        app.UseManagementApiContract(12345);
        app.MapPost("/api/audit-test", () => Results.Ok());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            if (!oversized)
            {
                using var first = await client.PostAsync("/api/audit-test", new StringContent("{}"));
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }
            using var result = await client.PostAsync("/api/audit-test", new StringContent(oversized ? new string('x', 2048) : "{}"));
            Assert.Equal(status, (int)result.StatusCode);
            var trace = Assert.Single(result.Headers.GetValues("X-Request-Id"));
            Assert.Equal("1", Assert.Single(result.Headers.GetValues("X-LyWaf-Api-Version")));
            await app.StopAsync();
            Assert.Contains(audit.Actions, action => action.Contains($"=> {status} [{trace}]", StringComparison.Ordinal));
        }
        finally { await app.StopAsync(); }
    }

    private sealed class Audit : IAuditLogService
    {
        public ConcurrentQueue<string> Actions { get; } = new();
        public void Log(string username, string action, string ip) => Actions.Enqueue(action);
        public (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50) => ([], 0);
    }
}
