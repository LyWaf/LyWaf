using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using LyWaf.Plugins.Core;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodePeerClientFailureTests
{
    [Theory]
    [InlineData(false, 403)]
    [InlineData(true, 403)]
    [InlineData(false, 429)]
    [InlineData(true, 429)]
    [InlineData(false, 503)]
    [InlineData(true, 503)]
    public async Task ClientsPreserveBoundedMetadataWithoutRetry(bool handshake, int status)
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var handler = new Handler(() => new((HttpStatusCode)status)
        {
            Content = new StringContent("{\"errorCode\":\"node_operation_forbidden\",\"traceId\":\"remote-123\",\"messageKey\":\"untrusted.key\",\"message\":\"private-remote-body\"}")
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Call(handshake, handler));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal("node_operation_forbidden", error.Data["ErrorCode"]);
        Assert.Equal("node.errors.node_operation_forbidden", error.Data["MessageKey"]);
        Assert.Equal("remote-123", error.Data["RemoteTraceId"]);
        Assert.Equal(TimeSpan.FromSeconds(7), error.Data["RetryAfter"]);
        Assert.DoesNotContain("private-remote-body", error.ToString());
        Assert.Equal(1, handler.Calls);
        if (!handshake) fixture.AssertMissing();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OversizeErrorRetainsStatusAndRetryButNotBody(bool handshake, bool knownLength)
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var body = new Body(Encoding.UTF8.GetBytes("{\"errorCode\":\"secret\",\"traceId\":\"private\",\"padding\":\""
            + new string('x', 20000) + "\"}"));
        var handler = new Handler(() =>
        {
            var content = new StreamContent(body);
            if (knownLength) content.Headers.ContentLength = 21000;
            return new(HttpStatusCode.ServiceUnavailable) { Content = content };
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Call(handshake, handler));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), error.Data["RetryAfter"]);
        Assert.False(error.Data.Contains("ErrorCode"));
        Assert.False(error.Data.Contains("RemoteTraceId"));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(knownLength ? 0 : NodeRemoteError.MaximumBodyBytes + 1, body.BytesRead);
        Assert.Equal(1, handler.Calls);
        if (!handshake) fixture.AssertMissing();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallerCancellationDuringBodyPreservesEvidenceWithoutExtendingFreshness(bool handshake, bool errorBody)
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        using var caller = new CancellationTokenSource();
        var body = new Body([], stall: true);
        var handler = new Handler(() => new(errorBody ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StreamContent(body) });
        var call = fixture.Call(handshake, handler, caller.Token);
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            fixture.AssertPresent();
            fixture.Clock.Now = fixture.Clock.Now.AddMilliseconds(501);
            fixture.AssertMissing();
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            caller.Cancel();
            try { await call; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternalDeadlineIncludesSuccessAndErrorBodiesAndInvalidates(bool errorBody)
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        using var caller = new CancellationTokenSource();
        var body = new Body([], stall: true);
        var handler = new Handler(() => new(errorBody ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StreamContent(body) });
        var call = fixture.Call(false, handler, caller.Token);
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.False(caller.IsCancellationRequested);
            // The evidence clock is frozen: disappearance proves invalidation, not natural expiry.
            fixture.AssertMissing();
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            caller.Cancel();
            try { await call; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task InvalidSuccessPayloadStillInvalidates()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        var handler = new Handler(() => new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Call(false, handler));
        fixture.AssertMissing();
        Assert.Equal(1, handler.Calls);
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Assert.Single(request.Headers.GetValues("X-LyWaf-Signature"));
            var result = response();
            result.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(result);
        }
    }

    private sealed class Body(byte[] bytes, bool stall = false) : MemoryStream(bytes)
    {
        public int BytesRead;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }

    private sealed class ClockSource : TimeProvider
    {
        public DateTimeOffset Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        private const string Secret = "node-client-failure-test-secret-at-least-32";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "node-client-failure-" + Guid.NewGuid().ToString("N"));
        private readonly StaticOptionsMonitor<NodeCommunicationOptions> _communication;
        private readonly StaticOptionsMonitor<RuntimeStateOptions> _runtime;
        private readonly RuntimeStateStore _store;
        private readonly NodeIdentityService _identity;
        private readonly CcCohortState _cohort;
        private readonly AdvancedCcRule _rule = new() { Id = "rule", Period = 10, ExpectedNodes = ["a", "b"], RequireCompleteCoverage = true };
        private readonly CcPolicyDescriptor _policy;
        public readonly ClockSource Clock = new();

        public Fixture()
        {
            _communication = new(new() { Enabled = true, ClusterId = "cluster", NodeId = "a", RequireHttps = false,
                StateDirectory = Path.Combine(_root, "identity"), Peers = new()
                { ["b"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = Secret },
                    AllowedOperations = [NodeHandshakeService.Operation, CcExecutionQueryService.Operation] } } });
            _runtime = new(new() { Enabled = true, NodeId = "a", StateDirectory = Path.Combine(_root, "runtime"),
                Peers = new() { ["b"] = new() { Endpoint = "http://localhost", ProtocolVersion = 2 } } });
            _store = new(_runtime);
            _identity = new(_communication);
            Clock.Now = CcDecisionService.Window(DateTimeOffset.UtcNow.AddMinutes(1), 10);
            _cohort = new(_store, _runtime, _identity, Clock, _communication);
            _policy = new(_rule.Id, CcRuleStateKey.Revision(_rule), "host:a.example", "192.0.2.1",
                CcRuleStateKey.Create(_rule, "192.0.2.1", "host:a.example"), CcPolicyGeneration.Initial);
            _cohort.ObserveRules([_rule]);
            Clock.Now = Clock.Now.AddSeconds(10);
        }

        public async Task Seed()
        {
            await _identity.StartAsync(default);
            var reply = new CcPeerReply("request", "digest", new("b", Guid.NewGuid().ToString("N"),
                _policy.PolicyKey, _policy.PolicyGeneration, _policy.RuleRevision, true, Clock.Now.AddSeconds(-10),
                Clock.Now, Clock.Now, 0, true), new("b", "decision", "Unknown", Clock.Now, null, _policy.PolicyGeneration), "key", "");
            reply = reply with { Signature = CcPeerQueryClient.Sign(reply, Secret) };
            _cohort.Accept(_policy, "b", "http://localhost", reply, Clock.Now, Clock.Now, 0);
            AssertPresent();
        }

        public void AssertPresent() => Assert.DoesNotContain("b", _cohort.Read(_policy, _rule).Coverage.MissingNodes);
        public void AssertMissing() => Assert.Contains("b", _cohort.Read(_policy, _rule).Coverage.MissingNodes);

        public async Task Call(bool handshake, HttpMessageHandler handler, CancellationToken token = default)
        {
            if (handshake)
            {
                var metadata = new NodeHandshakeMetadataService(_identity, new Revision(),
                    DispatchProxy.Create<IPluginManager, EmptyPlugins>(), new StaticOptionsMonitor<PluginNodeMessageOptions>(new()));
                using var client = new NodeHandshakeClient(_communication, new StaticOptionsMonitor<DistributionOptions>(new()
                { Nodes = new() { ["b"] = new() { Endpoint = "http://localhost", ProtocolVersion = 2, Enabled = true } } }), metadata, handler);
                await client.CaptureAsync("b", token);
            }
            else
            {
                using var client = new CcPeerQueryClient(_communication, _runtime, handler);
                await client.ProbeAsync("b", new(_policy, CcDecisionService.DecisionId(_policy, Clock.Now), Clock.Now), _cohort, token);
            }
        }

        public void Dispose()
        {
            _identity.Dispose();
            _store.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class Revision : INodeHandshakeConfigurationSource
    {
        public Task<NodeConfigurationRevision> CaptureAsync(CancellationToken token)
            => Task.FromResult(new NodeConfigurationRevision(null, new string('A', 64), null));
    }

    public class EmptyPlugins : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == nameof(IPluginManager.GetAllPlugins) ? Array.Empty<ILyWafPlugin>() : throw new NotSupportedException();
    }
}
