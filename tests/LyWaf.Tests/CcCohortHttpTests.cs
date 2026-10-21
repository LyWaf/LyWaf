using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcCohortHttpTests
{
    [Fact]
    public async Task ResponseBodyStallIsBoundedWithoutHostCancellation()
    {
        await using var node = await Node.Start("a");
        node.Runtime.Peers["b"].Endpoint = "http://localhost";
        using var client = new CcPeerQueryClient(new StaticOptionsMonitor<NodeCommunicationOptions>(node.Communication),
            new StaticOptionsMonitor<RuntimeStateOptions>(node.Runtime), new StalledBodyHandler());
        var window = CcDecisionService.Window(DateTimeOffset.UtcNow, node.Rule.Period);
        using var cleanup = new CancellationTokenSource();
        var probe = client.ProbeAsync("b", new(node.Policy, CcDecisionService.DecisionId(node.Policy, window), window), node.Cohort, cleanup.Token);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.False(cleanup.IsCancellationRequested);
            Assert.True(probe.IsCompleted);
            Assert.Contains("b", node.Cohort.Read(node.Policy, node.Rule).Coverage.MissingNodes);
        }
        finally
        {
            cleanup.Cancel();
            try { await probe; } catch (OperationCanceledException) { }
        }
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledBody()) });
    }

    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task RealRouteBatchBindsEverySubjectAndRejectsDuplicateSubjects()
    {
        await using var a = await Node.Start("a"); await using var b = await Node.Start("b");
        a.Runtime.Peers["b"].Endpoint = b.Url;
        var window = CcDecisionService.Window(DateTimeOffset.UtcNow, a.Rule.Period);
        var queries = Enumerable.Range(1, 64).Select(i =>
        {
            var policy = a.Decisions.Register(a.Rule, "host:a.example", "192.0.2." + i);
            return new CcExecutionQuery(policy, CcDecisionService.DecisionId(policy, window), window);
        }).ToArray();
        await a.Client.ProbeBatchAsync("b", queries, a.Cohort, default);
        await Assert.ThrowsAsync<HttpRequestException>(() => a.Client.ProbeBatchAsync("b", [queries[0], queries[0]], a.Cohort, default));
    }

    [Fact]
    public async Task SlowPeerDoesNotHoldHealthyPeerBehindWholeRoundBarrier()
    {
        await using var a = await Node.Start("a"); await using var b = await Node.Start("b"); await using var c = await Node.Start("c");
        a.Runtime.Peers["b"].Endpoint = b.Url; a.Runtime.Peers["c"].Endpoint = c.Url;
        a.Client.Dispose();
        a.Client = new(new StaticOptionsMonitor<NodeCommunicationOptions>(a.Communication),
            new StaticOptionsMonitor<RuntimeStateOptions>(a.Runtime), new SlowPeer(new Uri(c.Url).Port));
        a.Cohort.ObserveRules([a.Rule]); b.Cohort.ObserveRules([b.Rule]);
        var start = CcDecisionService.Window(DateTimeOffset.UtcNow, a.Rule.Period).AddSeconds(a.Rule.Period);
        await Task.Delay(start - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(20));
        await a.StartEvaluation();
        await Task.Delay(1100);
        var observed = a.Cohort.Read(a.Policy, a.Rule);
        Assert.Contains("c", observed.Coverage.MissingNodes);
        Assert.DoesNotContain("b", observed.Coverage.MissingNodes);
    }

    private sealed class SlowPeer(int port) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Port == port) { await Task.Delay(1800, token); throw new HttpRequestException("partition"); }
            return await base.SendAsync(request, token);
        }
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("requestId")]
    [InlineData("boot")]
    [InlineData("timing")]
    public async Task ActualRouteReplyTamperingFailsClosed(string fault)
    {
        await using var a = await Node.Start("a"); await using var b = await Node.Start("b");
        a.Runtime.Peers["b"].Endpoint = b.Url;
        using var client = new CcPeerQueryClient(new StaticOptionsMonitor<NodeCommunicationOptions>(a.Communication),
            new StaticOptionsMonitor<RuntimeStateOptions>(a.Runtime), new CorruptReply(fault));
        var window = CcDecisionService.Window(DateTimeOffset.UtcNow, a.Rule.Period);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ProbeAsync("b",
            new(a.Policy, CcDecisionService.DecisionId(a.Policy, window), window), a.Cohort, default));
        Assert.Contains("b", a.Cohort.Read(a.Policy, a.Rule).Coverage.MissingNodes);
    }
    private sealed class CorruptReply(string fault) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
            if (fault == "signature") body["signature"] = new string('0', 64);
            if (fault == "requestId") body["requestId"] = Guid.NewGuid().ToString("D");
            if (fault == "boot") body["evidence"]!["bootId"] = Guid.NewGuid().ToString("N");
            if (fault == "timing") body["processingMilliseconds"] = -1;
            response.Content.Dispose(); response.Content = new StringContent(body.ToJsonString()); return response;
        }
    }

    [Fact]
    public async Task ThreeRealRoutesEstablishZeroWatermarksActivateRevisionAndConfirm105WithoutNewRequests()
    {
        await using var a = await Node.Start("a"); await using var b = await Node.Start("b"); await using var c = await Node.Start("c");
        var nodes = new[] { a, b, c };
        foreach (var node in nodes) foreach (var other in nodes.Where(n => n != node)) node.Runtime.Peers[other.Id].Endpoint = other.Url;
        foreach (var node in nodes) node.Cohort.ObserveRules([node.Rule]);
        // Warm the actual authenticated HTTP route before the measured fixed window.
        await Probe(nodes);
        var start = CcDecisionService.Window(DateTimeOffset.UtcNow, a.Rule.Period).AddSeconds(a.Rule.Period);
        await Task.Delay(start - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(30));
        foreach (var node in nodes)
        {
            Assert.Equal(0, node.Cohort.Capture(node.Policy, node.Rule, start).LocalCount);
            node.Store.IncrementWindow("cc.access", node.Policy.StateKey, start, TimeSpan.FromSeconds(node.Rule.Period), node.Id == "a" ? 40 : node.Id == "b" ? 35 : 30);
            node.Decisions.Sweep(); Assert.Empty(node.Log.Read());
        }
        foreach (var node in nodes) await node.StartEvaluation();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            while (nodes.Any(n => n.Log.Read().Length == 0)) await Task.Delay(10, timeout.Token);
        foreach (var node in nodes) await node.StopEvaluation();
        await Probe(nodes);
        foreach (var node in nodes)
        {
            var observed = node.Cohort.Read(node.Policy, node.Rule);
            Assert.Equal("Complete", observed.Coverage.Status);
            Assert.NotNull(observed.Coverage.Watermark);
            Assert.Equal(105, observed.Count);
            Assert.True(node.Store.TryGet<DateTimeOffset>("cc.triggered", node.Policy.StateKey, out _));
        }
        var id = Assert.Single(a.Log.Read()).DecisionId!;
        Assert.All(nodes, n => Assert.Equal(id, Assert.Single(n.Log.Read()).DecisionId));
        await Probe(nodes, id);
        Assert.All(nodes, n => Assert.True(n.Decisions.InspectClusterExecution(n.Policy, id)!.AllNodesConfirmed));
        Assert.All(nodes, n => Assert.Equal(3, n.Decisions.InspectClusterExecution(n.Policy, id)!.Boots.Count));
        b.Rule.Threshold = 101;
        b.Cohort.ObserveRules([b.Rule]);
        await a.Client.ProbeAsync("b", new(a.Policy, id, start), a.Cohort, default);
        Assert.Equal("Partial", a.Cohort.Read(a.Policy, a.Rule).Coverage.Status);
        Assert.False(a.Decisions.InspectClusterExecution(a.Policy, id)!.AllNodesConfirmed);
        b.Rule.Threshold = 100;
        b.Cohort.ObserveRules([b.Rule]);
        await a.Client.ProbeAsync("b", new(a.Policy, id, start), a.Cohort, default);
        Assert.Contains("window-incomplete", a.Cohort.Read(a.Policy, a.Rule).Coverage.Reasons);
        a.Communication.Peers["b"].AllowedOperations.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Client.ProbeAsync("b", new(a.Policy, id, start), a.Cohort, default));
        Assert.False(a.Decisions.InspectClusterExecution(a.Policy, id)!.AllNodesConfirmed);
    }

    private static async Task Probe(Node[] nodes, string? decision = null)
    {
        foreach (var node in nodes)
        {
            var window = CcDecisionService.Window(DateTimeOffset.UtcNow, node.Rule.Period);
            foreach (var other in nodes.Where(n => n != node))
                await node.Client.ProbeAsync(other.Id, new(node.Policy, decision ?? CcDecisionService.DecisionId(node.Policy, window), window), node.Cohort, default);
        }
    }

    private sealed class Node : IAsyncDisposable
    {
        private const string Secret = "cc-cohort-query-secret-at-least-32-bytes";
        public string Id = "", Url = "";
        public RuntimeStateOptions Runtime = null!;
        public NodeCommunicationOptions Communication = null!;
        public RuntimeStateStore Store = null!;
        public CcDecisionService Decisions = null!;
        public CcCohortState Cohort = null!;
        public CcPeerQueryClient Client = null!;
        public CcPolicyDescriptor Policy = null!;
        public CcBanLog Log = new();
        public AdvancedCcRule Rule = new() { Id = "cohort", Action = CcAction.Block, Threshold = 100, Period = 5,
            ActionSeconds = 20, ExpectedNodes = ["a", "b", "c"], RequireCompleteCoverage = true };
        private NodeIdentityService _identity = null!;
        private WebApplication _app = null!;
        private IStatisticService _statistics = null!;
        private CcCohortMonitor? _monitor;
        public async Task StartEvaluation()
        {
            _monitor = new(Cohort, Client, _statistics, Store);
            await Decisions.StartAsync(default); await _monitor.StartAsync(default);
        }
        public async Task StopEvaluation()
        {
            if (_monitor is not null) { await _monitor.StopAsync(default); _monitor.Dispose(); _monitor = null; }
            await Decisions.StopAsync(default);
        }
        public static async Task<Node> Start(string id)
        {
            var node = new Node { Id = id };
            node.Runtime = new() { Enabled = true, NodeId = id, StateDirectory = Temp() };
            node.Communication = new() { Enabled = true, NodeId = id, ClusterId = "cluster", RequireHttps = false, StateDirectory = Temp() };
            foreach (var peer in new[] { "a", "b", "c" }.Where(p => p != id))
            {
                node.Runtime.Peers[peer] = new() { ProtocolVersion = 2, Send = new() { AllowedDomains = ["cc.decision"], AllowUngroupedKeys = true } };
                node.Communication.Peers[peer] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = Secret }, AllowedOperations = [CcExecutionQueryService.Operation] };
            }
            var ro = new StaticOptionsMonitor<RuntimeStateOptions>(node.Runtime);
            var co = new StaticOptionsMonitor<NodeCommunicationOptions>(node.Communication);
            node.Store = new(ro); node._identity = new(co); await node._identity.StartAsync(default);
            var statistics = DispatchProxy.Create<IStatisticService, Rules>(); ((Rules)(object)statistics).Rule = node.Rule;
            node._statistics = statistics;
            node.Cohort = new(node.Store, ro, node._identity, communication: co);
            node.Decisions = new(statistics, node.Store, node.Log, cohort: node.Cohort);
            node.Client = new(co, ro); node.Policy = node.Decisions.Register(node.Rule, "host:a.example", "192.0.2.1");
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(ro).AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(co);
            builder.Services.AddSingleton(node._identity).AddSingleton<NodeMessageAuthentication>();
            builder.Services.AddSingleton<IStartupFilter, CcQueryTimingStartupFilter>();
            builder.Services.AddSingleton(new CcExecutionQueryService(node.Decisions, co, ro, node.Cohort, statistics));
            node._app = builder.Build(); node._app.MapNodeCommunication(port); await node._app.StartAsync();
            node.Url = $"http://127.0.0.1:{port}"; return node;
        }
        public async ValueTask DisposeAsync() { await StopEvaluation(); await _app.DisposeAsync(); Client.Dispose(); Decisions.Dispose(); Store.Dispose(); _identity.Dispose(); }
        private static string Temp() => Path.Combine(Path.GetTempPath(), "cc-cohort-" + Guid.NewGuid().ToString("N"));
    }
    public class Rules : DispatchProxy
    {
        public AdvancedCcRule Rule = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        { nameof(IStatisticService.GetAdvancedCcRules) => new List<AdvancedCcRule> { Rule }, _ => throw new NotSupportedException() };
    }
}
