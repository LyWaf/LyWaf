using LyWaf.Plugins.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class PluginApiAdmissionGateTests
{
    [Fact]
    public void RejectsOversizedRequestBeforeTakingConcurrencySlot()
    {
        using var gate = CreateGate(new PluginApiProxyOptions { MaxRequestBodyBytes = 10, RequestsPerMinute = 2, MaxConcurrentRequests = 1 });
        var rejected = gate.TryAcquire("alice", "plugin", 11);
        var accepted = gate.TryAcquire("alice", "plugin", 10);

        Assert.False(rejected.Allowed);
        Assert.Equal("plugin_request_too_large", rejected.ErrorCode);
        Assert.True(accepted.Allowed);
        accepted.Lease!.Dispose();
    }

    [Fact]
    public void LimitsRatePerIdentityAndPlugin()
    {
        using var gate = CreateGate(new PluginApiProxyOptions { MaxRequestBodyBytes = 100, RequestsPerMinute = 1, MaxConcurrentRequests = 2 });
        var first = gate.TryAcquire("alice", "plugin-a", 0);
        first.Lease!.Dispose();

        var rejected = gate.TryAcquire("alice", "plugin-a", 0);
        var otherPlugin = gate.TryAcquire("alice", "plugin-b", 0);

        Assert.Equal("plugin_rate_limited", rejected.ErrorCode);
        Assert.True(otherPlugin.Allowed);
        otherPlugin.Lease!.Dispose();
    }

    [Fact]
    public void ReleasesConcurrencySlotWithLease()
    {
        using var gate = CreateGate(new PluginApiProxyOptions { MaxRequestBodyBytes = 100, RequestsPerMinute = 10, MaxConcurrentRequests = 1 });
        var first = gate.TryAcquire("alice", "plugin", 0);
        var blocked = gate.TryAcquire("bob", "plugin", 0);
        first.Lease!.Dispose();
        var next = gate.TryAcquire("bob", "plugin", 0);

        Assert.Equal("plugin_concurrency_limited", blocked.ErrorCode);
        Assert.True(next.Allowed);
        next.Lease!.Dispose();
    }

    private static PluginApiAdmissionGate CreateGate(PluginApiProxyOptions apiProxy) =>
        new(new StaticOptionsMonitor<PluginOptions>(new PluginOptions { ApiProxy = apiProxy }));

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
