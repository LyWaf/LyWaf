using LyWaf.Config;
using LyWaf.Services.Distribution;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

/// <summary>保证公开配置示例中的多节点配置能够被实际解析和绑定。</summary>
public sealed class DistributionConfigurationTests
{
    [Fact]
    public void ExampleBindsDashboardPermissions()
    {
        var examplePath = Path.Combine(AppContext.BaseDirectory, "config.ly.example");
        var configuration = new ConfigurationBuilder().AddLyConfig(examplePath, optional: false).Build();
        var options = configuration.GetSection("Distribution").Get<DistributionOptions>();

        Assert.NotNull(options);
        Assert.Contains("admin", options.Permissions.GlobalOperators);
        Assert.Equal(["east-operator"], options.Permissions.NodeOperators["edge-01"]);
    }
}
