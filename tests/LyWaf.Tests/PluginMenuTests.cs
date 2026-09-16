using LyWaf.Plugins.Core;
using LyWaf.Plugins.Sys;
using Xunit;

namespace LyWaf.Tests;

public sealed class PluginMenuTests
{
    [Theory]
    [InlineData(PluginState.Running, 1)]
    [InlineData(PluginState.Initialized, 1)]
    [InlineData(PluginState.Stopped, 0)]
    [InlineData(PluginState.Unloaded, 0)]
    [InlineData(PluginState.Error, 0)]
    public void AnalysisOwnsCcMenuOnlyWhileActive(PluginState state, int count)
    {
        var entries = PluginMenuCatalog.GetEntries(new AnalysisPlugin(), state).ToArray();
        Assert.Equal(count, entries.Length);
        if (count > 0) Assert.Equal("/plugins/analysis/cc", entries[0].Path);
    }

    [Theory]
    [InlineData("/plugins/test/page", true)]
    [InlineData("/plugins/test", true)]
    [InlineData("/plugins/other/page", false)]
    [InlineData("https://example.com", false)]
    [InlineData("/plugins/test/../other", false)]
    [InlineData("/plugins/test/%2e%2e/other", false)]
    public void MenuPathsStayInsideOwningPlugin(string path, bool valid)
    {
        Assert.Equal(valid, PluginMenuCatalog.GetEntries(new TestPlugin(path), PluginState.Running).Any());
    }

    private sealed class TestPlugin(string path) : LyWafPluginBase
    {
        public override PluginMetadata Metadata => new() { Id = "test", Name = "Test", Version = "1.0" };
        public override IReadOnlyList<PluginAction> GetActions() =>
            [new() { Id = "page", Name = "Page", Type = "page-link", Path = path }];
    }
}
