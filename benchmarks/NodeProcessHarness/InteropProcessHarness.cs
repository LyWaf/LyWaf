using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Shared;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

static class InteropProcessHarness
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const string Secret = "isolated-interop-test-key-never-production-12345678";
    static readonly string[] ConfigOperations = ["configuration.prepare", "configuration.commit", "configuration.result", "configuration.rollback"];
    public sealed record Settings(string Id, string Cluster, Dictionary<string, string> Nodes, int IncomingPort, string OwnerToken);
    public sealed record IncrementInput(DateTimeOffset Window, long Amount);

    public static async Task Host(string[] args)
    {
        var root = Path.GetFullPath(args[1]);
        var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(root, "settings.json")), Json)!;
        var controller = settings.Id == "controller";
        SharedData.ConfigFilePath = Path.Combine(root, "config.yaml");
        if (!File.Exists(SharedData.ConfigFilePath))
            await File.WriteAllTextAsync(SharedData.ConfigFilePath, Candidate("baseline", settings.Id));
        var distribution = new DistributionOptions { Enabled = true, NodeId = settings.Id,
            Role = controller ? "Controller" : "Node", StateDirectory = Path.Combine(root, "distribution"),
            Nodes = controller ? settings.Nodes.ToDictionary(x => x.Key, x => new DistributionNodeOptions
                { Endpoint = x.Value, ProtocolVersion = 2 }) : new() };
        var protocol = new NodeCommunicationOptions { Enabled = true, NodeId = settings.Id, ClusterId = settings.Cluster,
            Role = distribution.Role, RequireHttps = false, AllowLegacyProtocol = false, AllowedClockSkewSeconds = 300,
            StateDirectory = Path.Combine(root, "protocol") };
        foreach (var id in controller ? settings.Nodes.Keys.ToArray() : new[] { "controller", settings.Id == "windows" ? "linux" : "windows" })
            protocol.Peers[id] = new() { Role = id == "controller" ? "Controller" : "Node", ActiveKeyId = "test",
                Keys = new() { ["test"] = Secret }, AllowedOperations = id == "controller" || controller ? [.. ConfigOperations] : ["runtime.merge"] };
        var runtime = new RuntimeStateOptions { Enabled = !controller, NodeId = settings.Id,
            StateDirectory = Path.Combine(root, "runtime"), ProtectionGroupKeyPrefixes = new() { ["interop"] = ["shared|"] } };
        if (!controller)
        {
            var peer = settings.Id == "windows" ? "linux" : "windows";
            var grant = new RuntimeStateReplicationPolicy { AllowedDomains = ["process.interop", "process.interop.nonce"],
                AllowedProtectionGroups = ["interop"], TrustedForwardedSources = ["windows", "linux"] };
            runtime.Peers[peer] = new() { ProtocolVersion = 2, Endpoint = settings.Nodes.GetValueOrDefault(peer, ""),
                DurableSyncEnabled = false, Send = grant, Receive = grant };
            if (!RuntimeStateReplicationAuthorization.IsValidConfiguration(runtime)) throw new InvalidOperationException("Invalid explicit runtime grants");
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddYamlFile(SharedData.ConfigFilePath, optional: false, reloadOnChange: false);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<DistributionOptions>>(new FixedOptions<DistributionOptions>(distribution));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new FixedOptions<NodeCommunicationOptions>(protocol));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new FixedOptions<RuntimeStateOptions>(runtime));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<ConfigVersionService>();
        builder.Services.AddSingleton<ConfigurationLifecycleService>();
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<NodeCommandInbox>();
        builder.Services.AddSingleton<DistributionNodeService>();
        if (controller)
        {
            builder.Services.AddHttpClient("distribution-node").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            builder.Services.AddSingleton<IDistributionNodeClient, DistributionNodeClient>();
            builder.Services.AddSingleton<DistributionControllerService>();
            builder.Services.AddHostedService<NodeOutboxRecoveryService>();
            builder.Services.AddHostedService<DistributionOutboxConfirmationService>();
        }
        else
        {
            builder.Services.AddSingleton<RuntimeStateStore>();
            builder.Services.AddSingleton<IRuntimeStateStore>(s => s.GetRequiredService<RuntimeStateStore>());
            builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateStore>());
            builder.Services.AddSingleton<RuntimeStateReplicator>();
            builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateReplicator>());
        }
        await using var app = builder.Build();
        if (controller)
            app.MapPost("/test/deploy", async (ConfigVersionService versions, DistributionControllerService service) =>
            {
                var version = await versions.WriteDraftAsync(Candidate("mixed-os", "controller"), "interop-test");
                return Results.Json(await service.CreateAsync(new(version.Id, settings.Nodes.Keys.ToArray(), 2, 2, false), default));
            });
        else
        {
            app.MapGet("/test/state", async (DistributionNodeService service, ConfigVersionService versions, RuntimeStateStore store) => Results.Json(new
            {
                status = service.GetStatus(), versions = await versions.GetVersionsAsync(),
                marker = app.Configuration["SimpleRes:marker:Content"], localIdentity = app.Configuration["NodeLocal:Identity"],
                snapshots = store.GetSnapshots(), runtime = store.GetStatus()
            }));
            app.MapPost("/test/increment", (IncrementInput input, RuntimeStateStore store) => Results.Json(
                store.IncrementWindow("process.interop", "shared", input.Window, TimeSpan.FromHours(1), input.Amount)));
        }
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        if (!controller) app.MapNodeCommunication(settings.IncomingPort);
        await File.WriteAllTextAsync(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(new
        { address, pid = Environment.ProcessId, ownerToken = settings.OwnerToken,
            os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription }, Json));
        // The launcher owns stdin. EOF/stop or the hard lifetime ends only this host.
        var stop = Task.Run(() => Console.ReadLine());
        await Task.WhenAny(stop, Task.Delay(TimeSpan.FromMinutes(5)), app.WaitForShutdownAsync());
        if (stop.IsCompletedSuccessfully && stop.Result == "kill:" + settings.OwnerToken && OperatingSystem.IsLinux())
        {
            Console.WriteLine($"OWNED SELF SIGKILL pid={Environment.ProcessId} root={root}");
            Console.Out.Flush();
            using var self = Process.GetCurrentProcess();
            self.Kill();
            await Task.Delay(Timeout.Infinite);
        }
        await app.StopAsync();
    }

    static string Candidate(string marker, string id) => $"SimpleRes:\n  marker:\n    Content: {marker}\nNodeLocal:\n  Identity: {id}\n";

    public static async Task Run(string linuxExecutable)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Launcher requires Windows with Debian WSL");
        var root = Path.Combine(Path.GetTempPath(), "lywaf-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        void Record(string value) { var line = $"{DateTimeOffset.UtcNow:O} {value}"; Console.WriteLine(line); log.WriteLine(line); }
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        var children = new List<(Process Process, Task Output, Task Error, string Id)>();
        await using var linuxProxy = new FaultProxy();
        await using var windowsProxy = new FaultProxy();
        var ownerTokens = new Dictionary<string, string>();
        var hostPids = new Dictionary<string, int>();
        var generation = 0;
        var window = DateTimeOffset.UtcNow;
        string WslPath(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
        async Task<string> Start(string id, Dictionary<string, string> nodes)
        {
            var directory = Path.Combine(root, id);
            Directory.CreateDirectory(directory);
            File.Delete(Path.Combine(directory, "ready.json"));
            var ownerToken = Guid.NewGuid().ToString("N");
            ownerTokens[id] = ownerToken;
            var incoming = id == "linux" ? linuxProxy.Port : windowsProxy.Port;
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(
                new Settings(id, Path.GetFileName(root), nodes, incoming, ownerToken), Json));
            var info = new ProcessStartInfo(id == "linux" ? "wsl.exe" : "dotnet") { WorkingDirectory = directory,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var arguments = id == "linux"
                ? new[] { "-d", "Debian", "--exec", WslPath(Path.GetFullPath(linuxExecutable)), "interop-host", WslPath(directory) }
                : new[] { Assembly.GetExecutingAssembly().Location, "interop-host", directory };
            foreach (var arg in arguments) info.ArgumentList.Add(arg);
            var process = Process.Start(info)!;
            async Task Capture(StreamReader reader, string name) => await File.WriteAllTextAsync(Path.Combine(directory, name), await reader.ReadToEndAsync());
            var instance = ++generation;
            children.Add((process, Capture(process.StandardOutput, $"{instance}.stdout.log"), Capture(process.StandardError, $"{instance}.stderr.log"), id));
            Record($"START id={id} ownedLauncherPid={process.Id}");
            var readyFile = Path.Combine(directory, "ready.json");
            for (var i = 0; !File.Exists(readyFile); i++)
            {
                if (process.HasExited || i >= 150) throw new InvalidOperationException($"Host {id} failed readiness; inspect {directory}");
                await Task.Delay(200);
            }
            using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(readyFile));
            var evidence = ready.RootElement;
            Check(!process.HasExited && evidence.GetProperty("ownerToken").GetString() == ownerToken, "owned launch token " + id);
            hostPids[id] = evidence.GetProperty("pid").GetInt32();
            if (id != "linux") Check(hostPids[id] == process.Id, "owned Windows PID " + id);
            Check(id == "linux" ? evidence.GetProperty("os").GetString()!.Contains("Debian") : evidence.GetProperty("os").GetString()!.Contains("Windows"), "actual OS " + id);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{instance}.ready.json"), evidence.ToString());
            if (id == "linux") linuxProxy.TargetPort = new Uri(evidence.GetProperty("address").GetString()!).Port;
            if (id == "windows") windowsProxy.TargetPort = new Uri(evidence.GetProperty("address").GetString()!).Port;
            Record($"READY id={id} {evidence}");
            return evidence.GetProperty("address").GetString()!;
        }
        async Task<JsonElement> State(string address) => (await http.GetFromJsonAsync<JsonElement>(address + "/test/state", Json));
        long Value(JsonElement state, string domain = "process.interop") => state.GetProperty("snapshots").EnumerateArray()
            .Where(x => x.GetProperty("domain").GetString() == domain).Sum(x => x.GetProperty("counterValue").GetInt64());
        async Task Increment(string address, long amount)
        {
            using var response = await http.PostAsJsonAsync(address + "/test/increment", new IncrementInput(window, amount));
            response.EnsureSuccessStatusCode();
        }
        async Task Until(Func<Task<bool>> check, string description, int seconds = 80)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < seconds) { if (await check()) return; await Task.Delay(200); }
            throw new TimeoutException(description);
        }
        try
        {
            Record("BEGIN Windows controller + Windows node + Debian Linux node; production v2 config and runtime.merge");
            var linuxEndpoint = $"http://127.0.0.1:{linuxProxy.Port}";
            var windowsEndpoint = $"http://127.0.0.1:{windowsProxy.Port}";
            var linux = await Start("linux", new() { ["windows"] = windowsEndpoint });
            var windows = await Start("windows", new() { ["linux"] = linuxEndpoint });
            var controller = await Start("controller", new() { ["linux"] = linuxEndpoint, ["windows"] = windowsEndpoint });
            using var response = await http.PostAsync(controller + "/test/deploy", null);
            response.EnsureSuccessStatusCode();
            var deployment = (await response.Content.ReadFromJsonAsync<ConfigurationDeployment>(Json))!;
            var deploymentFile = Path.Combine(root, "controller", "distribution", $"deployment-{deployment.Id}.json");
            for (var i = 0; i < 100; i++)
            {
                deployment = JsonSerializer.Deserialize<ConfigurationDeployment>(await File.ReadAllTextAsync(deploymentFile), Json)!;
                if (deployment.State == "Completed") break;
                await Task.Delay(200);
            }
            Check(deployment.State == "Completed" && deployment.NextIndex == 2 && deployment.Targets.Values.All(x => x.State == "Ack"), "two-target deployment completed");
            foreach (var (id, address) in new[] { ("linux", linux), ("windows", windows) })
            {
                var state = await State(address);
                Check(state.GetProperty("marker").GetString() == "mixed-os" && state.GetProperty("localIdentity").GetString() == id, "applied candidate and local overlay " + id);
                Check(state.GetProperty("versions").EnumerateArray().Count(x => x.GetProperty("action").GetString() == "distribution:" + deployment.Id) == 1, "exactly one application " + id);
                await File.WriteAllTextAsync(Path.Combine(root, id, "config-evidence.json"), state.ToString());
            }
            Record($"PASS signed v2 prepare/commit: deployment={deployment.Id} Completed NextIndex=2, both Ack, one application each, local overlays preserved");
            await Increment(windows, 9);
            await Until(async () => Value(await State(linux)) == 9, "Windows -> Linux replication");
            await Increment(linux, 9);
            await Until(async () => Value(await State(linux)) == 18 && Value(await State(windows)) == 18, "Linux -> Windows replication");
            Record("PASS bidirectional signed runtime.merge: Windows +9, Linux +9, both=18; DurableSyncEnabled=false");

            var now = DateTimeOffset.UtcNow;
            var mutation = new RuntimeStateMutation("process.interop.nonce", "shared|nonce", "windows", 1, now, now.AddHours(1),
                false, null, new Dictionary<string, long> { ["windows"] = 40 });
            var message = new NodeMessage(2, Path.GetFileName(root), "windows", "linux", Guid.NewGuid().ToString("D"), "nonce-test",
                "runtime.merge", now, now.AddMinutes(10), JsonSerializer.SerializeToElement(new[] { mutation }, Json));
            var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            HttpRequestMessage Request() => new(HttpMethod.Post, linux + NodeMessageAuthentication.Prefix + "runtime.merge")
                { Content = new ByteArrayContent(body) };
            using var original = Request();
            original.Headers.Host = $"127.0.0.1:{linuxProxy.Port}";
            original.Content!.Headers.ContentType = new("application/json");
            NodeMessageAuthentication.Sign(original, message, "test", Secret, body);
            var headers = original.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray());
            using (var accepted = await http.SendAsync(original)) accepted.EnsureSuccessStatusCode();
            async Task Replay(string stage)
            {
                using var request = Request();
                request.Content!.Headers.ContentType = new("application/json");
                foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                using var rejected = await http.SendAsync(request);
                var error = await rejected.Content.ReadAsStringAsync();
                Check(rejected.StatusCode == HttpStatusCode.Unauthorized && error.Contains("node_replay"), "nonce rejected " + stage + ": " + error);
                Check(Value(await State(linux), "process.interop.nonce") == 40, "replay does not increase counter");
                Record($"PASS nonce {stage}: status=401 body={error} counter=40");
            }
            await Replay("before-kill");
            await Until(async () => Value(await State(windows), "process.interop.nonce") == 40, "nonce counter replication");
            linuxProxy.Block(); windowsProxy.Block();
            await Increment(linux, 4);
            await Increment(windows, 3);
            await Until(async () =>
            {
                foreach (var file in Directory.GetFiles(Path.Combine(root, "linux", "runtime"), "*.jsonl"))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    foreach (var line in (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try { var value = JsonSerializer.Deserialize<RuntimeStateMutation>(line, Json);
                            if (value?.Domain == "process.interop" && value.CounterComponents?.Values.Sum() == 22) return true; }
                        catch (JsonException) { }
                    }
                }
                return false;
            }, "Linux journal count=22 visible before SIGKILL", 20);
            await Until(() => Task.FromResult(linuxProxy.Rejected + windowsProxy.Rejected > 0), "actual blocked TCP attempt", 10);
            Check(Value(await State(linux)) == 22 && Value(await State(windows)) == 21, "isolated divergent values");
            await File.WriteAllTextAsync(Path.Combine(root, "before-kill.json"), JsonSerializer.Serialize(new
                { linux = await State(linux), windows = await State(windows), linuxPid = hostPids["linux"], rejected = linuxProxy.Rejected + windowsProxy.Rejected }, Json));
            var owned = children.Single(x => x.Id == "linux");
            Check(!owned.Process.HasExited && ownerTokens["linux"].Length == 32 && hostPids["linux"] > 0, "live owned launcher and host token before kill");
            Record($"KILL ownership verified: ownedLauncher={owned.Process.Id}, LinuxPid={hostPids["linux"]}; private stdin token instructs Linux process to SIGKILL only itself");
            await owned.Process.StandardInput.WriteLineAsync("kill:" + ownerTokens["linux"]);
            await owned.Process.StandardInput.FlushAsync();
            await owned.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(owned.Output, owned.Error);
            // Direct wsl --exec returns signal 9 here; a shell wrapper may encode it as 128+9.
            Check(owned.Process.ExitCode is 9 or 137, "Linux SIGKILL launcher exit; actual=" + owned.Process.ExitCode);
            var killedLog = await File.ReadAllTextAsync(Path.Combine(root, "linux", "1.stdout.log"));
            Check(killedLog.Contains($"OWNED SELF SIGKILL pid={hostPids["linux"]} root={WslPath(Path.Combine(root, "linux"))}")
                && !killedLog.Contains("Application is shutting down"), "self-kill marker without graceful shutdown");
            Record($"PASS Linux force-killed launcherExit={owned.Process.ExitCode}; journal-visible state=22; both replication proxies still blocked");
            linux = await Start("linux", new() { ["windows"] = windowsEndpoint });
            var recovered = await State(linux);
            Check(Value(recovered) == 22 && Value(recovered, "process.interop.nonce") == 40, "independent disk recovery with peers blocked");
            Check(recovered.GetProperty("marker").GetString() == "mixed-os" && recovered.GetProperty("localIdentity").GetString() == "linux", "config retained after kill");
            await Replay("after-SIGKILL-restart-while-isolated");
            await File.WriteAllTextAsync(Path.Combine(root, "isolated-recovery.json"), recovered.ToString());
            Record("PASS independent Linux restart: count=22 nonceCounter=40 config retained; no peer recovery possible");
            await Increment(linux, 6);
            Check(Value(await State(linux)) == 28 && Value(await State(windows)) == 21, "independent post-restart increment");
            linuxProxy.Unblock(); windowsProxy.Unblock();
            await Until(async () => Value(await State(linux)) == 31 && Value(await State(windows)) == 31, "reconverge after kill without further writes");
            Record("PASS restored network: both=31 (18+Linux4+Windows3+Linux6); no new writes to trigger recovery");
            await Task.Delay(TimeSpan.FromSeconds(32));
            var linuxState = await State(linux);
            var windowsState = await State(windows);
            Check(Value(linuxState) == 31 && Value(windowsState) == 31 && Value(linuxState, "process.interop.nonce") == 40
                && Value(windowsState, "process.interop.nonce") == 40, "stable after repeated anti-entropy");
            await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { deployment, linux = linuxState, windows = windowsState }, Json));
            Record("PASS repeated merge anti-entropy stable: both=31 nonce=40; runtime.sync NOT tested");
        }
        finally
        {
            foreach (var child in children.AsEnumerable().Reverse())
            {
                if (!child.Process.HasExited)
                {
                    try { await child.Process.StandardInput.WriteLineAsync("stop"); child.Process.StandardInput.Close(); }
                    catch (IOException) { }
                    // Never kill Linux PIDs obtained from a file. The host self-expires if stdin breaks.
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(330));
                }
                await Task.WhenAll(child.Output, child.Error);
                Record($"EXIT id={child.Id} ownedLauncherPid={child.Process.Id} exit={child.Process.ExitCode}");
                child.Process.Dispose();
            }
            Record("END all owned launchers exited; no wsl shutdown; evidence retained at " + root);
        }
    }

    sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL " + message); }
}
