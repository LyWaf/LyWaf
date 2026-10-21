using System.Net;
using System.Reflection;
using System.Text.Json;
using LyWaf.Services.Captcha;
using LyWaf.Services.Metrics;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcCaptchaReleaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryNodeCompletesCaptchaAndLostReplyRetriesOnlyOnce(bool loseReply)
    {
        await using var f = await Fixture.Start();
        f.Transport.LoseFirstReply = loseReply;
        f.Local.Set("cc.triggered", f.Target.PolicyKey, "captcha");
        f.Local.Set("ip.blocked", f.Target.Ip, "manual");
        var other = CcRuleStateKey.Create(f.Rule, f.Target.Ip, "host:other.example");
        f.Local.Set("cc.triggered", other, "captcha");
        var result = await f.Verify("42");
        Assert.True(result.Success);
        Assert.Equal("", f.LocalOptions.CcReleasePrivateKeyPem);
        Assert.True(result.Release!.IssuerSigned);
        Assert.True(result.Release.LocalApplied);
        Assert.False(result.Release.AllNodesConfirmed);
        Assert.Equal("pending", result.Release.PropagationStatus);
        Assert.Equal(loseReply ? 2 : 1, f.Transport.Calls);
        Assert.Equal(1, f.Local.GetCcPolicyGeneration(f.Target.PolicyKey).Version);
        Assert.Equal(f.Local.GetCcPolicyGeneration(f.Target.PolicyKey), f.Authority.GetCcPolicyGeneration(f.Target.PolicyKey));
        Assert.False(f.Local.TryGet<string>("cc.triggered", f.Target.PolicyKey, out _));
        Assert.True(f.Local.TryGet<string>("cc.triggered", other, out _));
        Assert.True(f.Local.TryGet<string>("ip.blocked", f.Target.Ip, out _));
    }

    [Theory]
    [InlineData("operation", 403)]
    [InlineData("scope", 403)]
    [InlineData("rule", 403)]
    [InlineData("fingerprint", 409)]
    [InlineData("ip", 409)]
    [InlineData("block", 409)]
    [InlineData("expired", 400)]
    [InlineData("secret", 401)]
    public async Task AuthenticatedEndpointRejectsUnauthorizedOrUnboundRequests(string fault, int status)
    {
        await using var f = await Fixture.Start();
        var target = f.Target;
        var verified = DateTimeOffset.UtcNow;
        if (fault == "operation") f.Communication.Peers["receiver"].AllowedOperations = ["runtime.merge"];
        if (fault == "scope") target = target with { Scope = "host:other.example" };
        if (fault == "rule") target = target with { RuleId = "other" };
        if (fault == "fingerprint") target = target with { PolicyKey = "v2:wrong:192.0.2.87" };
        if (fault == "ip") target = target with { Ip = "192.0.2.88" };
        if (fault == "block") f.Rule.Action = CcAction.Block;
        if (fault == "expired") verified = verified.AddMinutes(-3);
        using var response = await f.Send(new(target, new string('a', 64), verified), fault == "secret");
        Assert.Equal(status, (int)response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("node.errors." + body.GetProperty("errorCode").GetString(), body.GetProperty("messageKey").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.True(body.TryGetProperty("retryable", out _));
        if (fault is not ("secret" or "operation"))
        {
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("messageId").GetString()));
            Assert.Equal("test", body.GetProperty("operationId").GetString());
        }
        Assert.Equal(CcPolicyGeneration.Initial, f.Authority.GetCcPolicyGeneration(f.Target.PolicyKey));
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("unavailable")]
    [InlineData("signature")]
    [InlineData("binding")]
    [InlineData("timeout")]
    public async Task FailedRemoteReleaseKeepsLocalPenaltyAndReturnsFailure(string fault)
    {
        await using var f = await Fixture.Start();
        f.Local.Set("cc.triggered", f.Target.PolicyKey, "captcha");
        f.Transport.Fault = fault;
        var result = await f.Verify("42");
        Assert.False(result.Success);
        Assert.Equal(503, result.FailureStatus);
        Assert.Equal("cc_release_unconfirmed", result.ErrorCode);
        Assert.Equal(CcPolicyGeneration.Initial, f.Local.GetCcPolicyGeneration(f.Target.PolicyKey));
        Assert.True(f.Local.TryGet<string>("cc.triggered", f.Target.PolicyKey, out _));
    }

    [Fact]
    public async Task InvalidAnswerDoesNotRequestRelease()
    {
        await using var f = await Fixture.Start();
        Assert.False((await f.Verify("wrong")).Success);
        Assert.Equal(0, f.Transport.Calls);
        Assert.Equal(CcPolicyGeneration.Initial, f.Authority.GetCcPolicyGeneration(f.Target.PolicyKey));
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("public-key")]
    [InlineData("legacy")]
    public async Task MissingAuthorityConfigurationNeverFallsBackToUnsignedOrLegacyRelease(string missing)
    {
        await using var f = await Fixture.Start();
        if (missing == "authority") f.LocalOptions.CcCaptchaAuthorityNodeId = "";
        if (missing == "public-key") f.LocalOptions.CcReleaseAuthorities.Clear();
        if (missing == "legacy") f.LocalOptions.Peers["sender"].ProtocolVersion = 1;
        var result = await f.Verify("42");
        Assert.False(result.Success);
        Assert.Equal(503, result.FailureStatus);
        Assert.Equal(0, f.Transport.Calls);
        Assert.Equal(CcPolicyGeneration.Initial, f.Local.GetCcPolicyGeneration(f.Target.PolicyKey));
    }

    [Fact]
    public async Task StaleCaptchaCannotReleaseNewIssuerGeneration()
    {
        await using var f = await Fixture.Start();
        var current = f.Authority.AdvanceCcPolicyGeneration(f.Target.PolicyKey);
        f.Authority.Set("cc.triggered", current.StateKey(f.Target.PolicyKey), "new-penalty");
        var result = await f.Verify("42");
        Assert.False(result.Success);
        Assert.Equal(1, f.Transport.Calls);
        Assert.Equal(current, f.Authority.GetCcPolicyGeneration(f.Target.PolicyKey));
        Assert.True(f.Authority.TryGet<string>("cc.triggered", current.StateKey(f.Target.PolicyKey), out _));
        Assert.Equal(CcPolicyGeneration.Initial, f.Local.GetCcPolicyGeneration(f.Target.PolicyKey));
    }

    [Fact]
    public async Task RetrySurvivesIssuerRestartButCannotClearNewerPenalty()
    {
        await using var f = await Fixture.Start();
        var request = new CcCaptchaReleaseRequest(f.Target, new string('b', 64), DateTimeOffset.UtcNow);
        using var first = await f.Send(request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var id = CcCaptchaReleaseIssuer.ReleaseId("receiver", request);
        var success = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        Assert.True(success.GetProperty("success").GetBoolean());
        Assert.True(success.GetProperty("issuerApplied").GetBoolean());
        Assert.False(success.GetProperty("allNodesConfirmed").GetBoolean());
        Assert.True(success.TryGetProperty("fence", out _));
        Assert.Equal("test", success.GetProperty("operationId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(success.GetProperty("traceId").GetString()));
        using var restarted = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(f.AuthorityOptions));
        var repeated = restarted.IssueCcCaptchaRelease(f.Target.PolicyKey, f.Target.Expected, id);
        Assert.Equal(1, repeated.Value!.Value.Deserialize<CcPolicyGeneration>(NodeMessageAuthentication.JsonOptions)!.Version);
        restarted.AdvanceCcPolicyGeneration(f.Target.PolicyKey);
        Assert.Throws<CcCaptchaGenerationConflictException>(() => restarted.IssueCcCaptchaRelease(f.Target.PolicyKey, f.Target.Expected, id));
        Assert.Equal(2, restarted.GetCcPolicyGeneration(f.Target.PolicyKey).Version);
    }

    [Fact]
    public async Task GrantHasBoundedRateAndMissingIssuerKeyFailsClosed()
    {
        await using var f = await Fixture.Start();
        f.Communication.Peers["receiver"].CcCaptchaReleasesPerMinute = 1;
        var request = new CcCaptchaReleaseRequest(f.Target, new string('c', 64), DateTimeOffset.UtcNow);
        using var first = await f.Send(request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await f.Send(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        f.AuthorityOptions.CcReleasePrivateKeyPem = "";
        using var third = await f.Send(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string Secret = "cc-captcha-transport-secret-at-least-32-bytes";
        public AdvancedCcRule Rule = new() { Id = "captcha", Action = CcAction.Captcha, Threshold = 2 };
        public RuntimeStateOptions AuthorityOptions = CcReleaseTestKeys.Configure(new() { Enabled = true, NodeId = "sender", StateDirectory = Temp() });
        public RuntimeStateOptions LocalOptions = CcReleaseTestKeys.Configure(new() { Enabled = true, NodeId = "receiver", StateDirectory = Temp() });
        public NodeCommunicationOptions Communication = new()
        {
            Enabled = true, RequireHttps = false, NodeId = "sender", ClusterId = "cluster", StateDirectory = Temp(),
            Peers = new() { ["receiver"] = new() { Keys = new() { ["key"] = Secret },
                AllowedOperations = [CcCaptchaReleaseIssuer.Operation], CcCaptchaReleaseGrants = [new("captcha", "host:a.example")] } }
        };
        public RuntimeStateStore Authority = null!, Local = null!;
        public CcCaptchaTarget Target = null!;
        public Transport Transport = new();
        private WebApplication _app = null!;
        private HttpClient _http = null!;
        private CcCaptchaReleaseClient _client = null!;
        private ServiceProvider _services = null!;

        public static async Task<Fixture> Start()
        {
            var f = new Fixture();
            f.LocalOptions.CcReleasePrivateKeyPem = "";
            f.LocalOptions.CcCaptchaAuthorityNodeId = "sender";
            f.Target = new(f.Rule.Id, "host:a.example", "192.0.2.87",
                CcRuleStateKey.Create(f.Rule, "192.0.2.87", "host:a.example"), CcPolicyGeneration.Initial);
            var statistics = DispatchProxy.Create<IStatisticService, Rules>();
            ((Rules)(object)statistics).Rule = f.Rule;
            f.Authority = new(new StaticOptionsMonitor<RuntimeStateOptions>(f.AuthorityOptions));
            f.Local = new(new StaticOptionsMonitor<RuntimeStateOptions>(f.LocalOptions));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new StaticOptionsMonitor<RuntimeStateOptions>(f.AuthorityOptions));
            builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new StaticOptionsMonitor<NodeCommunicationOptions>(f.Communication));
            builder.Services.AddSingleton<IRuntimeStateStore>(f.Authority);
            builder.Services.AddSingleton(statistics);
            builder.Services.AddSingleton<NodeMessageAuthentication>();
            builder.Services.AddSingleton<CcCaptchaReleaseIssuer>();
            f._app = builder.Build();
            f._app.MapNodeCommunication(7030);
            await f._app.StartAsync();
            var url = f._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            f._http = new() { BaseAddress = new(url) };
            f.LocalOptions.Peers["sender"] = new() { Enabled = true, Endpoint = url, ProtocolVersion = 2 };
            var localCommunication = new NodeCommunicationOptions { Enabled = true, RequireHttps = false, NodeId = "receiver", ClusterId = "cluster",
                Peers = new() { ["sender"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = Secret } } } };
            f._client = new(new StaticOptionsMonitor<RuntimeStateOptions>(f.LocalOptions),
                new StaticOptionsMonitor<NodeCommunicationOptions>(localCommunication), f.Local, statistics, f.Transport);
            f._services = new ServiceCollection().AddSingleton(f._client).BuildServiceProvider();
            return f;
        }

        public Task<CaptchaVerifyResult> Verify(string answer)
        {
            var token = Guid.NewGuid().ToString("N");
            Local.Set("captcha.challenge", token, new CaptchaService.CaptchaEntry
            { ClientIp = Target.PolicyKey, Answer = "42", ExpiryTime = DateTime.UtcNow.AddMinutes(1) });
            var captcha = new CaptchaService(_services, new PrometheusMetrics(new StaticOptionsMonitor<MetricsOptions>(new())), Local);
            return captcha.VerifyAsync(Target.PolicyKey, token, answer, new BehaviorData { Duration = 2000, MouseTrackCount = 4 }, Target);
        }

        public Task<HttpResponseMessage> Send(CcCaptchaReleaseRequest payload, bool badSecret = false)
        {
            var now = DateTimeOffset.UtcNow;
            var envelope = new NodeMessage(2, "cluster", "receiver", "sender", Guid.NewGuid().ToString("D"), "test",
                CcCaptchaReleaseIssuer.Operation, now, now.AddMinutes(1), JsonSerializer.SerializeToElement(payload, NodeMessageAuthentication.JsonOptions));
            var body = JsonSerializer.SerializeToUtf8Bytes(envelope, NodeMessageAuthentication.JsonOptions);
            var message = new HttpRequestMessage(HttpMethod.Post, NodeMessageAuthentication.Prefix + envelope.Type) { Content = new ByteArrayContent(body) };
            message.Headers.Host = "localhost:7030";
            NodeMessageAuthentication.Sign(message, envelope, "key", badSecret ? Secret + "wrong" : Secret, body);
            return _http.SendAsync(message);
        }

        public async ValueTask DisposeAsync()
        {
            _services.Dispose(); _client.Dispose(); _http.Dispose(); Local.Dispose();
            await _app.StopAsync(); await _app.DisposeAsync(); Authority.Dispose();
        }
        private static string Temp() => Path.Combine(Path.GetTempPath(), "cc-captcha-test-" + Guid.NewGuid().ToString("N"));
    }

    public class Rules : DispatchProxy
    {
        public AdvancedCcRule Rule = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IStatisticService.GetAdvancedCcRule) => (string)args![0]! == Rule.Id ? Rule : null,
            nameof(IStatisticService.GetAdvancedCcRules) => new List<AdvancedCcRule> { Rule },
            _ => throw new NotSupportedException()
        };
    }

    private sealed class Transport : DelegatingHandler
    {
        public int Calls;
        public bool LoseFirstReply;
        public string Fault = "";
        public Transport() : base(new SocketsHttpHandler { AllowAutoRedirect = false }) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            request.Headers.Host = "localhost:7030";
            if (Fault == "timeout") throw new TaskCanceledException("simulated timeout");
            if (Fault == "denied") return new(HttpStatusCode.Forbidden);
            if (Fault == "unavailable") return new(HttpStatusCode.ServiceUnavailable);
            var response = await base.SendAsync(request, token);
            if (LoseFirstReply && Calls == 1) { response.Dispose(); throw new HttpRequestException("reply lost after issuer commit"); }
            if (Fault is "signature" or "binding")
            {
                var value = JsonSerializer.Deserialize<CcCaptchaReleaseResponse>(await response.Content.ReadAsStringAsync(token), NodeMessageAuthentication.JsonOptions)!;
                value = value with { Fence = Fault == "signature" ? value.Fence with { CcReleaseProof = null } : value.Fence with { Key = "wrong" } };
                response.Content.Dispose();
                response.Content = new StringContent(JsonSerializer.Serialize(value, NodeMessageAuthentication.JsonOptions));
            }
            return response;
        }
    }
}
