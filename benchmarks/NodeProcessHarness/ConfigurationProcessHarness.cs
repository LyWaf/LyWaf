using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Shared;
using LyWaf.Plugins.Core;
using LyWaf.Config;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

static class ConfigurationProcessHarness
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const string Secret = "isolated-config-process-key-never-use-in-production-1234";
    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task Host(string[] args)
    {
        var role = args[1];
        var directory = Path.GetFullPath(args[2]);
        var proxyPort = int.Parse(args[3]);
        var controller = role == "controller";
        var v7 = args.Contains("--v7");
        var reload = args.Contains("--reload");
        var peer = controller ? "node" : "controller";
        SharedData.ConfigFilePath = Path.Combine(directory, "config.yaml");
        if (!File.Exists(SharedData.ConfigFilePath))
            await File.WriteAllTextAsync(SharedData.ConfigFilePath, Candidate("baseline", role));
        var distribution = new DistributionOptions
        {
            Enabled = true, Role = controller ? "Controller" : "Node", NodeId = role,
            StateDirectory = Path.Combine(directory, "distribution"), RequestTimeoutSeconds = 30,
            Nodes = controller ? new() { ["node"] = new() { ProtocolVersion = 2,
                Endpoint = $"http://127.0.0.1:{proxyPort}" } } : new()
        };
        var communication = new NodeCommunicationOptions
        {
            Enabled = true, Role = distribution.Role, NodeId = role, ClusterId = "config-process-test",
            RequireHttps = false, AllowLegacyProtocol = false, StateDirectory = Path.Combine(directory, "protocol"),
            Peers = new() { [peer] = new() { Role = controller ? "Node" : "Controller", ActiveKeyId = "test",
                Keys = new() { ["test"] = Secret }, AllowedOperations =
                ["configuration.prepare", "configuration.commit", "configuration.rollback", "configuration.result", "node.status", "node.handshake"] } }
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ContentRootPath = directory, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddYamlFile(SharedData.ConfigFilePath, optional: false, reloadOnChange: false);
        if (reload)
        {
            builder.Configuration.AddDraftAwareYamlFile(Path.Combine(directory, "protocol.yaml"), optional: false);
            builder.Services.AddLyWafDistribution(builder.Configuration);
            // This agent fixture retains production options validators but no controller/telemetry workers.
            foreach (var service in builder.Services.Where(s => s.ServiceType == typeof(IHostedService)
                || s.ServiceType == typeof(IStartupFilter)).ToArray()) builder.Services.Remove(service);
        }
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<DistributionOptions>>(new FixedOptions<DistributionOptions>(distribution));
        if (!reload) builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new FixedOptions<NodeCommunicationOptions>(communication));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ConfigVersionService>();
        builder.Services.AddSingleton<ConfigurationLifecycleService>();
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<NodeCommandInbox>();
        builder.Services.AddSingleton<DistributionNodeService>();
        builder.Services.AddSingleton<NodeIdentityService>();
        builder.Services.AddHostedService(s => s.GetRequiredService<NodeIdentityService>());
        builder.Services.AddSingleton<IOptionsMonitor<PluginOptions>>(new FixedOptions<PluginOptions>(new()));
        builder.Services.AddSingleton<IOptionsMonitor<PluginNodeMessageOptions>>(new FixedOptions<PluginNodeMessageOptions>(new()));
        builder.Services.AddSingleton<IPluginEventBus, PluginEventBus>();
        // This isolated host installs no plugins; metadata comes from the real empty registry.
        builder.Services.AddSingleton<IPluginManager, PluginManager>();
        builder.Services.AddNodeHandshakeServices();
        if (v7)
        {
            communication.AllowLegacyProtocol = true;
            distribution.SharedSecret = Secret;
            builder.Services.AddSingleton<DistributionAuthentication>();
            builder.Services.AddSingleton<NodeDashboardAgent>();
            builder.Services.AddSingleton<MultiNodeDashboardService>();
            builder.Services.AddSingleton<DistributionControllerService>();
            builder.Services.AddSingleton<NodeReconciliationService>();
        }
        if (controller)
        {
            builder.Services.AddHttpClient("distribution-node").ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
            builder.Services.AddSingleton<IDistributionNodeClient, DistributionNodeClient>();
            builder.Services.AddSingleton<DistributionControllerService>();
            builder.Services.AddHostedService<NodeOutboxRecoveryService>();
            builder.Services.AddHostedService<DistributionOutboxConfirmationService>();
            builder.Services.AddHostedService<NodeHandshakeMonitor>();
        }
        await using var app = builder.Build();
        if (reload)
        {
            app.MapPost("/test/reload", (ConfigurationLifecycleService lifecycle) =>
            {
                var before = lifecycle.Capture(app.Configuration);
                return Results.Json(lifecycle.ReloadAndValidate((IConfigurationRoot)app.Configuration, before));
            });
        }
        if (v7)
        {
            communication.Peers[peer].Keys["old"] = Secret + "old";
            app.MapPost("/test/revoke", () => { communication.Peers[peer].Keys.Remove("old"); return Results.Ok(); });
        }
        if (!controller)
        {
            if (v7) app.MapLyWafDistributionApi(proxyPort);
            else app.MapNodeCommunication(proxyPort);
            app.MapGet("/test/node", async (DistributionNodeService service, ConfigVersionService versions) => Results.Json(new
            {
                status = service.GetStatus(), versions = await versions.GetVersionsAsync(),
                marker = app.Configuration["SimpleRes:marker:Content"], localIdentity = app.Configuration["NodeLocal:Identity"],
                config = await File.ReadAllTextAsync(SharedData.ConfigFilePath)
            }));
        }
        else
        {
            app.MapGet("/test/registration", (NodeRegistrationObserver observer) =>
            {
                var observation = observer.Get("node");
                return Results.Json(new { observation, ready = observation is not null && observer.CanStartTasks("node", observation.ObservationId) });
            });
            app.MapPost("/test/gate-check", async (IDistributionNodeClient client) =>
            {
                try { await client.PrepareAsync("node", new("gate-check", "unsubmitted", "", ""), default); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("handshake reconciliation"))
                { return Results.Json(new { blocked = true }); }
                throw new InvalidOperationException("Real handshake gate did not reject unready dispatch");
            });
            app.MapPost("/test/deploy", async (DeploymentInput input, ConfigVersionService versions, DistributionControllerService service) =>
            {
                var version = await versions.WriteDraftAsync(Candidate(input.Marker, "controller"), "process-test");
                return Results.Json(await service.CreateAsync(new(version.Id, ["node"], 1, 1, true), default));
            });
            app.MapPost("/test/deploy/{id}/rollback", async (string id, DistributionControllerService service, HttpContext context) =>
                Results.Json(await service.RollbackAsync(id, context.RequestAborted)));
        }
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await File.WriteAllTextAsync(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new { address, pid = Environment.ProcessId }));
        await app.WaitForShutdownAsync();
    }

    static string Candidate(string marker, string identity) =>
        $"SimpleRes:\n  marker:\n    Content: {marker}\nNodeLocal:\n  Identity: {identity}\n";

    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-config-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        void Record(string message)
        {
            var line = $"{DateTimeOffset.UtcNow:O} {message}";
            Console.WriteLine(line); log.WriteLine(line);
        }
        Record($"BEGIN root={root}; actual controller/node/client/inbox/outbox/lifecycle and both recovery hosted services");
        var failures = new List<string>();
        foreach (var scenario in new[] { "lost-responses", "crash-prepare", "crash-commit", "crash-rollback" })
        {
            var directory = Path.Combine(root, scenario);
            Directory.CreateDirectory(directory);
            var children = new List<Child>();
            Task<string>? rollbackResponse = null;
            await using var proxy = await ResponseFaultProxy.Start(directory);
            async Task<Child> Start(string role)
            {
                var path = Path.Combine(directory, role);
                Directory.CreateDirectory(path);
                File.Delete(Path.Combine(path, "ready.json"));
                var info = new ProcessStartInfo("dotnet") { WorkingDirectory = path, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "configuration-child", role, path, proxy.Port.ToString() })
                    info.ArgumentList.Add(arg);
                var child = new Child(Process.Start(info)!, path);
                children.Add(child);
                Record($"START scenario={scenario} role={role} ownedPid={child.Process.Id} directory={path}");
                Check(await Until(() => Task.FromResult(File.Exists(Path.Combine(path, "ready.json"))), 30), "child readiness");
                using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "ready.json")));
                Check(ready.RootElement.GetProperty("pid").GetInt32() == child.Process.Id, "readiness PID");
                child.Address = ready.RootElement.GetProperty("address").GetString()!;
                Record($"READY scenario={scenario} role={role} pid={child.Process.Id} address={child.Address} proxyPort={proxy.Port}");
                return child;
            }
            async Task Kill(Child child, string reason)
            {
                if (child.Process.HasExited) return;
                child.Process.Kill();
                await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Record($"KILL reason={reason} ownedPid={child.Process.Id} exit={child.Process.ExitCode}");
            }
            try
            {
                var node = await Start("node");
                proxy.Target = node.Address;
                proxy.BlockHandshake = true;
                var controller = await Start("controller");
                using (var gate = await Http.PostAsync(controller.Address + "/test/gate-check", null))
                {
                    gate.EnsureSuccessStatusCode();
                    Check((await gate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("blocked").GetBoolean()
                        && proxy.Count("configuration.prepare") == 0, "real client gate rejects unready prepare without wire dispatch");
                }
                proxy.BlockHandshake = false;
                Check(await Until(async () => (await Http.GetFromJsonAsync<JsonElement>(controller.Address + "/test/registration")).GetProperty("ready").GetBoolean(), 40),
                    "background authenticated handshake and read-only reconciliation reach Ready");
                await File.WriteAllTextAsync(Path.Combine(directory, "ready-registration.json"), await Http.GetStringAsync(controller.Address + "/test/registration"));
                Check(proxy.Count("node.handshake") >= 2 && proxy.Count("node.status") >= 1, "actual handshake/status/recheck on wire");
                Record($"PASS {scenario}: real gate blocked unready prepare; background handshake/status/recheck reached Ready");
                var initialControllerConfig = await File.ReadAllTextAsync(Path.Combine(controller.Directory, "config.yaml"));
                var initialNodeConfig = await File.ReadAllTextAsync(Path.Combine(node.Directory, "config.yaml"));
                var crashStage = scenario switch { "crash-prepare" => "prepare", "crash-rollback" => "rollback", _ => "commit" };
                var isRollback = crashStage == "rollback";
                var expectedIndex = 0;
                if (scenario == "lost-responses")
                {
                    proxy.Arm("configuration.prepare", "drop");
                    proxy.Arm("configuration.commit", "drop");
                }
                else proxy.Arm("configuration." + crashStage, "hold");
                using var created = await Http.PostAsJsonAsync(controller.Address + "/test/deploy", new DeploymentInput(scenario));
                created.EnsureSuccessStatusCode();
                var deployment = (await created.Content.ReadFromJsonAsync<ConfigurationDeployment>(Json))!;
                Record($"DEPLOY scenario={scenario} deployment={deployment.Id}");
                async Task<ConfigurationDeployment> ReadDeployment() => JsonSerializer.Deserialize<ConfigurationDeployment>(
                    await File.ReadAllTextAsync(Path.Combine(controller.Directory, "distribution", $"deployment-{deployment.Id}.json")), Json)!;
                Task<NodeOutboxRecord[]> ReadOutbox() => ReadRecords(controller.Directory);
                async Task<NodeEvidence> NodeState() =>
                    (await Http.GetFromJsonAsync<NodeEvidence>(node.Address + "/test/node", Json))!;

                if (isRollback)
                {
                    Check(await Until(async () => (await ReadDeployment()).State == "Completed", 25), "deployment completed before real rollback");
                    var applied = await NodeState();
                    Check(applied.Status.State == "Applied" && applied.Marker == scenario, "real candidate applied before rollback");
                    expectedIndex = (await ReadDeployment()).NextIndex;
                    Check(expectedIndex == 1, "completed one-node deployment index");
                    Record($"PASS rollback setup: node Applied marker={applied.Marker} NextIndex={expectedIndex}");
                    rollbackResponse = ObserveRollback(controller.Address + $"/test/deploy/{deployment.Id}/rollback");
                }

                if (scenario == "lost-responses")
                {
                    Check(await Until(async () => (await ReadDeployment()).State == "Completed", 25), "live response-loss deployment completed");
                    var state = await NodeState();
                    var outbox = await ReadOutbox();
                    Check(proxy.Count("configuration.prepare") == 1 && proxy.Count("configuration.commit") == 1,
                        "commands must not be resent after losing responses");
                    Check(proxy.ResultCount("configuration.prepare") >= 1 && proxy.ResultCount("configuration.commit") >= 1,
                        "lost response recovered by result queries for both stages");
                    Check(proxy.Dropped == 2 && outbox.Length == 2 && outbox.All(x => x.State == "Completed" && x.Result?.Ack == true && x.Attempts == 2),
                        "both durable outbox records completed after one query");
                    Check(state.Status.State == "Applied" && state.Marker == scenario && state.LocalIdentity == "node", "real reload and local overlay");
                    Check(state.Versions.Count(x => x.Action == "distribution:" + deployment.Id) == 1, "one real configuration application");
                    Check((await ReadDeployment()).Targets["node"].ConfigVersionId == state.Status.ConfigVersionId, "controller version confirmation");
                    Record($"PASS {scenario}: prepare=1 commit=1 result.prepare={proxy.ResultCount("configuration.prepare")} result.commit={proxy.ResultCount("configuration.commit")} dropped=2; Outbox Completed attempts=2 each; one applied version={state.Status.ConfigVersionId}");
                }
                else
                {
                    var held = await proxy.Held.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Check(held.Stage == crashStage && held.Ack, "proxy holds a real successful ACK before killing controller");
                    var before = await ReadDeployment();
                    var beforeRecords = await ReadOutbox();
                    var command = beforeRecords.Single(x => x.Message.Type == "configuration." + crashStage);
                    Check(command.State == "Unknown" && command.Attempts == 1 && command.Result is null, "durable in-flight outbox");
                    NodeEvidence? beforeNode = null;
                    if (isRollback)
                    {
                        beforeNode = await NodeState();
                        Check(before.State == "RollingBack" && before.Targets["node"].State == "Unknown"
                            && before.Targets["node"].PendingStage == "rollback", "durable rollback intent before kill");
                        Check(beforeNode.Status.State == "RolledBack" && beforeNode.Marker == "baseline" && beforeNode.LocalIdentity == "node",
                            "node really rolled back and reloaded before controller kill");
                        Check(beforeNode.Versions.Count(x => x.Action == "distribution-rollback:" + deployment.Id) == 1
                            && beforeNode.Status.ConfigVersionId == held.ConfigVersionId, "one actual rollback version matches held ACK");
                    }
                    await File.WriteAllTextAsync(Path.Combine(directory, "before-kill.json"), JsonSerializer.Serialize(new { deployment = before, outbox = beforeRecords, node = beforeNode }, Json));
                    Record($"HELD stage={crashStage} nodeAck=true target={before.Targets["node"].State} pendingStage={before.Targets["node"].PendingStage ?? "null"}; outbox=Unknown attempts=1");
                    await Kill(controller, "forced-controller-crash-after-node-ACK");
                    if (rollbackResponse is not null)
                    {
                        var outcome = await rollbackResponse;
                        Record("ROLLBACK caller after forced kill: " + outcome);
                        Check(outcome != "unexpected-http-response", "controller must not return rollback response before kill");
                    }
                    proxy.Release();
                    var requestsBeforeRestart = proxy.Events.Count;
                    controller = await Start("controller");
                    // Only inspect durable files here. No controller HTTP/UI/recover/resume call can drive the recovery.
                    Check(await Until(async () => (await ReadOutbox()).Any(x => x.Message.MessageId == command.Message.MessageId
                        && x.State == "Completed" && x.Result?.Ack == true), 20), "background outbox recovery after kill");
                    var recovered = (await ReadOutbox()).Single(x => x.Message.MessageId == command.Message.MessageId);
                    Check(recovered.Attempts == 2 && recovered.Message == command.Message with { Payload = recovered.Message.Payload }, "original identity retained");
                    Check(proxy.Count("configuration." + crashStage) == 1 && proxy.ResultCount("configuration." + crashStage) >= 1,
                        "restart must only query outcome");
                    Check(proxy.Events.Skip(requestsBeforeRestart).All(x => x.Type is "configuration.result" or "node.handshake" or "node.status"), "no new business command after restart");
                    Record($"PASS {scenario}: background Outbox=Completed attempts=2 ACK stage={recovered.Result!.Stage}; original command count=1, result query count={proxy.ResultCount("configuration." + crashStage)}; no manual recovery call");
                    var expected = crashStage switch { "prepare" => "Prepared", "rollback" => "RolledBack", _ => "Ack" };
                    var expectedDeployment = isRollback ? "RollbackFailed" : "Paused";
                    var confirmed = await Until(async () => (await ReadDeployment()).Targets["node"].State == expected, 17);
                    if (crashStage == "prepare")
                    {
                        Check(await Until(async () => (await Http.GetFromJsonAsync<JsonElement>(controller.Address + "/test/registration")).GetProperty("ready").GetBoolean(), 40),
                            "prepared recovery must reach Ready through background handshake reconciliation");
                        await File.WriteAllTextAsync(Path.Combine(directory, "recovered-ready-registration.json"),
                            await Http.GetStringAsync(controller.Address + "/test/registration"));
                        Record("PASS prepared restart: real background handshake reconciliation reached Ready");
                    }
                    if (isRollback) await Task.Delay(6500);
                    var after = await ReadDeployment();
                    var state = await NodeState();
                    var appliedCount = state.Versions.Count(x => x.Action == "distribution:" + deployment.Id);
                    Check(state.Status.State == (crashStage switch { "prepare" => "Prepared", "rollback" => "RolledBack", _ => "Applied" }), "node command state survives controller crash");
                    Check(appliedCount == (crashStage == "prepare" ? 0 : 1), "no duplicate configuration application");
                    if (crashStage == "prepare")
                        Check(state.Config == initialNodeConfig && proxy.Count("configuration.commit") == 0, "prepare recovery cannot auto-commit");
                    else if (isRollback)
                    {
                        Check(state.Marker == "baseline" && state.LocalIdentity == "node", "rolled-back baseline remains loaded");
                        Check(state.Versions.Count(x => x.Action == "distribution-rollback:" + deployment.Id) == 1,
                            "no duplicate rollback application after another recovery interval");
                        Check(proxy.Count("configuration.prepare") == 1 && proxy.Count("configuration.commit") == 1
                            && proxy.Count("configuration.rollback") == 1 && proxy.ResultCount("configuration.rollback") >= 1,
                            "exactly one prepare/commit/rollback and at least one rollback result query");
                        Check(proxy.Events.Skip(requestsBeforeRestart).All(x => x.Type is "configuration.result" or "node.handshake" or "node.status"), "query-only across another recovery interval");
                        Check(recovered.Result.ConfigVersionId == state.Status.ConfigVersionId, "rollback ACK identifies actual restored version");
                    }
                    else Check(state.Marker == scenario && state.LocalIdentity == "node", "committed configuration really reloaded");
                    Check(after.NextIndex == expectedIndex, "recovery must not advance batch");
                    await File.WriteAllTextAsync(Path.Combine(directory, "after-recovery.json"), JsonSerializer.Serialize(new
                    { deployment = after, outbox = await ReadOutbox(), node = state, requests = proxy.Events.ToArray() }, Json));
                    if (!confirmed || after.State != expectedDeployment || after.Targets["node"].ConfigVersionId != recovered.Result.ConfigVersionId)
                    {
                        var failure = $"{scenario}: transport recovered but confirmation not backfilled; deployment={after.State} target={after.Targets["node"].State} pendingStage={after.Targets["node"].PendingStage ?? "null"} expected={expectedDeployment}/{expected}; NextIndex={after.NextIndex}; appliedVersions={appliedCount}";
                        failures.Add(failure);
                        Record("BUG " + failure);
                    }
                    else Record($"PASS {scenario}: background confirmation target={expected} deployment={expectedDeployment} NextIndex={after.NextIndex}; appliedVersions={appliedCount}");
                    if (isRollback) Record($"PASS rollback stable after extra recovery interval: prepare=1 commit=1 rollback=1 result.rollback={proxy.ResultCount("configuration.rollback")}; rollbackVersions=1 marker=baseline version={state.Status.ConfigVersionId}");
                }
                Check(await File.ReadAllTextAsync(Path.Combine(controller.Directory, "config.yaml")) == initialControllerConfig,
                    "controller active configuration remains unchanged");
                Record($"PASS {scenario}: isolated controller active config unchanged; node-local identity preserved");
            }
            catch (Exception ex) { failures.Add(scenario + ": " + ex.Message); Record("FAIL " + scenario + " " + ex); }
            finally
            {
                proxy.Release();
                foreach (var child in children)
                {
                    await Kill(child, "cleanup");
                    await child.Logs;
                    child.Process.Dispose();
                }
                if (rollbackResponse is not null) await rollbackResponse;
                Record($"CLEANUP {scenario}: all owned children exited");
            }
        }
        Record(failures.Count == 0 ? "RESULT PASS scoped configuration process suite" : "RESULT FAIL " + string.Join(" | ", failures));
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }

    static async Task<string> ObserveRollback(string url)
    {
        try
        {
            using var response = await Http.PostAsync(url, null);
            return "unexpected-http-response";
        }
        catch (HttpRequestException ex) { return ex.GetType().Name; }
        catch (TaskCanceledException ex) { return ex.GetType().Name; }
    }

    static async Task<NodeOutboxRecord[]> ReadRecords(string directory)
    {
        var path = Path.Combine(directory, "protocol", "outbox");
        if (!Directory.Exists(path)) return [];
        var records = new List<NodeOutboxRecord>();
        foreach (var file in Directory.GetFiles(path, "*.json"))
            records.Add(JsonSerializer.Deserialize<NodeOutboxRecord>(await File.ReadAllTextAsync(file), Json)!);
        return records.ToArray();
    }
    static async Task<bool> Until(Func<Task<bool>> predicate, int seconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        { if (await predicate()) return true; await Task.Delay(200); }
        return false;
    }
    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public sealed record DeploymentInput(string Marker);
    public sealed record NodeEvidence(DistributionNodeStatus Status, ConfigVersionInfo[] Versions, string Marker, string LocalIdentity, string Config);
    sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    sealed class Child
    {
        public Process Process { get; }
        public string Directory { get; }
        public string Address { get; set; } = "";
        public Task Logs { get; }
        public Child(Process process, string directory)
        {
            Process = process; Directory = directory;
            Logs = Task.WhenAll(Copy(process.StandardOutput, "stdout"), Copy(process.StandardError, "stderr"));
        }
        async Task Copy(StreamReader reader, string name)
        {
            using var writer = new StreamWriter(Path.Combine(Directory, $"{Process.Id}.{name}.log")) { AutoFlush = true };
            while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
        }
    }
}

