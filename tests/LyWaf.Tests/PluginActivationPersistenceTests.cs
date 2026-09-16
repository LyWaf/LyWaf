using System.Text.Json;
using LyWaf.Config;
using LyWaf.Plugins.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class PluginActivationPersistenceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SavedChoiceSurvivesFreshConfigurationAndManager(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), "lywaf-plugin-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                PluginEnabledStates = new Dictionary<string, bool> { ["analysis"] = enabled },
                PluginConfigs = new { analysis = new { interval = 123 } }
            }));
            for (var restart = 0; restart < 2; restart++)
            {
                using var config = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Plugins:PluginConfigs:analysis:enable"] = (!enabled).ToString()
                }).Add(new LbPatchSource(path, validateSource: false)).Build();
                var options = config.GetSection("Plugins").Get<PluginOptions>()!;
                options.DisabledPlugins.Add("analysis");
                var manager = new PluginManager(config, new PluginEventBus(), new StaticOptionsMonitor<PluginOptions>(options));
                Assert.Equal(enabled, manager.GetPluginEnabledState("analysis", true, true));
                Assert.Equal("123", config["Plugins:analysis:interval"]);
                Assert.True(manager.GetPluginEnabledState("unmodified", false, true));
            }
        }
        finally { File.Delete(path); }
    }
}
