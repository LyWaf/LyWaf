using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

static class BootstrapProcessHarness
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const string Secret = "isolated-bootstrap-process-secret-never-production-123456";
    const string Domain = "process.bootstrap";
    public sealed record Settings(string Id, string Cluster, string Endpoint, int IncomingPort,
        bool AutoReplicate, bool OldClock, bool HoldBeforePublish, string OwnerToken);
    public sealed record Confirmation(RuntimeStateSyncBatch Batch, RuntimeStateSyncAck Ack);
    sealed class Fixed<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
    static object? Invoke(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    static IReadOnlyList<RuntimeStateMutation> Export(RuntimeStateStore store) =>
        (IReadOnlyList<RuntimeStateMutation>)Invoke(store, "ExportMutations")!;

    public static async Task Host(string root)
    {
        var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(root, "settings.json")), Json)!;
        var source = settings.Id == "source";
        var peer = source ? "target" : "source";
        var grant = new RuntimeStateReplicationPolicy { AllowedDomains = [Domain], AllowedProtectionGroups = ["bootstrap"],
            TrustedForwardedSources = ["source", "target"] };
        var runtime = new RuntimeStateOptions { Enabled = true, NodeId = settings.Id, StateDirectory = Path.Combine(root, "runtime"),
            ProtectionGroupKeyPrefixes = new() { ["bootstrap"] = ["scope|"] }, Peers = new() { [peer] = new()
            { Endpoint = settings.Endpoint, ProtocolVersion = 2, DurableSyncEnabled = true,
                BootstrapAuthority = source, AllowBootstrapExport = !source, Send = grant, Receive = grant } } };
        var protocol = new NodeCommunicationOptions { Enabled = true, ClusterId = settings.Cluster, NodeId = settings.Id,
            RequireHttps = false, AllowLegacyProtocol = false, StateDirectory = Path.Combine(root, "protocol"),
            Peers = new() { [peer] = new() { ActiveKeyId = "test", Keys = new() { ["test"] = Secret },
                AllowedOperations = [RuntimeStateSyncProtocol.Operation] } } };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new Fixed<RuntimeStateOptions>(runtime));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new Fixed<NodeCommunicationOptions>(protocol));
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<RuntimeStateStore>();
        builder.Services.AddSingleton<IRuntimeStateStore>(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddSingleton<RuntimeStateSyncReceiver>();
        builder.Services.AddSingleton<RuntimeStateSyncSender>();
        builder.Services.AddSingleton<RuntimeStateReplicator>();
        await using var app = builder.Build();
        var store = app.Services.GetRequiredService<RuntimeStateStore>();
        var sender = app.Services.GetRequiredService<RuntimeStateSyncSender>();
        var receiver = app.Services.GetRequiredService<RuntimeStateSyncReceiver>();
        var replicator = app.Services.GetRequiredService<RuntimeStateReplicator>();
        var started = false;
        if (settings.OldClock)
            typeof(RuntimeStateSyncReceiver).GetProperty("UtcNow", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(receiver, (Func<DateTimeOffset>)(() => DateTimeOffset.UtcNow.AddHours(-26)));
        if (settings.HoldBeforePublish)
            typeof(RuntimeStateStore).GetProperty("BootstrapBeforePublish", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(store, (Action)(() =>
                {
                    // Existing production test hook runs only after the snapshot journal fsync fence.
                    File.WriteAllText(Path.Combine(root, "fault.json"), JsonSerializer.Serialize(new
                    { stage = "snapshot-fsynced-before-publication", pid = Environment.ProcessId, ownerToken = settings.OwnerToken,
                        staleVisible = store.TryGet<string>(Domain, "scope|deleted", out _),
                        liveVisible = store.TryGet<string>(Domain, "scope|live", out _) }, Json));
                    using var hold = new ManualResetEventSlim();
                    if (!hold.Wait(TimeSpan.FromMinutes(3))) throw new TimeoutException("Harness kill did not arrive");
                }));
        object State() => new
        {
            pid = Environment.ProcessId, ownerToken = settings.OwnerToken,
            stale = store.TryGet<string>(Domain, "scope|deleted", out var stale) ? stale : null,
            live = store.TryGet<string>(Domain, "scope|live", out var live) ? live : null,
            fresh = store.TryGet<string>(Domain, "scope|fresh", out var fresh) ? fresh : null,
            keep = store.TryGet<string>("private", "keep", out var keep) ? keep : null,
            pending = source ? sender.ReadPending(settings.Cluster, "source", "target") : null,
            mutations = Export(store), status = store.GetStatus()
        };
        async Task StartReplication()
        {
            if (started) return;
            started = true;
            await replicator.StartAsync(default);
        }
        app.MapGet("/test/state", () => Results.Json(State()));
        app.MapPost("/test/seed", () =>
        {
            store.Set(Domain, "scope|deleted", "stale");
            store.Set("private", "keep", "local");
            return Results.Json(State());
        });
        app.MapPost("/test/prepare", () => Results.Json(sender.Prepare(settings.Cluster, "source", "target",
            [Export(store).Single(x => x.Domain == Domain && x.Key == "scope|deleted")])));
        app.MapPost("/test/confirm", (Confirmation input) => { sender.Confirm(input.Batch, input.Ack); return Results.Ok(); });
        app.MapPost("/test/retire", (RuntimeStateMutation stale, HttpContext context) =>
        {
            store.Merge(stale with { Sequence = stale.Sequence + 1, Deleted = true, Value = null, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            store.Set(Domain, "scope|live", "authoritative");
            if (context.Request.Query.ContainsKey("pages"))
                for (var i = 0; i < 160; i++) store.Set(Domain, "scope|large-" + i, new string('x', 10000));
            return Results.Ok();
        });
        app.MapPost("/test/start", async () => { await StartReplication(); return Results.Ok(); });
        app.MapPost("/test/fresh", () => { store.Set(Domain, "scope|fresh", "new-epoch"); return Results.Ok(); });
        app.MapPost("/test/compact-stop", async () =>
        {
            if (started) { await replicator.StopAsync(default); started = false; }
            await store.StopAsync(default);
            var compact = (Task<bool>)Invoke(store, "CompactJournalAsync", Path.Combine(runtime.StateDirectory, "runtime-state.jsonl"), CancellationToken.None)!;
            Check(await compact, "actual journal compaction");
            return Results.Ok();
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        app.MapNodeCommunication(settings.IncomingPort);
        await File.WriteAllTextAsync(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(new
        { address, pid = Environment.ProcessId, ownerToken = settings.OwnerToken, os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription }, Json));
        if (settings.AutoReplicate) await StartReplication();
        var input = Task.Run(Console.ReadLine);
        await Task.WhenAny(input, Task.Delay(TimeSpan.FromMinutes(5)), app.WaitForShutdownAsync());
        if (input.IsCompletedSuccessfully && input.Result == "kill:" + settings.OwnerToken)
        {
            Console.WriteLine($"OWNED SELF KILL pid={Environment.ProcessId} root={root}");
            Console.Out.Flush();
            using var self = Process.GetCurrentProcess();
            self.Kill();
            await Task.Delay(Timeout.Infinite);
        }
        if (started) await replicator.StopAsync(default);
        await app.StopAsync();
    }

    public static async Task Run(string linuxExecutable, bool windowsOnly)
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-bootstrap-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        void Record(string value) { var line = $"{DateTimeOffset.UtcNow:O} {value}"; Console.WriteLine(line); log.WriteLine(line); }
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        Record("BEGIN actual runtime.sync bootstrap; source snapshot fsync hook / held remote new-epoch commit ACK; root=" + root);
        foreach (var linuxSource in windowsOnly ? new[] { false } : new[] { false, true })
        foreach (var fault in new[] { "before-publish", "commit-response", "page-response" })
        {
            var scenario = (linuxSource ? "mixed" : "windows") + "-" + fault;
            var directory = Path.Combine(root, scenario);
            Directory.CreateDirectory(directory);
            var children = new List<Owned>();
            await using var proxy = await SyncProxy.Start(directory);
            var cluster = "bootstrap-" + Guid.NewGuid().ToString("N");
            string WslPath(string path) => "/mnt/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
            async Task<Owned> Start(string id, bool auto = false, bool oldClock = false, bool hold = false)
            {
                var path = Path.Combine(directory, id);
                Directory.CreateDirectory(path);
                File.Delete(Path.Combine(path, "ready.json"));
                var token = Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(Path.Combine(path, "settings.json"), JsonSerializer.Serialize(new Settings(id, cluster,
                    $"http://127.0.0.1:{proxy.Port}", proxy.Port, auto, oldClock, hold, token), Json));
                var linux = linuxSource && id == "source";
                var info = new ProcessStartInfo(linux ? "wsl.exe" : "dotnet") { WorkingDirectory = path, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in linux ? new[] { "-d", "Debian", "--exec", WslPath(Path.GetFullPath(linuxExecutable)), "bootstrap-host", WslPath(path) }
                    : new[] { Assembly.GetExecutingAssembly().Location, "bootstrap-host", path }) info.ArgumentList.Add(arg);
                var child = new Owned(Process.Start(info)!, path, id, token, children.Count + 1, linux) { HoldBeforePublish = hold };
                children.Add(child);
                Record($"START {scenario} id={id} ownedLauncher={child.Process.Id} linux={linux}");
                await Until(() => Task.FromResult(File.Exists(Path.Combine(path, "ready.json"))), "ready " + id, 30);
                using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "ready.json")));
                var e = ready.RootElement;
                Check(!child.Process.HasExited && e.GetProperty("ownerToken").GetString() == token, "owned startup token");
                child.HostPid = e.GetProperty("pid").GetInt32();
                Check(linux ? e.GetProperty("os").GetString()!.Contains("Debian") : child.HostPid == child.Process.Id, "OS/PID ownership");
                child.Address = e.GetProperty("address").GetString()!;
                await File.WriteAllTextAsync(Path.Combine(path, $"{children.Count}.ready.json"), e.ToString());
                Record($"READY {scenario} id={id} {e}");
                return child;
            }
            async Task Stop(Owned child)
            {
                if (!child.Process.HasExited)
                {
                    await child.Process.StandardInput.WriteLineAsync("stop");
                    child.Process.StandardInput.Close();
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
                }
                await child.Logs;
                Record($"EXIT {scenario} id={child.Id} ownedLauncher={child.Process.Id} hostPid={child.HostPid} code={child.Process.ExitCode}");
            }
            async Task Kill(Owned child)
            {
                Check(children.Contains(child) && !child.Process.HasExited && child.Token.Length == 32, "owned live process before kill");
                Record($"KILL {scenario} ownedLauncher={child.Process.Id} hostPid={child.HostPid}; token verified; no PID lookup");
                if (child.Linux)
                {
                    await child.Process.StandardInput.WriteLineAsync("kill:" + child.Token);
                    await child.Process.StandardInput.FlushAsync();
                }
                else child.Process.Kill();
                await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await child.Logs;
                Check(child.Linux ? child.Process.ExitCode is 9 or 137 : child.Process.ExitCode != 0, "abrupt OS exit");
                Record($"KILLED {scenario} ownedLauncher={child.Process.Id} code={child.Process.ExitCode}");
            }
            async Task<JsonElement> State(Owned child) => await http.GetFromJsonAsync<JsonElement>(child.Address + "/test/state", Json);
            async Task Post(Owned child, string action)
            { using var response = await http.PostAsync(child.Address + "/test/" + action, null); response.EnsureSuccessStatusCode(); }
            async Task<RuntimeStateSyncBatch> Prepare(Owned child)
            {
                using var response = await http.PostAsync(child.Address + "/test/prepare", null);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<RuntimeStateSyncBatch>(Json))!;
            }
            async Task<RuntimeStateSyncAck> Exchange(RuntimeStateSyncBatch batch)
            {
                var now = DateTimeOffset.UtcNow;
                var envelope = new NodeMessage(2, cluster, "source", "target", Guid.NewGuid().ToString("D"), batch.Epoch,
                    RuntimeStateSyncProtocol.Operation, now, now.AddMinutes(5), JsonSerializer.SerializeToElement(batch, Json));
                var body = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{proxy.Port}" + NodeMessageAuthentication.Prefix + RuntimeStateSyncProtocol.Operation)
                    { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new("application/json");
                NodeMessageAuthentication.Sign(request, envelope, "test", Secret, body);
                using var response = await http.SendAsync(request);
                Check(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict, "signed sync HTTP response");
                return (await response.Content.ReadFromJsonAsync<RuntimeStateSyncAck>(Json))!;
            }
            try
            {
                var target = await Start("target", oldClock: true);
                proxy.Target = target.Address;
                var source = await Start("source", hold: fault == "before-publish");
                await Post(source, "seed");
                var initial = await Prepare(source);
                var ack = await Exchange(initial);
                Check(ack.Status == "Applied" && ack.Digest == initial.Digest, "real durable sync initial ACK");
                using (var confirmed = await http.PostAsJsonAsync(source.Address + "/test/confirm", new Confirmation(initial, ack))) confirmed.EnsureSuccessStatusCode();
                using (var retired = await http.PostAsJsonAsync(target.Address + "/test/retire" + (fault == "page-response" ? "?pages=1" : ""), initial.Mutations.Single())) retired.EnsureSuccessStatusCode();
                await Post(target, "compact-stop");
                await Stop(target);
                target = await Start("target");
                proxy.Target = target.Address;
                var targetState = await State(target);
                Check(!targetState.GetProperty("mutations").EnumerateArray().Any(x => x.GetProperty("key").GetString() == "scope|deleted"), "expired tombstone physically absent after compaction/restart");
                var pending = await Prepare(source);
                await File.WriteAllTextAsync(Path.Combine(directory, "old-batch.json"), JsonSerializer.Serialize(pending, Json));
                Check(pending.Epoch == initial.Epoch && pending.Sequence == 2, "durable old-epoch pending");
                if (fault == "commit-response") proxy.HoldCommit = true;
                if (fault == "page-response") proxy.HoldPage = 2;
                await Post(source, "start");
                if (fault == "before-publish")
                {
                    await Until(() => Task.FromResult(File.Exists(Path.Combine(source.Directory, "fault.json"))), "snapshot fsync pause", 35);
                    using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source.Directory, "fault.json")));
                    Check(marker.RootElement.GetProperty("ownerToken").GetString() == source.Token
                        && marker.RootElement.GetProperty("staleVisible").GetBoolean() && !marker.RootElement.GetProperty("liveVisible").GetBoolean(), "paused after fsync before memory replacement");
                    Check(!proxy.Events.Any(x => x.Batch.Bootstrap?.Phase == "commit"), "no new-epoch commit before publication");
                }
                else
                {
                    var held = await proxy.Held.Task.WaitAsync(TimeSpan.FromSeconds(35));
                    Check(fault == "page-response" ? held.Status == "SnapshotPage" && held.Page?.Index == 2
                        : held.Status == "Bootstrapped" && held.Epoch != pending.Epoch && held.Sequence == 0, "real remote page/commit response held");
                    await File.WriteAllTextAsync(Path.Combine(directory, "held-ack.json"), JsonSerializer.Serialize(held, Json));
                    Check((await State(source)).GetProperty("pending").ValueKind != JsonValueKind.Null, "sender not rebased before receiving ACK");
                }
                Check(proxy.Events.Any(x => x.Ack?.Status == "SnapshotRequired") && proxy.Events.Any(x => x.Ack?.Status == "SnapshotOffered"), "real sync bootstrap handshake");
                proxy.Blocked = true;
                await Kill(source);
                proxy.Release();
                source = await Start("source", auto: true);
                var recovered = await State(source);
                Check((fault == "page-response" ? recovered.GetProperty("stale").GetString() == "stale" && recovered.GetProperty("live").ValueKind == JsonValueKind.Null
                        : recovered.GetProperty("stale").ValueKind == JsonValueKind.Null && recovered.GetProperty("live").GetString() == "authoritative")
                    && recovered.GetProperty("keep").GetString() == "local" && recovered.GetProperty("pending").ValueKind != JsonValueKind.Null,
                    "snapshot disk recovery independent of peer, old pending preserved");
                await File.WriteAllTextAsync(Path.Combine(directory, "isolated-recovery.json"), recovered.ToString());
                Record($"PASS {scenario}: killed at {fault}; isolated restart, snapshot {(fault == "page-response" ? "not prematurely installed" : "recovered")}, private retained, pending still durable");
                proxy.Blocked = false;
                await Until(async () => (await State(source)).GetProperty("pending").ValueKind == JsonValueKind.Null, "background bootstrap rebase", 40);
                await Post(source, "fresh");
                await Until(async () => (await State(target)).GetProperty("fresh").GetString() == "new-epoch"
                    && (await State(source)).GetProperty("pending").ValueKind == JsonValueKind.Null, "new epoch delta confirmed", 40);
                var newEpoch = proxy.Events.Last(x => x.Batch.Bootstrap is null && x.Batch.Mutations.Any(m => m.Key == "scope|fresh") && x.Ack?.Status == "Applied").Batch.Epoch;
                Check(newEpoch != initial.Epoch, "epoch replaced after snapshot");
                if (fault == "page-response")
                {
                    var pages = proxy.Events.Where(x => x.Batch.Bootstrap?.Phase == "page").Select(x => x.Batch.Bootstrap!.PageIndex).ToArray();
                    Check(pages.Count(x => x == 0) == 1 && pages.Count(x => x == 1) == 1 && pages.Count(x => x == 2) == 2
                        && pages.Max() > 2, "durable pages 0/1 reused, interrupted page 2 retried, later pages downloaded");
                    Check((await State(source)).GetProperty("mutations").EnumerateArray().Count(x => x.GetProperty("key").GetString()!.StartsWith("scope|large-")) == 160,
                        "all multi-page entries installed");
                    Record($"PASS {scenario}: page0=1 page1=1 page2=2 maxPage={pages.Max()} entries=160");
                }
                var rejected = await Exchange(pending);
                Check(rejected.Status == "SnapshotRequired" && rejected.Epoch == newEpoch, "old epoch rejected over real signed sync HTTP");
                Check((await State(target)).GetProperty("stale").ValueKind == JsonValueKind.Null, "old payload did not resurrect deleted value");
                await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                { initialEpoch = initial.Epoch, newEpoch, rejected, source = await State(source), target = await State(target) }, Json));
                await Post(source, "compact-stop");
                await Stop(source);
                proxy.Blocked = true;
                source = await Start("source");
                var compacted = await State(source);
                Check(compacted.GetProperty("stale").ValueKind == JsonValueKind.Null && compacted.GetProperty("live").GetString() == "authoritative"
                    && compacted.GetProperty("fresh").GetString() == "new-epoch" && compacted.GetProperty("keep").GetString() == "local", "compaction/restart preserves bootstrap fence and state");
                await File.WriteAllTextAsync(Path.Combine(directory, "compacted-recovery.json"), compacted.ToString());
                Record($"PASS {scenario}: background rebase, fresh durable delta, old epoch SnapshotRequired, no stale resurrection, compaction/restart; old={initial.Epoch} new={newEpoch}");
            }
            finally
            {
                proxy.Release();
                foreach (var child in children.AsEnumerable().Reverse())
                {
                    if (!child.Process.HasExited && child.HoldBeforePublish && File.Exists(Path.Combine(child.Directory, "fault.json")))
                        await Kill(child);
                    await Stop(child);
                    child.Process.Dispose();
                }
                Record("CLEAN " + scenario + ": all owned children exited");
            }
        }
        Record("RESULT PASS runtime.sync bootstrap OS-kill coverage; root=" + root);
    }

    static async Task Until(Func<Task<bool>> predicate, string message, int seconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds) { if (await predicate()) return; await Task.Delay(150); }
        throw new TimeoutException(message);
    }
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("FAIL " + message); }
    sealed class Owned
    {
        public Process Process { get; }
        public string Directory { get; }
        public string Id { get; }
        public string Token { get; }
        public bool Linux { get; }
        public bool HoldBeforePublish { get; init; }
        public int HostPid { get; set; }
        public string Address { get; set; } = "";
        public Task Logs { get; }
        public Owned(Process process, string directory, string id, string token, int generation, bool linux)
        {
            Process = process; Directory = directory; Id = id; Token = token; Linux = linux;
            Logs = Task.WhenAll(Copy(process.StandardOutput, "stdout"), Copy(process.StandardError, "stderr"));
            async Task Copy(StreamReader reader, string name)
            {
                using var writer = new StreamWriter(Path.Combine(directory, $"{generation}.{name}.log")) { AutoFlush = true };
                while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
            }
        }
    }
}

