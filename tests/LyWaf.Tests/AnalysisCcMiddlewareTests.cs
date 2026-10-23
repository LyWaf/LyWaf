using System.Net;
using System.Reflection;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Sys;
using LyWaf.Plugins.Sys.Analysis;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class AnalysisCcMiddlewareTests
{
    [Theory]
    [InlineData(false, CcAction.Block, false, 1, 0)]
    [InlineData(true, CcAction.Block, false, 0, 1)]
    [InlineData(true, CcAction.LogOnly, false, 1, 1)]
    [InlineData(true, CcAction.Block, true, 1, 1)]
    public async Task PluginRequestStagePreservesActionAndDownstreamBehavior(bool triggered, CcAction action,
        bool released, int expectedNext, int expectedActions)
    {
        var checker = DispatchProxy.Create<ICcRuleChecker, Checker>();
        var state = (Checker)(object)checker;
        state.Result = new() { IsTriggered = triggered, Action = action };
        state.ReleaseDuringAction = released;
        using var services = new ServiceCollection().AddSingleton(checker).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        new AnalysisPlugin().ConfigureProxyStage(app, PluginProxyStage.RequestProtection);
        var next = 0;
        app.Run(_ => { next++; return Task.CompletedTask; });
        var context = Context(services);
        await app.Build()(context);
        Assert.Equal(1, state.Checks);
        Assert.Equal(expectedActions, state.Actions);
        Assert.Equal(expectedNext, next);
        Assert.Equal("127.0.0.1", state.ClientIp);
        Assert.Equal("LyWaf.Plugins.Sys.Analysis", typeof(CcProtectionMiddleware).Namespace);
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(302, 0)]
    [InlineData(400, 1)]
    [InlineData(503, 1)]
    public async Task PluginResponseStageRecordsOnlyDownstreamErrors(int status, int expectedErrors)
    {
        var checker = DispatchProxy.Create<ICcRuleChecker, Checker>();
        using var services = new ServiceCollection().AddSingleton(checker).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        new AnalysisPlugin().ConfigureProxyStage(app, PluginProxyStage.ResponseMonitoring);
        app.Run(context => { context.Response.StatusCode = status; return Task.CompletedTask; });
        await app.Build()(Context(services));
        var state = (Checker)(object)checker;
        Assert.Equal(expectedErrors, state.Errors);
        if (expectedErrors > 0) Assert.Equal(status, state.ErrorStatus);
    }

    private static DefaultHttpContext Context(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        return context;
    }

    public class Checker : DispatchProxy
    {
        public CcCheckResult Result = new();
        public bool ReleaseDuringAction;
        public int Checks, Actions, Errors, ErrorStatus;
        public string? ClientIp;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case nameof(ICcRuleChecker.CheckRequest): Checks++; ClientIp = (string)args![1]!; return Result;
                case nameof(ICcRuleChecker.ExecuteAction):
                    Actions++; if (ReleaseDuringAction) Result.IsTriggered = false; return Task.CompletedTask;
                case nameof(ICcRuleChecker.RecordErrorEvent): Errors++; ErrorStatus = (int)args![2]!; return null;
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }
}
