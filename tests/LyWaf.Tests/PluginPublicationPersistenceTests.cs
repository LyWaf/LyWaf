using System.Reflection;
using LyWaf.Config;
using LyWaf.Control;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Sys;
using LyWaf.Services;
using LyWaf.Services.Configuration;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LyWaf.Tests;

public class PluginPublicationPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealPatchPublicationAndRejectedActivationRestoreDisk(bool existing, bool reject)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-plugin-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldPath = SharedData.ConfigFilePath;
        var locatorField = typeof(ServiceLocator).GetField("_serviceProvider", BindingFlags.NonPublic | BindingFlags.Static)!;
        var oldProvider = locatorField.GetValue(null);
        try
        {
            SharedData.ConfigFilePath = Path.Combine(directory, "config.ly");
            var path = LbPatchConfig.GetPatchFilePath();
            const string original = "{\"PluginConfigSnapshots\":{\"image-process\":{\"DefaultQuality\":\"80\"}}}";
            if (existing) await File.WriteAllTextAsync(path, original);
            using var config = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Plugins:image-process:SupportedFormats:0"] = "jpg", ["Plugins:image-process:SupportedFormats:1"] = "webp"
            }).Add(new LbPatchSource(path, validateSource: false)).Build();
            using var services = new ServiceCollection().AddSingleton(new ConfigVersionService(new HttpContextAccessor()))
                .AddSingleton<ConfigurationLifecycleService>().BuildServiceProvider();
            ServiceLocator.Initialize(services);
            var plugin = new ImageProcessPlugin();
            using var scopes = new PluginScopedConfiguration(config, [plugin.Metadata], NullLogger<PluginScopedConfiguration>.Instance);
            var publisher = new PluginConfigurationPublisher(config, scopes);
            var candidate = new Dictionary<string, string> { ["DefaultQuality"] = "60", ["SupportedFormats:0"] = "png" };
            var method = typeof(ControlApi).GetMethod("PersistPluginConfigPatch", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = await publisher.PublishAsync(plugin, publisher.Version(), candidate, validate =>
                (Task)method.Invoke(null, [config, "image-process", candidate, true, (Action)(() => {
                    validate();
                    if (reject) throw new InvalidOperationException("Simulated activation rejection");
                })])!);
            if (reject)
            {
                Assert.Equal("publish_failed", result.Status);
                Assert.Equal(existing, File.Exists(path));
                if (existing) Assert.Equal(original, await File.ReadAllTextAsync(path));
                Assert.Equal(existing ? "80" : "85", scopes.Describe("image-process", "", "/").Config["DefaultQuality"]);
            }
            else
            {
                Assert.Equal("applied", result.Status);
                config.Reload();
                using var restarted = new PluginScopedConfiguration(config, [plugin.Metadata], NullLogger<PluginScopedConfiguration>.Instance);
                var current = restarted.Describe("image-process", "", "/").Config;
                Assert.Equal("60", current["DefaultQuality"]);
                Assert.Equal("png", current["SupportedFormats:0"]);
                Assert.False(current.ContainsKey("SupportedFormats:1"));
            }
        }
        finally
        {
            locatorField.SetValue(null, oldProvider);
            SharedData.ConfigFilePath = oldPath;
            Directory.Delete(directory, true);
        }
    }
}
