using System.ComponentModel.DataAnnotations;
using System.Reflection;
using LyWaf.Control;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Sys;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public class PluginConfigurationSchemaTests
{
    [Theory]
    [InlineData("DefaultQuality", "0")]
    [InlineData("DefaultQuality", "101")]
    [InlineData("Quality", "101")]
    [InlineData("Enabled", "not-bool")]
    [InlineData("MaxWidth", "bad-number")]
    [InlineData("MaxWidth", "-1")]
    [InlineData("MissingField", "true")]
    [InlineData("SupportedFormats:2048", "png")]
    [InlineData("SupportedFormats:-1", "png")]
    [InlineData("SupportedFormats:01", "png")]
    public void InvalidCandidateIsRejected(string key, string value)
    {
        Assert.NotEmpty(PluginConfigurationSchema.Validate(typeof(ImageProcessOptions), new Dictionary<string, string> { [key] = value }));
    }

    [Fact]
    public void SchemaIncludesBoundsAndSensitiveReloadMetadata()
    {
        var quality = Assert.Single(PluginConfigurationSchema.Describe(typeof(ImageProcessOptions)), f => f.Path == "DefaultQuality");
        Assert.Equal("1", quality.Minimum);
        Assert.Equal("100", quality.Maximum);
        var secret = Assert.Single(PluginConfigurationSchema.Describe(typeof(Options)), f => f.Path == "Credential");
        Assert.True(secret.Sensitive);
        var directory = Assert.Single(PluginConfigurationSchema.Describe(typeof(Options)), f => f.Path == "Directory");
        Assert.True(directory.RequiresRestart);
        Assert.True(PluginConfigurationSchema.SensitivePath(typeof(Options), "Rules:0:Credential"));
        Assert.True(PluginConfigurationSchema.SensitivePath(typeof(Options), "Rules:Credential"));
    }

    [Fact]
    public void NestedCollectionsAndConfigurationAliasesAreValidated()
    {
        Assert.Empty(PluginConfigurationSchema.Validate(typeof(Options), new Dictionary<string, string>
        {
            ["renamed"] = "4", ["Rules:0:Limit"] = "5", ["Headers:Accept"] = "application/json", ["Codes:0"] = "200"
        }));
        Assert.NotEmpty(PluginConfigurationSchema.Validate(typeof(Options), new Dictionary<string, string> { ["Rules:0:Limit"] = "-2" }));
        Assert.NotEmpty(PluginConfigurationSchema.Validate(typeof(Options), new Dictionary<string, string> { ["Rules:0:Unknown"] = "1" }));
        Assert.NotEmpty(PluginConfigurationSchema.Validate(typeof(Options), new Dictionary<string, string> { ["renamed"] = "2", ["Renamed"] = "3" }));
    }

    [Fact]
    public void PreflightRetainsSecretsAndNeverMutatesLiveOptions()
    {
        var plugin = new TestPlugin();
        var errors = Prepare(plugin, new() { ["renamed"] = "5", ["Directory"] = "data" }, out var candidate);
        Assert.Empty(errors);
        Assert.Equal("private-value", candidate["Credential"]);
        Assert.Equal(2, ((Options)plugin.Metadata.DefaultOptions!).Number);
        Assert.NotEmpty(Prepare(plugin, new() { ["Credential"] = "changed" }, out _));
        Assert.NotEmpty(Prepare(plugin, new() { ["Directory"] = "other" }, out _));
        Assert.Equal("data", ((Options)plugin.Metadata.DefaultOptions!).Directory);
    }

    private static Dictionary<string, string[]> Prepare(ILyWafPlugin plugin, Dictionary<string, string> body, out Dictionary<string, string> candidate)
    {
        object?[] arguments = [plugin, body, null];
        var result = (Dictionary<string, string[]>)typeof(ControlApi).GetMethod("PreparePluginConfiguration", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments)!;
        candidate = (Dictionary<string, string>)arguments[2]!;
        return result;
    }

    public class Options
    {
        [Required, PluginConfiguration(Sensitive = true)] public string Credential { get; set; } = "private-value";
        [PluginConfiguration(RequiresRestart = true)] public string Directory { get; set; } = "data";
        [ConfigurationKeyName("renamed"), Range(1, 10)] public int Number { get; set; } = 2;
        public List<Rule> Rules { get; set; } = [];
        public Dictionary<string, string> Headers { get; set; } = [];
        public int[] Codes { get; set; } = [201];
    }
    public class Rule
    {
        [Range(1, 10)] public int Limit { get; set; } = 1;
        [PluginConfiguration(Sensitive = true)] public string Credential { get; set; } = "hidden";
    }
    private class TestPlugin : LyWafPluginBase
    {
        public override PluginMetadata Metadata { get; } = new() { Id = "schema-test", Name = "Test", DefaultOptions = new Options() };
    }
}
