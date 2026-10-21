using System.Reflection;
using System.Text.Json;
using LyWaf.Plugins.Sys.Analysis;
using LyWaf.Services.AuditLog;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcReleaseApiTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("read")]
    public async Task MissingWritePermissionCannotRelease(string? permission)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        if (permission is not null) ctx.Items["ManagementScopes"] = new[] { permission };
        var result = await AnalysisAdminApi.HandleAsync("cc/release", ctx);
        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
        var status = await AnalysisAdminApi.HandleAsync("cc/release/status", ctx);
        Assert.Equal(403, ((IStatusCodeHttpResult)status).StatusCode);
    }

    [Theory]
    [InlineData("host", "site.example", "", "192.0.2.1", 202, true)]
    [InlineData("group", "checkout", "checkout", "192.0.2.1", 202, true)]
    [InlineData("host", "site.example", "checkout", "192.0.2.1", 409, true)]
    [InlineData("group", "wrong", "checkout", "192.0.2.1", 409, true)]
    [InlineData("route", "missing-route", "", "192.0.2.1", 400, true)]
    [InlineData("route", "route1", "", "192.0.2.1", 202, true)]
    [InlineData("host", "site.example", "", "not-an-ip", 400, true)]
    [InlineData("host", "site.example", "", "192.0.2.1", 503, false)]
    public async Task ExplicitScopeReleaseIsAuditedPendingAndDoesNotUnbanGlobalIp(string kind, string scope, string group, string ip, int expected, bool signing)
    {
        var options = CcReleaseTestKeys.Configure(new RuntimeStateOptions
        { Enabled = true, NodeId = "sender", StateDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        if (!signing) options.CcReleasePrivateKeyPem = "";
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(options));
        store.Set("ip.blocked", ip, "manual");
        var rule = new AdvancedCcRule { Id = "rule", ProtectionGroup = group };
        var statistic = DispatchProxy.Create<IStatisticService, RuleProvider>();
        ((RuleProvider)(object)statistic).Rule = rule;
        var audit = new Audit();
        using var services = new ServiceCollection().AddLogging().AddSingleton(statistic)
            .AddSingleton<Yarp.ReverseProxy.Configuration.IProxyConfigProvider, Routes>()
            .AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new StaticOptionsMonitor<RuntimeStateOptions>(options))
            .AddSingleton<IOptionsMonitor<LyWaf.Services.NodeCommunication.NodeCommunicationOptions>>(
                new StaticOptionsMonitor<LyWaf.Services.NodeCommunication.NodeCommunicationOptions>(new()))
            .AddSingleton<RuntimeStateSyncSender>().AddSingleton<CcPropagationTracker>()
            .AddSingleton<ICcRuleChecker>(new CcRuleChecker(statistic, null!, store)).AddSingleton<IAuditLogService>(audit).BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = "POST";
        ctx.Items["ManagementScopes"] = new[] { "write" };
        ctx.Items["Username"] = "operator";
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { ruleId = "rule", scopeType = kind, scope, ip }));
        ctx.Response.Body = new MemoryStream();
        var result = await AnalysisAdminApi.HandleAsync("cc/release", ctx);
        await result.ExecuteAsync(ctx);
        Assert.Equal(expected, ctx.Response.StatusCode);
        Assert.True(store.TryGet<string>("ip.blocked", ip, out var manual));
        Assert.Equal("manual", manual);
        if (expected == 202)
        {
            ctx.Response.Body.Position = 0;
            using var response = await JsonDocument.ParseAsync(ctx.Response.Body);
            Assert.Equal("pending", response.RootElement.GetProperty("propagation").GetProperty("status").GetString());
            Assert.False(response.RootElement.GetProperty("propagation").GetProperty("allNodesConfirmed").GetBoolean());
            Assert.Single(store.GetSnapshots(CcPolicyGeneration.Domain));
            Assert.Contains($"scope={kind}:{scope}", Assert.Single(audit.Actions));
            var generation = response.RootElement.GetProperty("policyGeneration").Deserialize<CcPolicyGeneration>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            ctx.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { ruleId = "rule", scopeType = kind, scope, ip, policyGeneration = generation }));
            ctx.Response.Body = new MemoryStream();
            await (await AnalysisAdminApi.HandleAsync("cc/release/status", ctx)).ExecuteAsync(ctx);
            ctx.Response.Body.Position = 0;
            using var status = await JsonDocument.ParseAsync(ctx.Response.Body);
            Assert.False(status.RootElement.GetProperty("propagation").GetProperty("allNodesConfirmed").GetBoolean());
            Assert.Single(audit.Actions);
            Assert.Equal(generation, store.GetCcPolicyGeneration(CcRuleStateKey.Create(rule, ip, kind + ":" + scope)));
        }
        else { Assert.Empty(audit.Actions); Assert.Empty(store.GetSnapshots(CcPolicyGeneration.Domain)); }
    }

    public class RuleProvider : DispatchProxy
    {
        public AdvancedCcRule Rule = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IStatisticService.GetAdvancedCcRule) => (string)args![0]! == Rule.Id ? Rule : null,
            _ => throw new NotSupportedException()
        };
    }
    private sealed class Routes : Yarp.ReverseProxy.Configuration.IProxyConfigProvider, Yarp.ReverseProxy.Configuration.IProxyConfig
    {
        public Yarp.ReverseProxy.Configuration.IProxyConfig GetConfig() => this;
        public IReadOnlyList<Yarp.ReverseProxy.Configuration.RouteConfig> RoutesList { get; } = [new() { RouteId = "route1" }];
        IReadOnlyList<Yarp.ReverseProxy.Configuration.RouteConfig> Yarp.ReverseProxy.Configuration.IProxyConfig.Routes => RoutesList;
        public IReadOnlyList<Yarp.ReverseProxy.Configuration.ClusterConfig> Clusters => [];
        public Microsoft.Extensions.Primitives.IChangeToken ChangeToken => new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
    }
    private sealed class Audit : IAuditLogService
    {
        public List<string> Actions = [];
        public void Log(string username, string action, string ip) => Actions.Add(action);
        public (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50) => ([], 0);
    }
}
