using System.ComponentModel.DataAnnotations;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Sys;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LyWaf.Tests;

public class PluginConfigurationPublisherTests
{
    [Fact]
    public async Task MissingOrStaleVersionNeverWrites()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var plugin = new ImageProcessPlugin();
        using var scopes = new PluginScopedConfiguration(config, [plugin.Metadata], NullLogger<PluginScopedConfiguration>.Instance);
        var publisher = new PluginConfigurationPublisher(config, scopes);
        var stale = publisher.Version();
        config["Sites:images:Hosts:0"] = "example.com";
        foreach (var version in new[] { null, stale })
        {
            var result = await publisher.PublishAsync(plugin, version, new Dictionary<string, string> { ["DefaultQuality"] = "60" },
                _ => throw new Exception("Must not write"));
            Assert.False(result.Success);
            Assert.Equal("version_conflict", result.Status);
        }
    }

    [Fact]
    public async Task ConcurrentEditorsCannotOverwriteEachOther()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var plugin = new ImageProcessPlugin();
        using var scopes = new PluginScopedConfiguration(config, [plugin.Metadata], NullLogger<PluginScopedConfiguration>.Instance);
        var publisher = new PluginConfigurationPublisher(config, scopes);
        var version = publisher.Version();
        var writes = 0;
        var candidate = new Dictionary<string, string> { ["DefaultQuality"] = "60", ["SupportedFormats"] = "" };
        async Task Persist(Action validate)
        {
            Interlocked.Increment(ref writes);
            await Task.Delay(10);
            foreach (var field in candidate) config[$"PluginConfigSnapshots:image-process:{field.Key}"] = field.Value;
            config.Reload();
            validate();
        }
        var results = await Task.WhenAll(publisher.PublishAsync(plugin, version, candidate, Persist), publisher.PublishAsync(plugin, version, candidate, Persist));
        Assert.Equal(1, writes);
        Assert.Single(results, r => r.Status == "applied");
        Assert.Single(results, r => r.Status == "version_conflict");
        Assert.Equal("", scopes.Describe("image-process", "", "/").Config["SupportedFormats"]);
    }

    public class Options : IValidatableObject
    {
        public int Min { get; set; }
        public int Max { get; set; } = 100;
        public IEnumerable<ValidationResult> Validate(ValidationContext context)
        {
            if (Min > Max) yield return new ValidationResult("Min exceeds Max", [nameof(Min)]);
        }
    }
    private sealed class TestPlugin : LyWafPluginBase
    {
        public override PluginMetadata Metadata { get; } = new() { Id = "test", Name = "test", SupportsScopedConfiguration = true, DefaultOptions = new Options() };
    }

    [Fact]
    public async Task ValidGlobalObjectThatInvalidatesSiteIsRejectedBeforeWrite()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sites:images:Hosts:0"] = "example.com", ["Sites:images:Plugins:test:Min"] = "50"
        }).Build();
        var plugin = new TestPlugin();
        using var scopes = new PluginScopedConfiguration(config, [plugin.Metadata], NullLogger<PluginScopedConfiguration>.Instance);
        var publisher = new PluginConfigurationPublisher(config, scopes);
        var candidate = new Dictionary<string, string> { ["Max"] = "20" };
        Assert.Empty(PluginConfigurationSchema.Validate(typeof(Options), candidate));
        var result = await publisher.PublishAsync(plugin, publisher.Version(), candidate, _ => throw new Exception("Must not write"));
        Assert.Equal("validation_failed", result.Status);
        Assert.Equal("100", scopes.Describe("test", "example.com", "/").Config["Max"]);
    }

    [Fact]
    public async Task PersistenceWithoutActualActivationDoesNotReportSuccess()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var plugin = new ImageProcessPlugin();
        using var scopes = new PluginScopedConfiguration(config, [plugin.Metadata], NullLogger<PluginScopedConfiguration>.Instance);
        var publisher = new PluginConfigurationPublisher(config, scopes);
        var result = await publisher.PublishAsync(plugin, publisher.Version(), new Dictionary<string, string> { ["DefaultQuality"] = "60" },
            validate => { validate(); return Task.CompletedTask; });
        Assert.Equal("publish_failed", result.Status);
        Assert.False(result.Success);
        Assert.Equal("85", scopes.Describe("image-process", "", "/").Config["DefaultQuality"]);
    }
}
