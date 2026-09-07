using LyWaf.Services.Configuration;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

/// <summary>
/// 验证配置版本服务的候选校验、不可变归档和回滚保证。
/// </summary>
public sealed class ConfigVersionServiceTests
{
    [Fact]
    public async Task WriteRejectAndRollbackPreserveLastValidConfiguration()
    {
        var originalConfigPath = SharedData.ConfigFilePath;
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-config-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, "Logging:\n  LogLevel:\n    Default: Information\n");
        SharedData.ConfigFilePath = configPath;

        try
        {
            var context = new DefaultHttpContext();
            context.Items["Username"] = "test-operator";
            var accessor = new HttpContextAccessor { HttpContext = context };
            var service = new ConfigVersionService(accessor);

            var first = await service.WriteMainAsync(configPath,
                "Logging:\n  LogLevel:\n    Default: Debug\n", "test-write");
            var written = await File.ReadAllTextAsync(configPath);

            await Assert.ThrowsAnyAsync<Exception>(() => service.WriteMainAsync(configPath, "Logging: [", "invalid"));
            Assert.Equal(written, await File.ReadAllTextAsync(configPath));

            var history = await service.GetVersionsAsync();
            var baseline = Assert.Single(history, x => x.Action == "baseline");
            Assert.Equal("test-operator", first.Operator);
            Assert.NotNull(await service.GetDiffAsync(first.Id));

            var rollback = await service.RollbackAsync(baseline.Id);
            Assert.NotNull(rollback);
            Assert.Contains("Information", await File.ReadAllTextAsync(configPath));
            Assert.StartsWith("rollback:", rollback!.Action);
        }
        finally
        {
            SharedData.ConfigFilePath = originalConfigPath;
            Directory.Delete(directory, true);
        }
    }
}

/// <summary>
/// 验证配置生命周期分级以及路由、安全策略的重载冒烟检查。
/// </summary>
public sealed class ConfigurationLifecycleServiceTests
{
    [Fact]
    public void ReloadClassifiesChangesByRuntimeImpact()
    {
        var values = new Dictionary<string, string?>
        {
            ["Protect:Enabled"] = "true",
            ["ReverseProxy:Clusters:main:Destinations:one:Address"] = "http://localhost:8080",
            ["ReverseProxy:Routes:route1:ClusterId"] = "main",
            ["WafInfos:Listens:0:Port"] = "8080"
        };
        var root = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var service = new ConfigurationLifecycleService();
        var before = service.Capture(root);

        root["Protect:Enabled"] = "false";
        root["ReverseProxy:Routes:route1:Match:Path"] = "/api/{**catch-all}";
        root["WafInfos:Listens:0:Port"] = "8081";
        var impact = service.ReloadAndValidate(root, before);

        Assert.Contains("Protect", impact.Immediate);
        Assert.Contains("ReverseProxy", impact.RouteRebuild);
        Assert.Contains("WafInfos:Listens", impact.RestartRequired);
        Assert.True(impact.RequiresRestart);
    }

    [Fact]
    public void ValidationRejectsBrokenRouteAndRegex()
    {
        var service = new ConfigurationLifecycleService();
        var brokenRoute = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:route1:ClusterId"] = "missing"
        }).Build();
        Assert.Throws<InvalidDataException>(() => service.Validate(brokenRoute));

        var brokenRegex = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Protect:WafRules:bad:Field"] = "UriPath",
            ["Protect:WafRules:bad:Operator"] = "Regex",
            ["Protect:WafRules:bad:Value"] = "["
        }).Build();
        Assert.Throws<InvalidDataException>(() => service.Validate(brokenRegex));
    }

    [Fact]
    public void InvalidReloadRestoresActiveSnapshot()
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WafInfos:Listens:0:Port"] = "8080"
        }).Build();
        var service = new ConfigurationLifecycleService();
        var before = service.Capture(root);
        root["WafInfos:Listens:0:Port"] = "70000";

        Assert.Throws<InvalidDataException>(() => service.ReloadAndValidate(root, before));

        Assert.Equal("8080", root["WafInfos:Listens:0:Port"]);
    }
}
