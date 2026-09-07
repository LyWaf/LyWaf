using System.Diagnostics.Metrics;
using LyWaf.Plugins.Core;
using LyWaf.Services.Metrics;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeMetricsTests
{
    [Fact]
    public void ProxyAndAdminMetricsRenderFromSharedRegistry()
    {
        var metrics = new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new()));
        metrics.StartRequest("route-a").Complete("POST", 201, 12, 34);
        metrics.StartAdminRequest().Complete("GET", 200);

        var output = metrics.Render(false, new EmptyPluginManager());

        Assert.Contains("lywaf_http_request_body_bytes_total{route=\"route-a\"} 12", output);
        Assert.Contains("lywaf_http_response_body_bytes_total{route=\"route-a\"} 34", output);
        Assert.Contains("lywaf_admin_api_requests_total{method=\"GET\",code=\"200\"} 1", output);
        Assert.Contains("lywaf_admin_api_requests_in_flight 0", output);
    }

    [Fact]
    public async Task CountingStreamPassesThroughAndCountsWrites()
    {
        await using var target = new MemoryStream();
        await using var stream = new CountingWriteStream(target);

        stream.Write([1, 2]);
        await stream.WriteAsync(new byte[] { 3, 4, 5 });

        Assert.Equal(5, stream.BytesWritten);
        Assert.Equal([1, 2, 3, 4, 5], target.ToArray());
    }

    [Fact]
    public void ProxyCompletionPublishesMeterMeasurement()
    {
        long requestCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "LyWaf" && instrument.Name == "lywaf.http.requests")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "lywaf.http.requests") requestCount += measurement;
        });
        listener.Start();

        var metrics = new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new()));
        metrics.StartRequest("route-meter").Complete("GET", 204);

        Assert.Equal(1, requestCount);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class EmptyPluginManager : IPluginManager
    {
        public ILyWafPlugin? GetPlugin(string pluginId) => null;
        public IReadOnlyList<ILyWafPlugin> GetAllPlugins() => [];
        public IReadOnlyList<ILyWafPlugin> GetSystemPlugins() => [];
        public bool IsSystemPlugin(string pluginId) => false;
        public Task EnablePluginAsync(string pluginId) => Task.CompletedTask;
        public Task DisablePluginAsync(string pluginId) => Task.CompletedTask;
        public Task ReloadPluginAsync(string pluginId) => Task.CompletedTask;
        public PluginState GetPluginState(string pluginId) => PluginState.Unloaded;
    }
}
