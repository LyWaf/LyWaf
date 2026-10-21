using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeHandshakeProbeFailureTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("same-id-heartbeat")]
    [InlineData("reconciled")]
    [InlineData("new-id")]
    public async Task LateProbeFailureCannotOverwriteNewSuccess(string transition)
    {
        using var f = new Fixture();
        var before = transition == "missing" ? null : f.Observe(ready: transition != "reconciled");
        var running = f.Monitor.RefreshAsync(default);
        await f.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        NodeRegistrationObservation fresh;
        if (transition == "reconciled")
        {
            Assert.True(f.Observer.CompleteReconciliation("edge", before!.ObservationId, "new-result"));
            fresh = f.Observer.Get("edge")!;
        }
        else fresh = f.Observe(ready: true, revision: transition == "new-id" ? "changed" : "revision");
        if (transition == "same-id-heartbeat")
        {
            Assert.Equal(before!.ObservationId, fresh.ObservationId);
            Assert.Equal(before.LastSeen, fresh.LastSeen); // Even identical clock ticks must be fenced.
        }
        f.Probe.Release.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JsonSerializer.Serialize(fresh), JsonSerializer.Serialize(f.Observer.Get("edge")));
        Assert.True(f.Observer.CanStartTasks("edge", fresh.ObservationId));
    }

    [Theory]
    [InlineData("ClusterId")]
    [InlineData("NodeId")]
    [InlineData("StateDirectory")]
    public async Task LateFailureCannotInvalidateAnotherIdentityOrPoisonTheRestoredIdentity(string field)
    {
        using var f = new Fixture();
        var original = f.Options.CurrentValue;
        var old = f.Observe(ready: true);
        var running = f.Monitor.RefreshAsync(default);
        await f.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = f.Configuration();
        if (field == "ClusterId") next.ClusterId = "new-cluster";
        if (field == "NodeId") next.NodeId = "new-controller";
        if (field == "StateDirectory") next.StateDirectory = Path.Combine(original.StateDirectory, "new-identity");
        f.Options.Value = next;
        // Observer-level race fixture, not a claim that a leased host can change identity without restart.
        var fresh = f.Observe(ready: true);
        f.Probe.Release.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JsonSerializer.Serialize(fresh), JsonSerializer.Serialize(f.Observer.Get("edge")));
        Assert.True(f.Observer.CanStartTasks("edge", fresh.ObservationId));
        f.Options.Value = original;
        Assert.True(f.Observer.CanStartTasks("edge", old.ObservationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryFailureStillClosesExistingGateWithoutPoisoningAbsentObservation(bool existing)
    {
        using var f = new Fixture();
        var old = existing ? f.Observe(ready: true) : null;
        var running = f.Monitor.RefreshAsync(default);
        await f.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Probe.Release.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        if (existing)
        {
            Assert.Equal("NeedsReconciliation", f.Observer.Get("edge")!.State);
            Assert.False(f.Observer.CanStartTasks("edge", old!.ObservationId));
        }
        else Assert.Null(f.Observer.Get("edge"));
        var fresh = f.Observe(ready: true);
        Assert.True(f.Observer.CanStartTasks("edge", fresh.ObservationId));
    }

    [Fact]
    public void MatchingFailureFencesEvenWhenPersistenceFailsUntilFreshHandshake()
    {
        using var f = new Fixture();
        var old = f.Observe(ready: true);
        var started = f.Observer.CaptureProbe("edge");
        f.Observer.BeforePersist = () => throw new IOException("injected failure");
        Assert.Throws<IOException>(() => f.Observer.MarkDisconnected(started));
        Assert.Equal("Ready", f.Observer.Get("edge")!.State);
        Assert.False(f.Observer.CanStartTasks("edge", old.ObservationId));
        Assert.False(f.Observer.CompleteReconciliation("edge", old.ObservationId, "stale"));
        f.Observer.BeforePersist = null;
        var fresh = f.Observe(ready: true);
        Assert.NotEqual(old.ObservationId, fresh.ObservationId);
        Assert.True(f.Observer.CanStartTasks("edge", fresh.ObservationId));
        Assert.False(f.Observer.MarkDisconnected(started));
    }

    [Fact]
    public void IdentityChangingDuringPersistenceCannotTransferMemoryFenceToNewNamespace()
    {
        using var f = new Fixture();
        var oldConfig = f.Options.CurrentValue;
        f.Observe(ready: true);
        var started = f.Observer.CaptureProbe("edge");
        var newConfig = f.Configuration();
        newConfig.NodeId = "other-controller";
        f.Observer.BeforePersist = () =>
        {
            f.Options.Value = newConfig;
            f.Observer.BeforePersist = null;
        };
        Assert.True(f.Observer.MarkDisconnected(started));
        var fresh = f.Observe(ready: true);
        Assert.True(f.Observer.CanStartTasks("edge", fresh.ObservationId));
        f.Options.Value = oldConfig;
        var failed = f.Observer.Get("edge")!;
        Assert.Equal("NeedsReconciliation", failed.State);
        Assert.False(f.Observer.CanStartTasks("edge", failed.ObservationId));
        Assert.False(f.Observer.CompleteReconciliation("edge", failed.ObservationId, "stale"));
        Assert.True(f.Observer.CanStartTasks("edge", f.Observe(ready: true).ObservationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyRecordWithoutUpdateIdRemainsReadableAndRefreshInvalidatesOldProbe(bool refreshed)
    {
        using var f = new Fixture();
        var old = f.Observe(ready: true);
        var path = f.Observer.CaptureProbe("edge").Path;
        File.WriteAllText(path, JsonSerializer.Serialize(old with { UpdateId = null }));
        var started = f.Observer.CaptureProbe("edge");
        Assert.Null(started.UpdateId);
        var current = refreshed ? f.Observe(ready: true) : f.Observer.Get("edge")!;
        Assert.Equal(!refreshed, f.Observer.MarkDisconnected(started));
        Assert.Equal(refreshed, f.Observer.CanStartTasks("edge", current.ObservationId));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "probe-failure-" + Guid.NewGuid().ToString("N"));
        private readonly Clock _clock = new();
        private readonly string _boot = Guid.NewGuid().ToString("N");
        public SwappableOptions Options { get; }
        public NodeRegistrationObserver Observer { get; }
        public FailingProbe Probe { get; } = new();
        public NodeHandshakeMonitor Monitor { get; }
        public Fixture()
        {
            Options = new(Configuration());
            Observer = new(Options, _clock);
            var distribution = new StaticOptionsMonitor<DistributionOptions>(new()
            { Enabled = true, Nodes = new() { ["edge"] = new() { ProtocolVersion = 2, Endpoint = "https://edge" } } });
            var recovery = new NodeHandshakeRecoveryService(Observer, new NoRecovery(), Options, Probe);
            Monitor = new(Options, distribution, Probe, Observer, recovery, NullLogger<NodeHandshakeMonitor>.Instance)
            { HeartbeatBudget = TimeSpan.FromSeconds(5) };
        }
        public NodeCommunicationOptions Configuration() => new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "controller", Role = "Controller", StateDirectory = _root,
            Peers = new() { ["edge"] = new() { AllowedOperations = ["node.handshake", "node.status"] } }
        };
        public NodeRegistrationObservation Observe(bool ready, string revision = "revision")
        {
            var config = Options.CurrentValue;
            var remote = new NodeHandshake(config.ClusterId, "edge", _boot, "Node", _clock.Now.AddSeconds(-1), "1.0", [2],
                new(revision, new string('a', 64), revision), new(NodeHandshakeMetadataService.ComputeDigest([]), []));
            var observed = Observer.ObserveAuthenticated(new(2, config.ClusterId, "edge", config.NodeId,
                Guid.NewGuid().ToString("D"), "handshake", "node.handshake", _clock.Now, _clock.Now.AddMinutes(1),
                JsonSerializer.SerializeToElement(remote, NodeMessageAuthentication.JsonOptions)));
            if (ready) Assert.True(Observer.CompleteReconciliation("edge", observed.ObservationId, "result"));
            return Observer.Get("edge")!;
        }
        public void Dispose()
        {
            Monitor.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
    private sealed class SwappableOptions(NodeCommunicationOptions value) : IOptionsMonitor<NodeCommunicationOptions>
    {
        public NodeCommunicationOptions Value = value;
        public NodeCommunicationOptions CurrentValue => Volatile.Read(ref Value);
        public NodeCommunicationOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<NodeCommunicationOptions, string?> listener) => null;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FailingProbe : INodeHandshakePeerProbe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<NodeHandshake> CaptureAsync(string peer, CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            throw new HttpRequestException("late failure");
        }
    }
    private sealed class NoRecovery : IDistributionNodeClient
    {
        public Task<DistributionNodeStatus> GetStatusAsync(string peer, CancellationToken token) => throw new NotSupportedException();
        public Task<NodeAcknowledgement> PrepareAsync(string peer, PrepareConfigurationRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<NodeAcknowledgement> CommandAsync(string peer, string operation, DistributionCommandRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<NodeDashboardSnapshot> GetDashboardAsync(string peer, CancellationToken token) => throw new NotSupportedException();
    }
}
