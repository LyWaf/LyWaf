using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Shared;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

static partial class CapacityProcessHarness
{
    const string Secret = "isolated-capacity-test-secret-at-least-32-bytes";
    const string Original = "SpeedLimit:\n  Value: old\n";
    const long MiB = 1024 * 1024;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    sealed class Fixed<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    static RuntimeStateOptions Runtime(string root, string id) => new()
    {
        Enabled = true, NodeId = id, StateDirectory = Path.Combine(root, "runtime"), MaxStateEntries = 8,
        MaxDiskBytes = 8 * MiB, SharedSecret = Secret,
        Peers = new() { [id == "edge" ? "control" : "edge"] = new()
        {
            Endpoint = "http://localhost", ProtocolVersion = 2,
            DurableSyncEnabled = id == "control" || Path.GetFileName(root) == "sync",
            Send = new() { AllowedDomains = ["capacity"], AllowUngroupedKeys = true },
            Receive = new() { AllowedDomains = ["capacity"], AllowUngroupedKeys = true }
        } }
    };

    public static async Task Host(string root, string? ownerToken = null)
    {
        SharedData.ConfigFilePath = Path.Combine(root, "config.yaml");
        if (!File.Exists(SharedData.ConfigFilePath)) await File.WriteAllTextAsync(SharedData.ConfigFilePath, Original);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, Args = [], EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddYamlFile(SharedData.ConfigFilePath, false, false);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var runtime = Runtime(root, "edge");
        if (Path.GetFileName(root).StartsWith("snapshot", StringComparison.Ordinal))
        {
            runtime.MaxStateEntries = 100_000;
            runtime.MaxDiskBytes = 512 * MiB;
            runtime.Peers["control"].DurableSyncEnabled = true;
            runtime.Peers["control"].AllowBootstrapExport = true;
        }
        var communication = new NodeCommunicationOptions
        {
            Enabled = true, AllowLegacyProtocol = true, RequireHttps = false, ClusterId = "capacity-test", NodeId = "edge",
            StateDirectory = Path.Combine(root, "protocol"), Peers = new() { ["control"] = new()
            {
                Role = "Controller", ActiveKeyId = "test", Keys = new() { ["test"] = Secret },
                AllowedOperations = ["runtime.merge", "runtime.sync", "configuration.prepare", "configuration.commit", "configuration.rollback", "configuration.result"]
            } }
        };
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new Fixed<RuntimeStateOptions>(runtime));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new Fixed<NodeCommunicationOptions>(communication));
        builder.Services.AddSingleton<IOptionsMonitor<DistributionOptions>>(new Fixed<DistributionOptions>(new() { NodeId = "edge", StateDirectory = Path.Combine(root, "distribution") }));
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<NodeCommandInbox>();
        builder.Services.AddSingleton<RuntimeStateAuthentication>();
        builder.Services.AddSingleton<RuntimeStateStore>();
        builder.Services.AddSingleton<IRuntimeStateStore>(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddSingleton<RuntimeStateReplicator>();
        builder.Services.AddSingleton<RuntimeStateSyncReceiver>();
        builder.Services.AddSingleton<ConfigVersionService>();
        builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        builder.Services.AddSingleton<ConfigurationLifecycleService>();
        builder.Services.AddSingleton<DistributionNodeService>();
        await using var app = builder.Build();
        // The fixed Host header only selects the isolated test route, not a bound port.
        app.MapNodeCommunication(18937);
        app.MapLyWafRuntimeStateApi(18937);
        var store = app.Services.GetRequiredService<RuntimeStateStore>();
        ConfigureSnapshotHost(app, runtime, store, root);
        ConfigureIndexGapHost(app, root);
        app.MapPost("/test/fill-memory", () =>
        {
            for (var i = 0; i < 8; i++) store.Set("capacity", "local-" + i, i);
            return Results.Ok();
        });
        app.MapPost("/test/hold-flush", () =>
        {
            typeof(RuntimeStateStore).GetProperty("JournalBeforeDurableFlush", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(store, (Action)(() =>
                {
                    File.WriteAllText(Path.Combine(root, "flush-held"), Environment.ProcessId.ToString());
                    using var hold = new ManualResetEventSlim();
                    if (!hold.Wait(TimeSpan.FromMinutes(2))) throw new TimeoutException("Owner did not kill child");
                }));
            return Results.Ok();
        });
        app.MapPost("/test/hold-rollback/{stage}", (string stage, DistributionNodeService node) =>
        {
            typeof(DistributionNodeService).GetProperty("RollbackFaultPoint", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(node, (Action<string>)(point =>
                {
                    if (point != stage) return;
                    File.WriteAllText(Path.Combine(root, "rollback-held"), point + ":" + Environment.ProcessId);
                    using var hold = new ManualResetEventSlim();
                    if (!hold.Wait(TimeSpan.FromMinutes(2))) throw new TimeoutException("Owner did not kill rollback child");
                }));
            return Results.Ok();
        });
        app.MapPost("/test/inbox", async (NodeMessage message, NodeCommandInbox inbox) =>
        {
            try
            {
                var result = await inbox.ExecuteAsync(message, () =>
                {
                    File.AppendAllText(Path.Combine(root, "effects"), "effect\n");
                    return Task.FromResult(new NodeAcknowledgement(true, "edge", "fixture", new string('x', (int)MiB)));
                }, default);
                return Results.Json(result, Json);
            }
            catch (IOException) { return Results.StatusCode(503); }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await File.WriteAllTextAsync(Path.Combine(root, "ready.json.tmp"), JsonSerializer.Serialize(new
        { address, pid = Environment.ProcessId, ownerToken, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription }, Json));
        File.Move(Path.Combine(root, "ready.json.tmp"), Path.Combine(root, "ready.json"), true);
        if (ownerToken is null) await app.WaitForShutdownAsync();
        else
        {
            var input = Task.Run(Console.ReadLine);
            await Task.WhenAny(input, app.WaitForShutdownAsync(), Task.Delay(TimeSpan.FromMinutes(10)));
            if (input.IsCompletedSuccessfully && input.Result == "kill:" + ownerToken)
            {
                Console.WriteLine("OWNED SELF KILL " + Environment.ProcessId);
                Console.Out.Flush();
                Process.GetCurrentProcess().Kill();
                await Task.Delay(Timeout.Infinite);
            }
            await app.StopAsync();
        }
    }

    public static async Task Run(string? linuxExecutable = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-capacity-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        void Record(string value) { Console.WriteLine(value); log.WriteLine($"{DateTimeOffset.UtcNow:O} {value}"); }
        Record("ROOT " + root);
        Record("LyWaf.dll SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RuntimeStateStore).Assembly.Location))));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Host = "localhost:18937";
        async Task<JsonElement> Send(Child child, NodeMessage message, int status)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            using var request = new HttpRequestMessage(HttpMethod.Post, child.Address + NodeMessageAuthentication.Prefix + message.Type) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            NodeMessageAuthentication.Sign(request, message, "test", Secret, bytes);
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Record($"HTTP {message.Type} {(int)response.StatusCode} {body}");
            Check((int)response.StatusCode == status, message.Type + " status expected " + status);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        var scenarios = new[] { "prepared", "history", "rollback", "inbox", "legacy", "sync",
            "IntentPersisted", "ConfigurationWritten", "ConfigurationReloaded", "StatePersisted" }
            .Where(s => linuxExecutable is null || s != "inbox").ToArray();
        foreach (var scenario in scenarios)
        {
            var directory = Path.Combine(root, scenario); Directory.CreateDirectory(directory);
            Child? child = null;
            try
            {
                child = await Start(directory, Record, linuxExecutable);
                if (scenario is "IntentPersisted" or "ConfigurationWritten" or "ConfigurationReloaded" or "StatePersisted")
                {
                    var content = "SpeedLimit:\n  Value: new\n";
                    await Send(child, Message("configuration.prepare", new PrepareConfigurationRequest("deployment", "v",
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), content)), 200);
                    await Send(child, Message("configuration.commit", new DistributionCommandRequest("deployment")), 200);
                    using (var armed = await http.PostAsync(child.Address + "/test/hold-rollback/" + scenario, null)) armed.EnsureSuccessStatusCode();
                    var command = Message("configuration.rollback", new DistributionCommandRequest("deployment"));
                    var rollingBack = Send(child, command, 200);
                    await Until(() => File.Exists(Path.Combine(directory, "rollback-held")));
                    Check(!rollingBack.IsCompleted, "rollback response held at actual persistence boundary");
                    child.Kill();
                    try { await rollingBack; throw new InvalidOperationException("Killed rollback unexpectedly ACKed"); }
                    catch (HttpRequestException) { }
                    await child.DisposeAsync(); child = await Start(directory, Record, linuxExecutable);
                    var history = Path.Combine(directory, ".lywaf-config-history");
                    var archives = Directory.GetFiles(history, "*.config").Order().ToArray();
                    var pins = File.ReadAllText(Path.Combine(history, "retention-pins.json"));
                    var query = Message("configuration.result", command);
                    var result = await Send(child, query, scenario == "IntentPersisted" ? 409 : 200);
                    if (scenario == "IntentPersisted") Check(result.GetProperty("errorCode").GetString() == "node_outcome_unknown", "intent without effect remains Unknown");
                    else
                    {
                        Check(result.GetProperty("ack").GetBoolean() && result.GetProperty("stage").GetString() == "rollback", "query proves completed rollback");
                        Check(File.ReadAllText(Path.Combine(directory, "config.yaml")) == Original, "rollback restored baseline");
                    }
                    await Send(child, Message("configuration.result", command), scenario == "IntentPersisted" ? 409 : 200);
                    Check(archives.SequenceEqual(Directory.GetFiles(history, "*.config").Order()), "queries never create another rollback version");
                    Check(pins == File.ReadAllText(Path.Combine(history, "retention-pins.json")), "compensation pins unchanged by queries");
                }
                else if (scenario is "prepared" or "history" or "rollback")
                {
                    var content = "SpeedLimit:\n  Value: new\n";
                    var prepare = Message("configuration.prepare", new PrepareConfigurationRequest("deployment", "v", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), content));
                    var prepared = Path.Combine(directory, "distribution", "prepared");
                    var history = Path.Combine(directory, ".lywaf-config-history");
                    if (scenario == "prepared") Fill(prepared, 64 * MiB);
                    var preparedResult = await Send(child, prepare, scenario == "prepared" ? 409 : 200);
                    if (scenario == "prepared") Check(!preparedResult.GetProperty("ack").GetBoolean(), "full prepared rejected");
                    NodeMessage command = prepare;
                    string? pins = null;
                    if (scenario != "prepared")
                    {
                        if (scenario == "history") Fill(history, 256 * MiB);
                        command = Message("configuration.commit", new DistributionCommandRequest("deployment"));
                        await Send(child, command, scenario == "history" ? 409 : 200);
                        if (scenario == "rollback")
                        {
                            pins = File.ReadAllText(Path.Combine(history, "retention-pins.json"));
                            Fill(prepared, 64 * MiB - Bytes(prepared));
                            command = Message("configuration.rollback", new DistributionCommandRequest("deployment"));
                            await Send(child, command, 409);
                        }
                    }
                    var config = File.ReadAllText(Path.Combine(directory, "config.yaml"));
                    if (scenario != "rollback") Check(config == Original, "capacity prevented configuration effect");
                    var before = Evidence(directory);
                    child.Kill(); await child.DisposeAsync(); child = await Start(directory, Record, linuxExecutable);
                    var query = Message("configuration.result", command);
                    var result = await Send(child, query, 409);
                    if (scenario == "history") Check(result.GetProperty("errorCode").GetString() == "node_outcome_unknown", "uncertain commit stays Unknown");
                    Check(File.ReadAllText(Path.Combine(directory, "config.yaml")) == config, "query does not repeat effect");
                    Check(Evidence(directory) == before, "query preserves candidate/pin/archive evidence");
                    if (pins is not null) Check(File.ReadAllText(Path.Combine(history, "retention-pins.json")) == pins, "compensation pin survives full prepared restart");
                    Check(Bytes(prepared) <= 64 * MiB && Bytes(history) <= 256 * MiB, "directory hard limits");
                }
                else if (scenario == "inbox")
                {
                    var message = Message("configuration.commit", new { deploymentId = "fixture" });
                    using (var response = await http.PostAsJsonAsync(child.Address + "/test/inbox", message, Json))
                        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable, "oversize result fails after effect");
                    child.Kill(); await child.DisposeAsync(); child = await Start(directory, Record, linuxExecutable);
                    using var repeated = await http.PostAsJsonAsync(child.Address + "/test/inbox", message, Json);
                    var result = await repeated.Content.ReadFromJsonAsync<JsonElement>();
                    Check(result.GetProperty("error").GetProperty("code").GetString() == "node_outcome_unknown", "inbox remains Unknown after kill");
                    Check(File.ReadAllLines(Path.Combine(directory, "effects")).Length == 1, "inbox effect exactly once");
                }
                else if (scenario == "legacy")
                {
                    using var fill = await http.PostAsync(child.Address + "/test/fill-memory", null); fill.EnsureSuccessStatusCode();
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(Mutation(1), Json);
                    using var request = new HttpRequestMessage(HttpMethod.Post, child.Address + "/api/runtime-state/agent/merge") { Content = new ByteArrayContent(bytes) };
                    RuntimeStateAuthentication.Sign(request, "control", Secret, bytes);
                    using var response = await http.SendAsync(request);
                    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                    Check(response.StatusCode == HttpStatusCode.ServiceUnavailable, "legacy capacity returns 503");
                    Check(body.GetProperty("errorCode").GetString() == "node_runtime_store_unavailable", "legacy stable capacity error");
                    Check(body.GetProperty("messageKey").GetString() == "node.errors.node_runtime_store_unavailable", "legacy localized capacity error");
                    Check(body.GetProperty("traceId").GetString()!.Length > 0, "legacy trace ID");
                }
                else
                {
                    var sender = new RuntimeStateSyncSender(new Fixed<RuntimeStateOptions>(Runtime(Path.Combine(directory, "sender"), "control")));
                    var batch = sender.Prepare("capacity-test", "control", "edge", [Mutation(1)]);
                    using (var held = await http.PostAsync(child.Address + "/test/hold-flush", null)) held.EnsureSuccessStatusCode();
                    var sending = Send(child, Message("runtime.sync", batch), 200);
                    await Until(() => File.Exists(Path.Combine(directory, "flush-held")));
                    Check(!sending.IsCompleted, "no ACK before fsync");
                    child.Kill();
                    try { await sending; throw new InvalidOperationException("Killed unflushed request unexpectedly ACKed"); }
                    catch (HttpRequestException) { }
                    await child.DisposeAsync(); child = await Start(directory, Record, linuxExecutable);
                    Check(sender.ReadPending("capacity-test", "control", "edge")?.Digest == batch.Digest, "pending survives unflushed kill");
                    var ack = (await Send(child, Message("runtime.sync", batch), 200)).Deserialize<RuntimeStateSyncAck>(Json)!;
                    Check(ack.Status == "Applied", "restarted receiver durably applies pending batch");
                    sender.Confirm(batch, ack);
                    batch = sender.Prepare("capacity-test", "control", "edge", [Mutation(2)]);
                    var runtimeDirectory = Path.Combine(directory, "runtime");
                    Fill(runtimeDirectory, 8 * MiB - Bytes(runtimeDirectory));
                    var denied = await Send(child, Message("runtime.sync", batch), 503);
                    Check(denied.GetProperty("errorCode").GetString() == "node_runtime_store_unavailable", "root quota fails clearly");
                    Check(sender.ReadPending("capacity-test", "control", "edge")?.Digest == batch.Digest, "full root preserves pending");
                    Check(Bytes(runtimeDirectory) <= 8 * MiB, "runtime root bounded");
                    // Only the fixture-owned filler is removed; no fence or authoritative record is touched.
                    File.Delete(Path.Combine(runtimeDirectory, "capacity-filler.tmp"));
                    child.Kill(); await child.DisposeAsync(); child = await Start(directory, Record, linuxExecutable);
                    ack = (await Send(child, Message("runtime.sync", batch), 200)).Deserialize<RuntimeStateSyncAck>(Json)!;
                    sender.Confirm(batch, ack);
                    Check(sender.ReadPending("capacity-test", "control", "edge") is null, "pending clears only after durable replay ACK");
                }
                Record("PASS " + scenario);
            }
            finally { if (child is not null) await child.DisposeAsync(); }
        }
        Record($"PASS all {scenarios.Length} capacity/crash process scenarios; linux={linuxExecutable is not null}");
    }

    static NodeMessage Message(string operation, object payload) => new(2, "capacity-test", "control", "edge",
        Guid.NewGuid().ToString("D"), "deployment", operation, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), JsonSerializer.SerializeToElement(payload, Json));
    static RuntimeStateMutation Mutation(long sequence) => new("capacity", "key", "control", sequence, DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(sequence));
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static long Bytes(string path) => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) : 0;
    static void Fill(string path, long bytes) { Directory.CreateDirectory(path); using var file = File.Create(Path.Combine(path, "capacity-filler.tmp")); file.SetLength(bytes); }
    static string Evidence(string root) => string.Join("\n", new[] { Path.Combine(root, "distribution"), Path.Combine(root, ".lywaf-config-history") }
        .Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
    static async Task Until(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Child did not reach expected checkpoint"); await Task.Delay(50); }
    }
    static async Task<Child> Start(string root, Action<string> record, string? linuxExecutable = null)
    {
        File.Delete(Path.Combine(root, "ready.json"));
        var token = Guid.NewGuid().ToString("N");
        static string WslPath(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
        var start = new ProcessStartInfo(linuxExecutable is null ? "dotnet" : "wsl.exe") { WorkingDirectory = root,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var arguments = linuxExecutable is null ? new[] { Assembly.GetExecutingAssembly().Location, "capacity-host", root, token }
            : new[] { "-d", "Debian", "--exec", WslPath(Path.GetFullPath(linuxExecutable)), "capacity-host", WslPath(root), token };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var child = new Child(Process.Start(start)!, root, linuxExecutable is not null, token, record);
        record($"START {root} pid={child.Process.Id}");
        try
        {
            JsonElement ready = default;
            await Until(() =>
            {
                if (child.Process.HasExited) return true;
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "ready.json")));
                    if (document.RootElement.GetProperty("ownerToken").GetString() != token) return false;
                    ready = document.RootElement.Clone();
                    return true;
                }
                catch (Exception ex) when (ex is IOException or JsonException) { return false; }
            });
            Check(!child.Process.HasExited, "child startup failed; see logs: " + root);
            Check(ready.GetProperty("ownerToken").GetString() == token, "ready belongs to owned launch");
            Check(linuxExecutable is null ? ready.GetProperty("pid").GetInt32() == child.Process.Id
                : ready.GetProperty("os").GetString()!.Contains("Debian"), "expected process platform");
            record("READY " + ready);
            child.Address = ready.GetProperty("address").GetString()!;
            return child;
        }
        catch (Exception error) { record("START FAILED " + error); await child.DisposeAsync(); throw; }
    }
    sealed class Child : IAsyncDisposable
    {
        public Process Process { get; }
        public string Address = "";
        readonly Task stdout, stderr;
        readonly bool linux;
        readonly string token;
        readonly Action<string> record;
        bool killRequested;
        bool disposed;
        public Child(Process process, string root, bool linux, string token, Action<string> record)
        {
            Process = process;
            this.linux = linux; this.token = token; this.record = record;
            stdout = Save(process.StandardOutput, Path.Combine(root, process.Id + ".stdout.log"));
            stderr = Save(process.StandardError, Path.Combine(root, process.Id + ".stderr.log"));
        }
        static async Task Save(StreamReader input, string path) { await using var file = File.CreateText(path); while (await input.ReadLineAsync() is { } line) { await file.WriteLineAsync(line); await file.FlushAsync(); } }
        public void Kill()
        {
            if (disposed || Process.HasExited || killRequested) return;
            killRequested = true;
            record($"KILL ownedLauncher={Process.Id} linux={linux}");
            if (linux) { Process.StandardInput.WriteLine("kill:" + token); Process.StandardInput.Flush(); }
            else Process.Kill(entireProcessTree: true);
        }
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            Kill(); await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); await Task.WhenAll(stdout, stderr);
            record($"EXIT ownedLauncher={Process.Id} code={Process.ExitCode}"); Process.Dispose(); disposed = true;
        }
    }
}