sealed class SyncProxy : IAsyncDisposable
{
    public sealed record Wire(RuntimeStateSyncBatch Batch, RuntimeStateSyncAck? Ack, int Status, bool Held, DateTimeOffset At);
    readonly WebApplication app;
    readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly StreamWriter wire;
    public readonly ConcurrentQueue<Wire> Events = new();
    public readonly TaskCompletionSource<RuntimeStateSyncAck> Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HoldCommit;
    public int? HoldPage;
    public volatile bool Blocked;
    public string Target = "";
    public int Port;
    SyncProxy(WebApplication app, string root) { this.app = app; wire = new(Path.Combine(root, "wire.jsonl")) { AutoFlush = true }; }
    public static async Task<SyncProxy> Start(string root)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var proxy = new SyncProxy(builder.Build(), root);
        proxy.app.Run(proxy.Forward);
        await proxy.app.StartAsync();
        proxy.Port = new Uri(proxy.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        return proxy;
    }
    public void Release() { HoldCommit = false; HoldPage = null; release.TrySetResult(); }
    async Task Forward(HttpContext context)
    {
        if (Blocked) { context.Response.StatusCode = 503; return; }
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();
        var message = JsonSerializer.Deserialize<NodeMessage>(body, BootstrapProcessHarness.Json)!;
        var batch = message.Payload.Deserialize<RuntimeStateSyncBatch>(BootstrapProcessHarness.Json)!;
        using var request = new HttpRequestMessage(HttpMethod.Post, Target + context.Request.Path) { Content = new ByteArrayContent(body) };
        foreach (var header in context.Request.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && !request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        using var response = await http.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var ack = JsonSerializer.Deserialize<RuntimeStateSyncAck>(bytes, BootstrapProcessHarness.Json);
        var hold = HoldCommit && batch.Bootstrap?.Phase == "commit"
            || HoldPage is not null && batch.Bootstrap?.Phase == "page" && batch.Bootstrap.PageIndex == HoldPage;
        var entry = new Wire(batch, ack, (int)response.StatusCode, hold, DateTimeOffset.UtcNow);
        Events.Enqueue(entry);
        lock (wire) wire.WriteLine(JsonSerializer.Serialize(entry, BootstrapProcessHarness.Json));
        if (hold)
        {
            Held.TrySetResult(ack!);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(40));
            context.Abort();
            return;
        }
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(bytes);
    }
    public async ValueTask DisposeAsync()
    {
        Release(); await app.StopAsync(); await app.DisposeAsync(); http.Dispose(); wire.Dispose();
    }
}
