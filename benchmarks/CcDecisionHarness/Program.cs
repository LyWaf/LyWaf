using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.Extensions.Options;

if (args.FirstOrDefault() == "child") await CcHarness.Child(args);
else if (args.FirstOrDefault() == "e3") await CcFaultRun.Run(args[1], int.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 1);
else await CcHarness.Run(args.FirstOrDefault(), args.Contains("--durable"));

static class CcHarness
{
    private const string Secret = "isolated-cc-decision-harness-transport-key-32bytes";
    private static readonly string[] Ids = ["a", "b", "c"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task Child(string[] args)
    {
        var logging = new NLog.Config.LoggingConfiguration();
        logging.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, new NLog.Targets.ConsoleTarget("diagnostics"));
        NLog.LogManager.Configuration = logging;
        var id = args[1]; var root = args[2]; var ports = args[3].Split(',').Select(int.Parse).ToArray();
        var durable = args.Length > 4 && args[4] == "--durable";
        var cohortMode = args.Contains("--cohort");
        using var queryListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "LyWaf.Cc.Query" or "LyWaf.Node.Replay",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => Console.WriteLine($"STAGE name={activity.OperationName} elapsedMs={activity.Duration.TotalMilliseconds:F3} scanMs={activity.GetTagItem("scan.ms")} persistMs={activity.GetTagItem("persist.ms")} peer={activity.GetTagItem("peer")} boundMs={activity.GetTagItem("clock.bound.ms")} roundtripMs={activity.GetTagItem("roundtrip.ms")} watermarkAgeMs={activity.GetTagItem("watermark.age.ms")} window={activity.GetTagItem("window.start")} received={activity.GetTagItem("received")}")
        };
        if (cohortMode) ActivitySource.AddActivityListener(queryListener);
        var rule = new AdvancedCcRule { Id = "cc-105", Name = "CC 105", Threshold = 100, Period = 3600,
            ActionSeconds = 300, Action = CcAction.Block };
        if (cohortMode) { rule.Period = int.Parse(args[^1]); rule.ExpectedNodes = Ids; rule.RequireCompleteCoverage = true; }
        var runtime = new RuntimeStateOptions { Enabled = true, NodeId = id, StateDirectory = Path.Combine(root, id, "runtime") };
        var communication = new NodeCommunicationOptions { Enabled = true, RequireHttps = false, AllowLegacyProtocol = false,
            ClusterId = "cc-decision-harness", NodeId = id, StateDirectory = Path.Combine(root, id, "protocol") };
        foreach (var (peer, index) in Ids.Select((peer, index) => (peer, index)).Where(p => p.peer != id))
        {
            RuntimeStateReplicationPolicy Policy() => new() { AllowedDomains = ["cc.access", "cc.policy", "cc.decision", "cc.triggered"],
                AllowUngroupedKeys = true, TrustedForwardedSources = Ids.ToList() };
            runtime.Peers[peer] = new() { Endpoint = $"http://127.0.0.1:{ports[index]}", ProtocolVersion = 2, DurableSyncEnabled = durable, Send = Policy(), Receive = Policy() };
            communication.Peers[peer] = new() { ActiveKeyId = "test", Keys = new() { ["test"] = Secret }, AllowedOperations = ["runtime.merge", "runtime.sync"] };
            if (cohortMode) communication.Peers[peer].AllowedOperations.Add(CcExecutionQueryService.Operation);
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root, EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, ports[Array.IndexOf(Ids, id)]));
        var statistics = DispatchProxy.Create<IStatisticService, Rules>();
        ((Rules)(object)statistics).Rule = rule;
        builder.Services.AddSingleton(statistics);
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new Fixed<RuntimeStateOptions>(runtime));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new Fixed<NodeCommunicationOptions>(communication));
        builder.Services.AddSingleton<RuntimeStateStore>();
        builder.Services.AddSingleton<IRuntimeStateStore>(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<RuntimeStateSyncSender>();
        builder.Services.AddSingleton<RuntimeStateSyncReceiver>();
        builder.Services.AddSingleton<CcPropagationTracker>();
        builder.Services.AddSingleton<CcBanLog>();
        var faultClock = new FaultClock();
        if (cohortMode)
        {
            builder.Services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, CcQueryTimingStartupFilter>();
            builder.Services.AddSingleton<NodeIdentityService>();
            builder.Services.AddHostedService(s => s.GetRequiredService<NodeIdentityService>());
            builder.Services.AddSingleton(s => new CcCohortState(s.GetRequiredService<IRuntimeStateStore>(),
                s.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>(), s.GetRequiredService<NodeIdentityService>(),
                faultClock, s.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>()));
            builder.Services.AddSingleton<CcPeerQueryClient>();
            builder.Services.AddSingleton<CcExecutionQueryService>();
            LyWaf.Plugins.Sys.Analysis.AnalysisNodeRequests.Register(builder.Services);
            builder.Services.AddHostedService<CcCohortMonitor>();
        }
        builder.Services.AddSingleton<CcDecisionService>();
        builder.Services.AddHostedService(s => s.GetRequiredService<CcDecisionService>());
        builder.Services.AddSingleton<RuntimeStateReplicator>();
        builder.Services.AddSingleton(s => new CcRuleChecker(statistics, null!, s.GetRequiredService<IRuntimeStateStore>(), s.GetRequiredService<CcDecisionService>()));
        await using var app = builder.Build();
        var partition = false;
        app.Use(async (context, next) =>
        {
            var requestWatch = Stopwatch.StartNew();
            if (partition && context.Request.Path.StartsWithSegments(NodeMessageAuthentication.Prefix.TrimEnd('/')))
            { context.Response.StatusCode = 503; return; }
            try { await next(context); }
            catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
            finally
            {
                if (cohortMode && context.Request.Path.StartsWithSegments(NodeMessageAuthentication.Prefix.TrimEnd('/')))
                    Console.WriteLine($"PROTOCOL status={context.Response.StatusCode} elapsedMs={requestWatch.ElapsedMilliseconds} path={context.Request.Path}");
            }
        });
        app.MapNodeCommunication(ports[Array.IndexOf(Ids, id)]);
        var seeded = 0;
        var connected = 0;
        CcPolicyDescriptor? policy = cohortMode ? app.Services.GetRequiredService<CcDecisionService>().Register(rule, "host:a.example", "192.0.2.55") : null;
        if (cohortMode)
        {
            var subjects = int.Parse(args.FirstOrDefault(a => a.StartsWith("--subjects=", StringComparison.Ordinal))?.Split('=')[1] ?? "1");
            for (var subject = 1; subject < subjects; subject++)
                app.Services.GetRequiredService<CcDecisionService>().Register(rule, "host:a.example", "198.51.100." + subject);
            app.MapPost("/fault/{kind}/{value:int}", (string kind, int value) =>
            {
                switch (kind)
                {
                    case "partition": partition = value != 0; break;
                    case "clock": Interlocked.Exchange(ref faultClock.OffsetMs, value); break;
                    case "revision": rule.Threshold = value; break;
                    default: return Results.BadRequest();
                }
                return Results.Ok();
            });
            app.MapGet("/cohort", (CcCohortState cohort, CcDecisionService decisions, CcBanLog log, NodeIdentityService identity) =>
            {
                var observed = cohort.Read(policy!, rule);
                var decision = log.Read().FirstOrDefault()?.DecisionId;
                return Results.Json(new { pid = Environment.ProcessId, identity = identity.Capture(), observed.Coverage,
                    observed.Count, logs = log.Read(), execution = decision is null ? null : decisions.InspectClusterExecution(policy!, decision),
                    rss = Environment.WorkingSet, utc = DateTimeOffset.UtcNow });
            });
        }
        app.MapPost("/seed/{amount:int}", (int amount, CcRuleChecker checker) =>
        {
            if (seeded != 0 || amount is < 1 or > 40) return Results.BadRequest();
            for (var i = 0; i < amount; i++)
            {
                var context = new DefaultHttpContext();
                context.Request.Host = new("a.example"); context.Request.Path = "/protected";
                if (checker.CheckRequest(context, "192.0.2.55").IsTriggered) throw new InvalidOperationException("local traffic triggered prematurely");
            }
            seeded = amount;
            return Results.Ok();
        });
        app.MapPost("/connect", async (RuntimeStateReplicator replicator) =>
        {
            if (Interlocked.Exchange(ref connected, 1) == 0) await replicator.StartAsync(default);
            return Results.Ok();
        });
        app.MapGet("/state", (RuntimeStateStore store, CcBanLog log, RuntimeStateReplicator replicator, CcPropagationTracker tracker) => Results.Json(new
        {
            pid = Environment.ProcessId, seeded,
            confirmations = log.Read().Where(l => l.DecisionId is not null).Select(l => tracker.Read(m =>
                m.Domain == CcDecisionService.DecisionDomain && !m.Deleted && m.Value?.Deserialize<CcDecision>(Json)?.DecisionId == l.DecisionId)).ToArray(),
            count = store.GetSnapshots("cc.access").Sum(s => s.CounterValue ?? 0), logs = log.Read(),
            windows = store.GetSnapshots("cc.access"),
            replicationDropped = replicator.DroppedMutations, authorizationRejected = replicator.AuthorizationRejectedMutations,
            penalties = store.GetValues<DateTimeOffset>("cc.triggered").Select(x => new { key = x.Key, until = x.Value }).ToArray()
        }));
        app.MapGet("/protected", async (HttpContext context, CcRuleChecker checker) =>
        {
            var result = checker.CheckRequest(context, "192.0.2.55");
            if (result.IsTriggered) await checker.ExecuteAction(context, result, "192.0.2.55");
            else await context.Response.WriteAsync("allowed");
        });
        app.MapPost("/stop", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.Ok(); });
        await app.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(root, id, "ready"), Environment.ProcessId.ToString());
        await app.WaitForShutdownAsync();
        if (connected != 0) await app.Services.GetRequiredService<RuntimeStateReplicator>().StopAsync(default);
    }

    public static async Task Run(string? output, bool durable)
    {
        var root = Path.GetFullPath(output ?? Path.Combine(Path.GetTempPath(), "cc-three-node-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var listeners = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var listener in listeners) listener.Start();
        var ports = listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        foreach (var listener in listeners) listener.Stop();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var children = new List<Process>();
        var captures = new List<Task>();
        var amounts = new[] { 40, 35, 30 };
        var lines = new List<string>();
        JsonElement[] states = [];
        void Record(string text) { lines.Add(text); Console.WriteLine(text); }
        string Url(int i, string path) => $"http://127.0.0.1:{ports[i]}{path}";
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task<JsonElement> State(int i) => JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync(Url(i, "/state")));
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var dir = Path.Combine(root, Ids[i]); Directory.CreateDirectory(dir);
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
                // This harness supplies fixed in-memory configuration; do not watch its runtime journal tree.
                start.Environment["DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] = "false";
                foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "child", Ids[i], root, string.Join(',', ports) }) start.ArgumentList.Add(arg);
                if (durable) start.ArgumentList.Add("--durable");
                var process = Process.Start(start)!; children.Add(process);
                captures.Add(Capture(process.StandardOutput, Path.Combine(dir, "stdout.log")));
                captures.Add(Capture(process.StandardError, Path.Combine(dir, "stderr.log")));
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            for (var i = 0; i < 3; i++)
            {
                while (!File.Exists(Path.Combine(root, Ids[i], "ready")))
                { Check(!children[i].HasExited, "child exited before ready"); await Task.Delay(50, timeout.Token); }
                using var response = await http.PostAsync(Url(i, $"/seed/{amounts[i]}"), null); response.EnsureSuccessStatusCode();
                var state = await State(i);
                Check(state.GetProperty("count").GetInt64() == amounts[i] && state.GetProperty("logs").GetArrayLength() == 0, "seed must remain below threshold");
                Record($"SEEDED {Ids[i]} pid={children[i].Id} count={amounts[i]} decisions=0");
            }
            Check(children.Select(p => p.Id).Distinct().Count() == 3, "three independent processes required");
            // From here until the assertions below, no endpoint calls CheckRequest or increments counters.
            for (var i = 0; i < 3; i++) { using var response = await http.PostAsync(Url(i, "/connect"), null); response.EnsureSuccessStatusCode(); }
            var convergence = Stopwatch.StartNew();
            while (true)
            {
                states = new JsonElement[3];
                for (var i = 0; i < 3; i++) states[i] = await State(i);
                if (states.All(s => s.GetProperty("count").GetInt64() == 105 && s.GetProperty("logs").GetArrayLength() == 1
                    && s.GetProperty("penalties").GetArrayLength() == 1)) break;
                await Task.Delay(100, timeout.Token);
            }
            var ids = states.Select(s => s.GetProperty("logs")[0].GetProperty("decisionId").GetString()).Distinct().ToArray();
            Record($"CONVERGED elapsedMs={convergence.ElapsedMilliseconds}");
            Check(ids.Length == 1 && ids[0] is not null, "all nodes must share one stable logical decision");
            for (var i = 0; i < 3; i++)
            {
                Check(states[i].GetProperty("seeded").GetInt32() == amounts[i], "no extra business requests");
                Check(states[i].GetProperty("logs")[0].GetProperty("nodes").EnumerateObject().All(n => n.Value.GetProperty("observedCount").GetInt64() == 105), "decision evidence must observe 105");
                Record($"PASS NO_NEW_REQUESTS node={Ids[i]} sum=105 logicalLogs=1 penalty=1 decisionId={ids[0]}");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "no-new-requests.json"), JsonSerializer.Serialize(states, Json));
            while (durable && !states.All(s => s.GetProperty("confirmations").GetArrayLength() == 1
                && s.GetProperty("confirmations")[0].GetProperty("allNodesConfirmed").GetBoolean()))
            {
                await Task.Delay(100, timeout.Token);
                for (var i = 0; i < 3; i++) states[i] = await State(i);
            }
            if (durable) Record("PASS durable ACK: each node has exact-decision receipt confirmation from both configured peers");
            await Task.Delay(1200);
            for (var i = 0; i < 3; i++)
            {
                var after = await State(i);
                Check(after.GetProperty("logs").GetArrayLength() == 1, "background sweeps must not duplicate logs");
                Check(after.GetProperty("penalties")[0].GetProperty("until").GetDateTimeOffset() <= states[i].GetProperty("penalties")[0].GetProperty("until").GetDateTimeOffset(), "background must not renew penalty");
                using var blocked = new HttpRequestMessage(HttpMethod.Get, Url(i, "/protected")); blocked.Headers.Host = "a.example";
                using var response = await http.SendAsync(blocked); Check(response.StatusCode == HttpStatusCode.Forbidden, "background decision must enforce 403");
                using var other = new HttpRequestMessage(HttpMethod.Get, Url(i, "/protected")); other.Headers.Host = "other.example";
                using var allowed = await http.SendAsync(other); Check(allowed.StatusCode == HttpStatusCode.OK, "other scope must stay allowed");
            }
            Record("PASS actual HTTP enforcement: all three nodes 403 for target scope, 200 for other scope; no duplicate or renewed decision");
        }
        finally
        {
            for (var i = 0; i < children.Count; i++)
            {
                try { using var response = await http.PostAsync(Url(i, "/stop"), null); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
                try { await children[i].WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { children[i].Kill(entireProcessTree: true); await children[i].WaitForExitAsync(); }
                children[i].Dispose();
            }
            await Task.WhenAll(captures);
            await File.WriteAllTextAsync(Path.Combine(root, "last-state.json"), JsonSerializer.Serialize(
                states.Select(s => s.ValueKind == JsonValueKind.Undefined ? (JsonElement?)null : s), Json));
            await File.WriteAllLinesAsync(Path.Combine(root, "verification.log"), lines);
            Console.WriteLine("Artifacts: " + root);
        }
    }
    private static async Task Capture(StreamReader input, string path)
    {
        await using var output = new StreamWriter(path) { AutoFlush = true };
        while (await input.ReadLineAsync() is { } line) await output.WriteLineAsync(line);
    }
}

public class Rules : DispatchProxy
{
    public AdvancedCcRule Rule = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    { nameof(IStatisticService.GetAdvancedCcRules) => new List<AdvancedCcRule> { Rule }, _ => throw new NotSupportedException() };
}
sealed class Fixed<T>(T value) : IOptionsMonitor<T>
{ public T CurrentValue => value; public T Get(string? name) => value; public IDisposable? OnChange(Action<T, string?> listener) => null; }

sealed class FaultClock : TimeProvider
{
    public long OffsetMs;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddMilliseconds(Interlocked.Read(ref OffsetMs));
}