// Real HTTP forwarding preserves signed body/headers. Faults occur only after reading the node's full response.
sealed class ResponseFaultProxy : IAsyncDisposable
{
    readonly WebApplication app;
    readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(20) };
    readonly ConcurrentDictionary<string, string> faults = new();
    readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly StreamWriter wire;
    readonly object wireGate = new();
    int dropped;
    public int Dropped => Volatile.Read(ref dropped);
    public int Port { get; private set; }
    public string Target { get; set; } = "";
    public volatile bool BlockHandshake;
    public ConcurrentQueue<WireEvent> Events { get; } = new();
    public TaskCompletionSource<NodeAcknowledgement> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ResponseFaultProxy(WebApplication app, string directory)
    { this.app = app; wire = new(Path.Combine(directory, "wire.jsonl")) { AutoFlush = true }; }
    public static async Task<ResponseFaultProxy> Start(string directory)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ContentRootPath = directory, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var proxy = new ResponseFaultProxy(builder.Build(), directory);
        proxy.app.Run(proxy.Forward);
        await proxy.app.StartAsync();
        proxy.Port = new Uri(proxy.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        return proxy;
    }
    public void Arm(string type, string fault) => faults[type] = fault;
    public void Release() => release.TrySetResult();
    public int Count(string type) => Events.Count(x => x.Type == type);
    public int ResultCount(string originalType) => Events.Count(x => x.Type == "configuration.result" && x.OriginalType == originalType);
    async Task Forward(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();
        var message = JsonSerializer.Deserialize<NodeMessage>(body, ConfigurationProcessHarness.Json)!;
        if (BlockHandshake && message.Type == "node.handshake") { context.Response.StatusCode = 503; return; }
        var original = message.Type == "configuration.result"
            ? message.Payload.Deserialize<NodeMessage>(ConfigurationProcessHarness.Json)?.Type : null;
        using var request = new HttpRequestMessage(HttpMethod.Post, Target + context.Request.Path)
        { Content = new ByteArrayContent(body) };
        foreach (var header in context.Request.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        using var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        faults.TryRemove(message.Type, out var fault);
        var entry = new WireEvent(DateTimeOffset.UtcNow, message.Type, original, message.CorrelationId, message.MessageId,
            (int)response.StatusCode, fault ?? "forward", System.Text.Encoding.UTF8.GetString(bytes));
        Events.Enqueue(entry);
        lock (wireGate) wire.WriteLine(JsonSerializer.Serialize(entry, ConfigurationProcessHarness.Json));
        if (fault is "drop" or "hold")
        {
            if (fault == "hold")
            {
                var ack = JsonSerializer.Deserialize<NodeAcknowledgement>(bytes, ConfigurationProcessHarness.Json)
                    ?? throw new InvalidDataException("Node did not return ACK");
                Held.TrySetResult(ack);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(25));
            }
            Interlocked.Increment(ref dropped);
            context.Abort();
            return;
        }
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(bytes);
    }
    public async ValueTask DisposeAsync()
    {
        Release(); await app.StopAsync(); await app.DisposeAsync(); client.Dispose(); wire.Dispose();
    }
    public sealed record WireEvent(DateTimeOffset At, string Type, string? OriginalType, string DeploymentId,
        string MessageId, int Status, string Fault, string Response);
}
