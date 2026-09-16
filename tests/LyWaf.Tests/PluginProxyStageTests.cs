using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public class PluginProxyStageTests
{
    [Fact]
    public async Task PipelineSupportsLateEnableDisableAndReenable()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        var state = PluginState.Stopped;
        var calls = 0;
        var downstream = 0;
        PluginProxyStages.UseWhenActive(app, () => state, branch =>
            branch.Use(next => async ctx => { calls++; await next(ctx); }));
        app.Run(ctx => { downstream++; return Task.CompletedTask; });
        var pipeline = app.Build();
        foreach (var current in new[] { PluginState.Stopped, PluginState.Running, PluginState.Stopped, PluginState.Running, PluginState.Error })
        {
            state = current;
            await pipeline(new DefaultHttpContext());
        }
        Assert.Equal(2, calls);
        Assert.Equal(5, downstream);
    }
}
