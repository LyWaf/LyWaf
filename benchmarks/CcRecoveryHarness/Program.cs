using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.Extensions.Options;

if (args.FirstOrDefault() == "child") await RecoveryNode.Run(args);
else await RecoveryRun.Run(args.FirstOrDefault(), args.Contains("--release"));

static class RecoveryNode
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly string[] Ids = ["a", "b", "c"];
    internal const string Secret = "isolated-cc-recovery-process-secret-32bytes";
    internal const string Cluster = "cc-recovery-harness";

    internal static async Task Run(string[] args)
    {
        var id = args[1]; var root = args[2]; var backend = int.Parse(args[3]);
        var ports = args[4].Split(',').Select(int.Parse).ToArray();
        var owner = File.ReadAllText(Path.Combine(root, "owner.token"));
        var rule = new AdvancedCcRule { Id = "recovery-105", Name = "Recovery 105", Threshold = 100,
            Period = 10, ActionSeconds = 600, Action = CcAction.Block, ExpectedNodes = Ids, RequireCompleteCoverage = true };
        var runtime = new RuntimeStateOptions { Enabled = true, NodeId = id, StateDirectory = Path.Combine(root, id, "runtime"),
            CcReleaseTrustDomain = Cluster, CcReleaseAuthorities = new() { ["a"] = File.ReadAllText(Path.Combine(root, "public.pem")) },
            CcReleasePrivateKeyPem = id == "a" ? File.ReadAllText(Path.Combine(root, "private.pem")) : "" };
        var protocol = new NodeCommunicationOptions { Enabled = true, NodeId = id, ClusterId = Cluster,
            RequireHttps = false, AllowLegacyProtocol = false, StateDirectory = Path.Combine(root, id, "protocol") };
        foreach (var peer in Ids.Where(p => p != id))
        {
            var sync = id == "a" || peer == "a";
            RuntimeStateReplicationPolicy Policy() => new() { AllowedDomains = ["cc.access", "cc.policy", "cc.decision", "cc.triggered", "cc.policy-generation"],
                AllowUngroupedKeys = true, TrustedForwardedSources = Ids.ToList() };
            runtime.Peers[peer] = new() { Endpoint = $"http://127.0.0.1:{ports[Array.IndexOf(Ids, peer)]}", ProtocolVersion = 2,
                DurableSyncEnabled = sync, BootstrapAuthority = peer == "a", AllowBootstrapExport = id == "a",
                SyncOfflineWindowSeconds = 5, Send = Policy(), Receive = Policy() };
            protocol.Peers[peer] = new() { ActiveKeyId = "test", Keys = new() { ["test"] = Secret },
                AllowedOperations = sync ? ["runtime.sync", CcExecutionQueryService.Operation] : [CcExecutionQueryService.Operation] };
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root, EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, backend));
        var statistics = DispatchProxy.Create<IStatisticService, RecoveryRules>();
        ((RecoveryRules)(object)statistics).Rule = rule;
        builder.Services.AddSingleton(statistics);
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new Fixed<RuntimeStateOptions>(runtime));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new Fixed<NodeCommunicationOptions>(protocol));
        builder.Services.AddSingleton<RuntimeStateStore>();
        builder.Services.AddSingleton<IRuntimeStateStore>(sp => sp.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<NodeIdentityService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeIdentityService>());
        builder.Services.AddSingleton<RuntimeStateSyncSender>();
        builder.Services.AddSingleton<RuntimeStateSyncReceiver>();
        builder.Services.AddSingleton<RuntimeStateReplicator>();
        builder.Services.AddSingleton<CcBanLog>();
        builder.Services.AddSingleton<CcCohortState>();
        builder.Services.AddSingleton<CcPeerQueryClient>();
        builder.Services.AddSingleton<CcExecutionQueryService>();
        builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, CcQueryTimingStartupFilter>();
        builder.Services.AddHostedService<CcCohortMonitor>();
        builder.Services.AddSingleton<CcDecisionService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CcDecisionService>());
        builder.Services.AddSingleton(sp => new CcRuleChecker(statistics, null!, sp.GetRequiredService<IRuntimeStateStore>(), sp.GetRequiredService<CcDecisionService>()));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api") && context.Request.Headers["X-Harness-Owner"] != owner)
            { context.Response.StatusCode = 403; return; }
            try { await next(context); }
            catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        });
        app.MapNodeCommunication(backend);
        var decisions = app.Services.GetRequiredService<CcDecisionService>();
        var store = app.Services.GetRequiredService<RuntimeStateStore>();
        var policy = decisions.Register(rule, "host:a.example", "192.0.2.55");
        var connected = false;
        app.MapPost("/connect", async (RuntimeStateReplicator replicator) =>
        { if (!connected) { connected = true; await replicator.StartAsync(default); } return Results.Ok(); });
        app.MapPost("/query/{peer}/{enabled:bool}", (string peer, bool enabled) =>
        {
            if (!protocol.Peers.TryGetValue(peer, out var entry)) return Results.BadRequest();
            entry.AllowedOperations = enabled ? entry.AllowedOperations.Append(CcExecutionQueryService.Operation).Distinct().ToList()
                : entry.AllowedOperations.Where(op => op != CcExecutionQueryService.Operation).ToList();
            return Results.Ok();
        });
        var seeded = false;
        app.MapPost("/seed/{amount:int}", (int amount, CcRuleChecker checker) =>
        {
            if (seeded || amount is < 1 or > 40) return Results.BadRequest();
            for (var i = 0; i < amount; i++)
            {
                var context = new DefaultHttpContext(); context.Request.Host = new("a.example"); context.Request.Path = "/protected";
                if (checker.CheckRequest(context, "192.0.2.55").IsTriggered) throw new InvalidOperationException("Premature local decision");
            }
            seeded = true; return Results.Ok();
        });
        app.MapPost("/release", () => id == "a" ? Results.Json(store.AdvanceCcPolicyGeneration(policy.PolicyKey)) : Results.StatusCode(403));
        app.MapGet("/state", (CcCohortState cohort, NodeIdentityService identity, CcBanLog log) =>
        {
            var current = policy with { PolicyGeneration = store.GetCcPolicyGeneration(policy.PolicyKey) };
            var decision = store.GetValues<CcDecision>(CcDecisionService.DecisionDomain).Select(p => p.Value)
                .Where(d => d.Policy.PolicyKey == policy.PolicyKey).OrderBy(d => d.CreatedAt).FirstOrDefault();
            var observed = cohort.Read(current, rule);
            return Results.Json(new { pid = Environment.ProcessId, identity = identity.Capture(), current, observed.Coverage, observed.Count,
                decision, execution = decision is null ? null : decisions.InspectClusterExecution(decision.Policy, decision.DecisionId),
                localExecution = decision is null ? null : decisions.InspectExecution(decision.Policy, decision.DecisionId),
                logs = log.Read(), generation = store.GetCcPolicyGeneration(policy.PolicyKey),
                penalties = store.GetValues<DateTimeOffset>("cc.triggered"), snapshots = store.GetSnapshots(),
                rss = Environment.WorkingSet, utc = DateTimeOffset.UtcNow });
        });
        app.MapGet("/protected", async (HttpContext context, CcRuleChecker checker) =>
        {
            context.Request.Host = new("a.example");
            var result = checker.CheckRequest(context, "192.0.2.55");
            if (result.IsTriggered) await checker.ExecuteAction(context, result, "192.0.2.55");
            else await context.Response.WriteAsync("allowed");
        });
        app.MapPost("/stop", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.Ok(); });
        await app.StartAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, pid = Environment.ProcessId, boot = app.Services.GetRequiredService<NodeIdentityService>().Capture().BootId }, Json));
        await app.WaitForShutdownAsync();
        if (connected) await app.Services.GetRequiredService<RuntimeStateReplicator>().StopAsync(default);
    }
}

public class RecoveryRules : DispatchProxy
{
    public AdvancedCcRule Rule = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(IStatisticService.GetAdvancedCcRules) => new List<AdvancedCcRule> { Rule },
        nameof(IStatisticService.GetAdvancedCcRule) => (string)args![0]! == Rule.Id ? Rule : null,
        _ => throw new NotSupportedException(method?.Name)
    };
}
sealed class Fixed<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
