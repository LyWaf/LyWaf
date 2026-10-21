using System.Reflection;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcCohortSchedulingTests
{
    [Fact]
    public async Task InFlightPeerDoesNotCauseRepeatedDecisionScansOrOverlappingRequests()
    {
        var runtime = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "a",
            StateDirectory = Path.Combine(Path.GetTempPath(), "cc-busy-" + Guid.NewGuid().ToString("N")),
            Peers = new() { ["b"] = new() { Endpoint = "https://b", ProtocolVersion = 2 } }
        });
        var communication = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, NodeId = "a", ClusterId = "cluster",
            Peers = new() { ["b"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('k', 32) },
                AllowedOperations = [CcExecutionQueryService.Operation] } }
        });
        using var concrete = new RuntimeStateStore(runtime);
        var store = DispatchProxy.Create<IRuntimeStateStore, StoreCalls>();
        var calls = (StoreCalls)(object)store; calls.Inner = concrete;
        using var identity = new NodeIdentityService(communication);
        var cohort = new CcCohortState(store, runtime, identity);
        var rule = new AdvancedCcRule { Id = "busy", Period = 1, ExpectedNodes = ["a", "b"] };
        var policy = new CcPolicyDescriptor(rule.Id, CcRuleStateKey.Revision(rule), "host:a.example", "192.0.2.1",
            CcRuleStateKey.Create(rule, "192.0.2.1", "host:a.example"), CcPolicyGeneration.Initial);
        cohort.Track(policy, rule);
        var statistics = DispatchProxy.Create<IStatisticService, RulesCalls>();
        var rules = (RulesCalls)(object)statistics; rules.Rule = rule;
        using var handler = new SuspendedHandler();
        using var client = new CcPeerQueryClient(communication, runtime, handler);
        using var monitor = new CcCohortMonitor(cohort, client, statistics, store);
        await monitor.StartAsync(default);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstTick = Volatile.Read(ref rules.Reads);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref rules.Reads) < firstTick + 5) await Task.Delay(10, deadline.Token);
            Assert.Equal(1, Volatile.Read(ref calls.ValueScans));
            Assert.Equal(1, Volatile.Read(ref handler.Requests));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await monitor.StopAsync(stop.Token);
        }
        Assert.NotNull(monitor.ExecuteTask);
        Assert.True(monitor.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref handler.Completed));
    }

    [Fact]
    public async Task IdleMonitorDoesNotScanStoreOnEveryTick()
    {
        var runtime = new StaticOptionsMonitor<RuntimeStateOptions>(new()
        {
            Enabled = true, NodeId = "a",
            StateDirectory = Path.Combine(Path.GetTempPath(), "cc-idle-" + Guid.NewGuid().ToString("N"))
        });
        var communication = new StaticOptionsMonitor<NodeCommunicationOptions>(new() { NodeId = "a" });
        using var concrete = new RuntimeStateStore(runtime);
        var store = DispatchProxy.Create<IRuntimeStateStore, StoreCalls>();
        var calls = (StoreCalls)(object)store;
        calls.Inner = concrete;
        using var identity = new NodeIdentityService(communication);
        var cohort = new CcCohortState(store, runtime, identity);
        var statistics = DispatchProxy.Create<IStatisticService, RulesCalls>();
        var rules = (RulesCalls)(object)statistics;
        using var client = new CcPeerQueryClient(communication, runtime);
        using var monitor = new CcCohortMonitor(cohort, client, statistics, store);
        var initialStatusReads = calls.StatusReads;
        await monitor.StartAsync(default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref rules.Reads) < 5) await Task.Delay(10, deadline.Token);
            Assert.Equal(initialStatusReads, Volatile.Read(ref calls.StatusReads));
            Assert.Equal(0, Volatile.Read(ref calls.ValueScans));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await monitor.StopAsync(stop.Token);
        }
        Assert.NotNull(monitor.ExecuteTask);
        Assert.True(monitor.ExecuteTask.IsCompletedSuccessfully);
    }

    public class StoreCalls : DispatchProxy
    {
        public IRuntimeStateStore Inner = null!;
        public int StatusReads;
        public int ValueScans;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IRuntimeStateStore.GetStatus)) Interlocked.Increment(ref StatusReads);
            if (targetMethod.Name == nameof(IRuntimeStateStore.GetValues)) Interlocked.Increment(ref ValueScans);
            return targetMethod.Invoke(Inner, args);
        }
    }

    public class RulesCalls : DispatchProxy
    {
        public int Reads;
        public AdvancedCcRule? Rule;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IStatisticService.GetAdvancedCcRules)) throw new NotSupportedException();
            Interlocked.Increment(ref Reads);
            return Rule is null ? new List<AdvancedCcRule>() : new List<AdvancedCcRule> { Rule };
        }
    }

    private sealed class SuspendedHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        public int Completed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Interlocked.Increment(ref Completed); }
            throw new InvalidOperationException("The request must end by cancellation.");
        }
    }
}
