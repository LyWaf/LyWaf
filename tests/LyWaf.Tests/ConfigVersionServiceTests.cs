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
    public async Task ConditionalWriteRejectsStaleBaselineWithoutCreatingVersions()
    {
        var original = SharedData.ConfigFilePath;
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-conditional-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");
        SharedData.ConfigFilePath = path;
        try
        {
            await File.WriteAllTextAsync(path, "Feature: old\n");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var service = new ConfigVersionService(new HttpContextAccessor());
            await service.WriteMainIfUnchangedAsync(path, "Feature: first\n", "first", hash);
            var versions = await service.GetVersionsAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.WriteMainIfUnchangedAsync(path, "Feature: stale\n", "stale", hash));
            Assert.Equal("Feature: first\n", await File.ReadAllTextAsync(path));
            Assert.Equal(versions.Count, (await service.GetVersionsAsync()).Count);

            var currentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
            {
                try { await service.WriteMainIfUnchangedAsync(path, $"Feature: candidate{i}\n", "competing", currentHash); return true; }
                catch (InvalidOperationException) { return false; }
            }));
            Assert.Single(results, succeeded => succeeded);
        }
        finally
        {
            SharedData.ConfigFilePath = original;
            Directory.Delete(directory, true);
        }
    }

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
