using System.Collections.Concurrent;
using System.Reflection;
using LyWaf.Plugins.Core;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LyWaf.Tests;

public sealed class PluginActivationConcurrencyTests
{
    [Fact]
    public async Task ConcurrentDisableStopsPluginOnceAndReturnsStopped()
    {
        var plugin = new SlowPlugin();
        var manager = CreateManager(plugin);
        var first = manager.DisablePluginAsync("test");
        await plugin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = manager.DisablePluginAsync("test");
        try { Assert.False(second.IsCompleted); }
        finally { plugin.Release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, plugin.StopCount);
        Assert.Equal(PluginState.Stopped, manager.GetPluginState("test"));
    }

    [Fact]
    public async Task ObsoleteReloadCannotOverwriteLatestDisabledChoice()
    {
        var manager = CreateManager(new SlowPlugin());
        typeof(PluginManager).GetField("_optionsRevision", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(manager, 2L);
        var stale = new PluginOptions { EnabledOverrides = { ["test"] = true } };
        var callback = typeof(PluginManager).GetMethod("OnOptionsChangedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)callback.Invoke(manager, [stale, 1L])!;
        Assert.False(manager.GetPluginEnabledState("test", false, true));
    }

    private static PluginManager CreateManager(SlowPlugin plugin)
    {
        var manager = new PluginManager(new ConfigurationBuilder().Build(), new PluginEventBus(),
            new StaticOptionsMonitor<PluginOptions>(new() { EnabledOverrides = { ["test"] = false } }));
        var plugins = (ConcurrentDictionary<string, PluginInfo>)typeof(PluginManager)
            .GetField("_plugins", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
        plugins["test"] = new() { Plugin = plugin, Context = null!, IsEnabled = true };
        return manager;
    }

    private sealed class SlowPlugin : LyWafPluginBase
    {
        public SlowPlugin() { State = PluginState.Running; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCount;
        public override PluginMetadata Metadata => new() { Id = "test", Name = "Test" };
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StopCount);
            Entered.TrySetResult();
            await Release.Task;
            await base.StopAsync(cancellationToken);
        }
    }
}
