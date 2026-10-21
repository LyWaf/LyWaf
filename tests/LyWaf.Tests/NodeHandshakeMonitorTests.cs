using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeHandshakeMonitorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "node-monitor-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackgroundHandshakeReconcilesAndFailureClosesActualCommandGate()
    {
        var config = new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "controller", Role = "Controller", StateDirectory = _root,
            Peers = new() { ["edge"] = new() { AllowedOperations = ["node.handshake", "node.status", "configuration.result"] } }
        });
        var distribution = new StaticOptionsMonitor<DistributionOptions>(new()
        {
            Enabled = true, Nodes = new() { ["edge"] = new() { ProtocolVersion = 2, Endpoint = "http://127.0.0.1:1" } }
        });
        var observer = new NodeRegistrationObserver(config);
        var probe = new Probe();
        var evidence = new EvidenceClient();
        var recovery = new NodeHandshakeRecoveryService(observer, evidence, config, probe);
        using var monitor = new NodeHandshakeMonitor(config, distribution, probe, observer, recovery,
            NullLogger<NodeHandshakeMonitor>.Instance);
        var factory = new NoHttp();
        var sender = new DistributionNodeClient(distribution, factory, config, observer);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Contains("reconciliation", blocked.Message);
        Assert.Equal(0, factory.Calls);

        var rollback = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.CommandAsync("edge", "rollback", new("deployment"), default));
        Assert.Contains("credentials", rollback.Message);
        var status = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.GetStatusAsync("edge", default));
        Assert.Contains("credentials", status.Message);

        await monitor.RefreshAsync(default);
        var ready = observer.Get("edge")!;
        Assert.True(observer.CanStartTasks("edge", ready.ObservationId));
        Assert.Equal(1, evidence.StatusCalls);
        Assert.Equal(0, evidence.CommandCalls);
        // The gate opens, but transport still independently requires valid credentials.
        var credentials = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Contains("credentials", credentials.Message);

        probe.Fail = true;
        await monitor.RefreshAsync(default);
        Assert.False(observer.CanStartTasks("edge", ready.ObservationId));
        blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.CommandAsync("edge", "commit", new("deployment"), default));
        Assert.Contains("reconciliation", blocked.Message);
        Assert.Equal(0, factory.Calls);
    }

    private sealed class Probe : INodeHandshakePeerProbe
    {
        public bool Fail;
        private readonly NodeHandshake _handshake = new("cluster", "edge", Guid.NewGuid().ToString("N"), "Node",
            DateTimeOffset.UtcNow.AddSeconds(-1), "1.0", [2], new("revision", new string('A', 64), "revision"),
            new(NodeHandshakeMetadataService.ComputeDigest([]), []));
        public Task<NodeHandshake> CaptureAsync(string peer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Fail ? Task.FromException<NodeHandshake>(new HttpRequestException("offline")) : Task.FromResult(_handshake);
        }
    }

    private sealed class EvidenceClient : IDistributionNodeClient
    {
        public int StatusCalls;
        public int CommandCalls;
        public IReadOnlyList<NodeOutboxRecord> ReadOutbox() => [];
        public Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken token)
        {
            StatusCalls++;
            return Task.FromResult(new DistributionNodeStatus(nodeId, "Node", "1.0", null, "revision", "Idle", DateTimeOffset.UtcNow));
        }
        public Task<NodeAcknowledgement> PrepareAsync(string id, PrepareConfigurationRequest request, CancellationToken token)
        { CommandCalls++; throw new InvalidOperationException("Must not execute"); }
        public Task<NodeAcknowledgement> CommandAsync(string id, string operation, DistributionCommandRequest request, CancellationToken token)
        { CommandCalls++; throw new InvalidOperationException("Must not execute"); }
        public Task<NodeDashboardSnapshot> GetDashboardAsync(string id, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) { Calls++; throw new InvalidOperationException("No network expected"); }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
