using LyWaf.Services.Sites;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagedSiteTests
{
    [Fact]
    public async Task SiteChangesArePublishedToRealYarpAndPersisted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-sites-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var route = new RouteConfig { RouteId = "original", ClusterId = "backend", Match = new RouteMatch { Hosts = ["example.test"], Path = "/{**path}" } };
            builder.Services.AddReverseProxy().LoadFromMemory([route], [new ClusterConfig
            {
                ClusterId = "backend", Destinations = new Dictionary<string, DestinationConfig> { ["one"] = new() { Address = "http://127.0.0.1:1" } }
            }]);
            builder.Services.AddManagedSites(Path.Combine(directory, "sites.json"));
            await using var app = builder.Build();
            app.MapReverseProxy();
            await app.StartAsync();
            var provider = app.Services.GetRequiredService<ManagedSiteProvider>();
            var original = Assert.Single(provider.List());
            var logs = new List<string>();
            await provider.PublishAsync([original with { Enabled = false }], "test", logs.Add, default);
            Assert.Empty(provider.GetConfig().Routes);
            Assert.False(Assert.Single(provider.List()).Enabled);
            await provider.PublishAsync([original], "test", logs.Add, default);
            Assert.Single(provider.GetConfig().Routes);
            var saved = File.ReadAllText(Path.Combine(directory, "sites.json"));
            await Assert.ThrowsAsync<ArgumentException>(() => provider.PublishAsync([original with { Route = route with { ClusterId = "missing" } }], "test", logs.Add, default));
            Assert.Equal(saved, File.ReadAllText(Path.Combine(directory, "sites.json")));
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.PublishAsync([original with { Enabled = false }], "test", line =>
            {
                if (line.StartsWith("发布站点配置")) cancellation.Cancel();
            }, cancellation.Token));
            Assert.True(Assert.Single(provider.List()).Enabled);
            Assert.Equal(saved, File.ReadAllText(Path.Combine(directory, "sites.json")));
            await provider.PublishAsync([original with { Deleted = true }], "test", logs.Add, default);
            Assert.Empty(provider.List());
            Assert.Empty(provider.GetConfig().Routes);
            Assert.Contains(logs, line => line.Contains("已生效"));
            await app.StopAsync();
        }
        finally { Directory.Delete(directory, true); }
    }
}
