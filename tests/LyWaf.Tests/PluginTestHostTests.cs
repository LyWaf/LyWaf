using LyWaf.Plugins.Core;
using LyWaf.Plugins.Core.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class PluginTestHostTests
{
    [Fact]
    public async Task ExercisesLifecyclePipelineApiAndCleanup()
    {
        var plugin = new TestPlugin();
        await using (var host = new PluginTestHost(plugin))
        {
            await host.StartAsync();
            var pipeline = host.BuildProxyPipeline(context =>
            {
                plugin.Calls.Add("terminal"); return Task.CompletedTask;
            });
            await pipeline(new DefaultHttpContext());
            var response = await host.ExecuteApiAsync("status");
            Assert.Equal(200, response.Response.StatusCode);
            await host.StopAsync();
        }
        Assert.Equal(["initialize", "start", "middleware-before", "terminal", "middleware-after", "api", "stop"], plugin.Calls);
    }

    [Fact]
    public async Task SlowLifecycleOperationTimesOut()
    {
        await using var host = new PluginTestHost(new SlowPlugin(), timeout: TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<TimeoutException>(() => host.StartAsync());
    }

    [Fact]
    public void LargePayloadIsGeneratedLazily()
    {
        using var stream = PluginTestPayloads.CreateGeneratedStream(1024L * 1024 * 1024);
        Span<byte> sample = stackalloc byte[32];
        Assert.Equal(32, stream.Read(sample));
        Assert.All(sample.ToArray(), value => Assert.Equal(0x5a, value));
        Assert.Equal(1024L * 1024 * 1024, stream.Length);
    }

    [Fact]
    public async Task BoundedQueueRejectsOverflowAndDrainsOnCompletion()
    {
        var queue = new PluginTestQueue<int>(2);
        Assert.True(queue.TryWrite(1));
        Assert.True(queue.TryWrite(2));
        Assert.False(queue.TryWrite(3));
        queue.Complete();
        var values = new List<int>();
        await foreach (var value in queue.ReadAllAsync()) values.Add(value);
        Assert.Equal([1, 2], values);
    }

    private sealed class TestPlugin : LyWafPluginBase, IPluginApiHandler
    {
        public List<string> Calls { get; } = [];
        public override PluginMetadata Metadata { get; } = new() { Id = "test", Name = "Test" };
        public override async Task InitializeAsync(IPluginContext context) { Calls.Add("initialize"); await base.InitializeAsync(context); }
        public override async Task StartAsync(CancellationToken token) { Calls.Add("start"); await base.StartAsync(token); }
        public override async Task StopAsync(CancellationToken token) { Calls.Add("stop"); await base.StopAsync(token); }
        public override void ConfigureProxyPipeline(IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            Calls.Add("middleware-before"); await next(); Calls.Add("middleware-after");
        });
        public Task<IResult> HandleApiAsync(string path, HttpContext context)
        {
            Calls.Add("api"); return Task.FromResult(Results.Ok(new { path }));
        }
    }

    private sealed class SlowPlugin : LyWafPluginBase
    {
        public override PluginMetadata Metadata { get; } = new() { Id = "slow", Name = "Slow" };
        public override async Task InitializeAsync(IPluginContext context)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
    }
}
