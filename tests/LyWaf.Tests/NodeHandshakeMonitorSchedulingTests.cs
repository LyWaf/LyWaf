using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace LyWaf.Tests;

public sealed class NodeHandshakeMonitorSchedulingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TwentyPeersStayFreshPastObservationLifetimeWhileSlowRecoveryDoesNotStarvePending()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 8; i++) fixture.Client.Slow.Add(Fixture.Peer(i));
        await fixture.MakeReadyAsync("p19");
        using var monitor = fixture.Monitor();
        await monitor.StartAsync(default);
        var pendingReady = false;
        var maxAge = TimeSpan.Zero;
        TimeSpan? pendingAt = null;
        try
        {
            var elapsed = Stopwatch.StartNew();
            // Real clock and actual hosted loop: exceed the observer's 60-second freshness lease.
            while (elapsed.Elapsed < NodeRegistrationObserver.ObservationLifetime + TimeSpan.FromSeconds(10))
            {
                var healthy = fixture.Observer.Get("p19")!;
                Assert.True(fixture.Observer.CanStartTasks("p19", healthy.ObservationId));
                var age = DateTimeOffset.UtcNow - healthy.LastSeen;
                if (age > maxAge) maxAge = age;
                Assert.True(age < TimeSpan.FromSeconds(25));
                var pending = fixture.Observer.Get("p18");
                pendingReady |= pending is not null && fixture.Observer.CanStartTasks("p18", pending.ObservationId);
                if (pendingReady) pendingAt ??= elapsed.Elapsed;
                await Task.Delay(250);
            }
            Assert.True(pendingReady, "The pending peer behind slow work must get a recovery slot");
            Assert.True(fixture.Probe.Calls["p19"] >= 6);
            Assert.InRange(fixture.Client.MaxActive, 1, 4);
            Assert.Equal(1, fixture.Client.MaxPerPeer);
        }
        finally
        {
            await StopAsync(monitor);
            output.WriteLine($"20 peers: max healthy observation age={maxAge.TotalSeconds:F3}s; pending Ready at={pendingAt?.TotalSeconds:F3}s; max recovery workers={fixture.Client.MaxActive}; max same-peer={fixture.Client.MaxPerPeer}");
        }
        Assert.Equal(0, fixture.Client.Active);
        Assert.True(monitor.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task SaturatedQueueRotatesAdmissionAndStopAwaitsOwnedCancellationCleanup()
    {
        using var fixture = new Fixture();
        foreach (var peer in fixture.Config.CurrentValue.Peers.Keys) fixture.Client.Slow.Add(peer);
        fixture.Client.CleanupDelay = TimeSpan.FromMilliseconds(120);
        using var monitor = fixture.Monitor(fast: true, capacity: 2);
        await monitor.StartAsync(default);
        try
        {
            await UntilAsync(() => fixture.Client.Calls.Count == 20, TimeSpan.FromSeconds(15));
            Assert.InRange(fixture.Client.MaxActive, 1, 2);
            Assert.Equal(1, fixture.Client.MaxPerPeer);
            await UntilAsync(() => fixture.Client.Active > 0, TimeSpan.FromSeconds(2));
        }
        finally
        {
            await StopAsync(monitor);
            output.WriteLine($"Capacity=2: serviced={fixture.Client.Calls.Count}/20 peers; max workers={fixture.Client.MaxActive}; active after stop={fixture.Client.Active}");
        }
        Assert.Equal(0, fixture.Client.Active);
        Assert.True(monitor.ExecuteTask!.IsCompleted);
        var calls = fixture.Probe.Calls.Values.Sum();
        await Task.Delay(150);
        Assert.Equal(calls, fixture.Probe.Calls.Values.Sum());
    }

    [Fact]
    public async Task SlowHeartbeatBudgetsDoNotDisconnectHealthyReadyPeerOrRunRecoveryInHeartbeatSlots()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 4; i++) fixture.Probe.Slow.Add(Fixture.Peer(i));
        for (var i = 4; i < 18; i++) fixture.Client.Slow.Add(Fixture.Peer(i));
        await fixture.MakeReadyAsync("p19");
        using var monitor = fixture.Monitor(fast: true);
        await monitor.StartAsync(default);
        try
        {
            await UntilAsync(() => fixture.Probe.Calls.GetValueOrDefault("p19") >= 6, TimeSpan.FromSeconds(8));
            var healthy = fixture.Observer.Get("p19")!;
            Assert.True(fixture.Observer.CanStartTasks("p19", healthy.ObservationId));
            await UntilAsync(() => fixture.Observer.Get("p18")?.State == "Ready", TimeSpan.FromSeconds(8));
            Assert.True(fixture.Probe.Cancelled >= 4);
            Assert.Equal(1, fixture.Client.MaxPerPeer);
        }
        finally { await StopAsync(monitor); }
        Assert.Equal(0, fixture.Probe.Active);
        Assert.Equal(0, fixture.Client.Active);
    }

    private static async Task StopAsync(NodeHandshakeMonitor monitor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await monitor.StopAsync(timeout.Token);
        await monitor.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < timeout, "Condition did not converge before timeout");
            await Task.Delay(20);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "monitor-scheduling-" + Guid.NewGuid().ToString("N"));
        public StaticOptionsMonitor<NodeCommunicationOptions> Config { get; }
        private StaticOptionsMonitor<DistributionOptions> Distribution { get; }
        public NodeRegistrationObserver Observer { get; }
        public PeerProbe Probe { get; } = new();
        public Evidence Client { get; } = new();
        private NodeHandshakeRecoveryService Recovery { get; }
        public static string Peer(int index) => $"p{index:D2}";

        public Fixture()
        {
            Config = new(new() { Enabled = true, ClusterId = "cluster", NodeId = "controller", Role = "Controller", StateDirectory = _root });
            Distribution = new(new() { Enabled = true });
            for (var i = 0; i < 20; i++)
            {
                var id = Peer(i);
                Config.CurrentValue.Peers[id] = new() { AllowedOperations = ["node.handshake", "node.status", "configuration.result"] };
                Distribution.CurrentValue.Nodes[id] = new() { ProtocolVersion = 2, Endpoint = "https://" + id };
                Probe.Handshakes[id] = new("cluster", id, Guid.NewGuid().ToString("N"), "Node", DateTimeOffset.UtcNow,
                    "1.0", [2], new("revision", new string('a', 64), "revision"), new(NodeHandshakeMetadataService.ComputeDigest([]), []));
            }
            Observer = new(Config);
            Recovery = new(Observer, Client, Config, Probe);
        }

        public async Task MakeReadyAsync(string peer)
        {
            var now = DateTimeOffset.UtcNow;
            var observed = Observer.ObserveAuthenticated(new(2, "cluster", peer, "controller", Guid.NewGuid().ToString("D"),
                "heartbeat", "node.handshake", now, now.AddMinutes(1), JsonSerializer.SerializeToElement(Probe.Handshakes[peer], NodeMessageAuthentication.JsonOptions)));
            Assert.True((await Recovery.ReconcileAsync(peer, observed.ObservationId, default)).Ready);
        }

        public NodeHandshakeMonitor Monitor(bool fast = false, int capacity = 256) => new(Config, Distribution, Probe, Observer, Recovery,
            NullLogger<NodeHandshakeMonitor>.Instance)
        {
            HeartbeatInterval = fast ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromSeconds(10),
            HeartbeatBudget = fast ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromSeconds(2),
            RecoveryBudget = fast ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromSeconds(40),
            RecoveryCapacity = capacity
        };

        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class PeerProbe : INodeHandshakePeerProbe
    {
        public Dictionary<string, NodeHandshake> Handshakes { get; } = new();
        public HashSet<string> Slow { get; } = new();
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        public int Active, Cancelled;
        public async Task<NodeHandshake> CaptureAsync(string peer, CancellationToken token)
        {
            Interlocked.Increment(ref Active);
            Calls.AddOrUpdate(peer, 1, (_, n) => n + 1);
            try
            {
                if (Slow.Contains(peer)) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Handshakes[peer];
            }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    private sealed class Evidence : IDistributionNodeClient
    {
        public HashSet<string> Slow { get; } = new();
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        private readonly ConcurrentDictionary<string, int> _activePeers = new();
        public int Active, MaxActive, MaxPerPeer;
        public TimeSpan CleanupDelay;
        public IReadOnlyList<NodeOutboxRecord> ReadOutbox() => [];
        public async Task<DistributionNodeStatus> GetStatusAsync(string peer, CancellationToken token)
        {
            UpdateMax(ref MaxActive, Interlocked.Increment(ref Active));
            UpdateMax(ref MaxPerPeer, _activePeers.AddOrUpdate(peer, 1, (_, n) => n + 1));
            Calls.AddOrUpdate(peer, 1, (_, n) => n + 1);
            try
            {
                if (Slow.Contains(peer)) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(peer, "Node", "1.0", null, "revision", "Idle", DateTimeOffset.UtcNow);
            }
            finally
            {
                if (Slow.Contains(peer) && CleanupDelay > TimeSpan.Zero) await Task.Delay(CleanupDelay);
                _activePeers.AddOrUpdate(peer, 0, (_, n) => n - 1);
                Interlocked.Decrement(ref Active);
            }
        }
        private static void UpdateMax(ref int target, int value)
        {
            int previous;
            do { previous = Volatile.Read(ref target); if (previous >= value) return; }
            while (Interlocked.CompareExchange(ref target, value, previous) != previous);
        }
        public Task<NodeAcknowledgement> PrepareAsync(string peer, PrepareConfigurationRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<NodeAcknowledgement> CommandAsync(string peer, string operation, DistributionCommandRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<NodeDashboardSnapshot> GetDashboardAsync(string peer, CancellationToken token) => throw new NotSupportedException();
    }
}
