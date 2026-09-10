using System.Net;
using System.Text.Json;
using LyWaf.Services.Incidents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LyWaf.Tests;

public sealed class SecurityIncidentTests
{
    [Fact]
    public async Task IncidentsAggregateWithoutCredentialsAndPersistDisposition()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-incidents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "events.json");
            using var incidents = new SecurityIncidentService(NullLogger<SecurityIncidentService>.Instance, path);
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Host = new HostString("example.test");
            context.Request.QueryString = new QueryString("?password=private-secret");
            context.Request.Headers.Authorization = "Bearer private-secret";
            context.Items["LyWaf.RuleId"] = "sql-injection";
            for (var i = 0; i < 20; i++) incidents.Record(context);
            var summary = Assert.Single(incidents.List());
            Assert.Equal(20, summary.Count);
            Assert.Empty(summary.Samples);
            var detail = incidents.Find(summary.Id)!;
            Assert.Equal(3, detail.Samples.Length);
            Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(detail));
            Assert.True(await incidents.SetDisposition(summary.Id, IncidentDisposition.FalsePositive, "确认误报", "operator", 0, default));
            Assert.False(await incidents.SetDisposition(summary.Id, IncidentDisposition.Ignored, "旧页面", "operator", 0, default));
            using var restored = new SecurityIncidentService(NullLogger<SecurityIncidentService>.Instance, path);
            await restored.StartAsync(default);
            var persisted = restored.Find(summary.Id)!;
            Assert.Equal(IncidentDisposition.FalsePositive, persisted.State);
            Assert.Equal("operator", Assert.Single(persisted.History).Operator);
            await restored.StopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FingerprintIsStableAndDoesNotHaveSeparatorCollisions()
    {
        Assert.Equal(SecurityIncidentService.Fingerprint("site", "rule"), SecurityIncidentService.Fingerprint("site", "rule"));
        Assert.NotEqual(SecurityIncidentService.Fingerprint("a|b", "c"), SecurityIncidentService.Fingerprint("a", "b|c"));
    }
}
