using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

if (args.FirstOrDefault() == "capacity-host") await CapacityProcessHarness.Host(args[1], args.ElementAtOrDefault(2));
else if (args.FirstOrDefault() == "capacity-snapshot-suite") await CapacityProcessHarness.RunSnapshots();
else if (args.FirstOrDefault() == "receiver-peak-host") await ReceiverPeakProcessHarness.Host(args[1]);
else if (args.FirstOrDefault() == "receiver-peak-suite") await ReceiverPeakProcessHarness.Run(args.ElementAtOrDefault(1) ?? "", args.Contains("--windows-only"));
else if (args.FirstOrDefault() == "capacity-cross-suite") await CapacityProcessHarness.Run(args[1]);
else if (args.FirstOrDefault() == "index-gap-suite") await CapacityProcessHarness.RunIndexGaps(args[1]);
else if (args.FirstOrDefault() == "capacity-suite") await CapacityProcessHarness.Run();
else if (args.FirstOrDefault() == "reload-suite") await ReloadProcessHarness.Run();
else if (args.FirstOrDefault() == "v7-suite") await V7ProcessHarness.Run();
else if (args.FirstOrDefault() == "bootstrap-host") await BootstrapProcessHarness.Host(args[1]);
else if (args.FirstOrDefault() == "bootstrap-suite") await BootstrapProcessHarness.Run(args[1], args.Contains("--windows-only"));
else if (args.FirstOrDefault() == "interop-host") await InteropProcessHarness.Host(args);
else if (args.FirstOrDefault() == "interop-suite") await InteropProcessHarness.Run(args[1]);
else if (args.FirstOrDefault() == "transport-suite") await TransportProcessHarness.Run();
else if (args.FirstOrDefault() is "scale-node" or "tls-node" or "tls-client") await TransportProcessHarness.Host(args);
else if (args.FirstOrDefault() == "configuration-child") await ConfigurationProcessHarness.Host(args);
else if (args.FirstOrDefault() == "configuration-suite") await ConfigurationProcessHarness.Run();
else if (args.FirstOrDefault() == "node") await Harness.Node(args);
else await Harness.Run();

static class Harness
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const string Secret = "isolated-process-test-key-not-for-production-123456";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task Node(string[] args)
    {
        var id = args[1];
        var root = Path.GetFullPath(args[2]);
        var incoming = int.Parse(args[3]);
        var outgoing = int.Parse(args[4]);
        var peer = id == "a" ? "b" : "a";
        var runtime = new RuntimeStateOptions { Enabled = true, NodeId = id,
            ProtectionGroupKeyPrefixes = new() { ["harness"] = ["shared|", "counter"] },
            StateDirectory = Path.Combine(root, "runtime"), Peers = new()
            { [peer] = new() { ProtocolVersion = 2, Endpoint = $"http://127.0.0.1:{outgoing}",
                Send = TestPolicy(), Receive = TestPolicy() } } };
        if (!RuntimeStateReplicationAuthorization.IsValidConfiguration(runtime))
            throw new InvalidOperationException("Invalid harness runtime grants");
        var protocol = new NodeCommunicationOptions { Enabled = true, AllowLegacyProtocol = false,
            RequireHttps = false, ClusterId = "process-test", NodeId = id,
            StateDirectory = Path.Combine(root, "protocol"), Peers = new()
            { [peer] = new() { ActiveKeyId = "test", Keys = new() { ["test"] = Secret }, AllowedOperations = ["runtime.merge"] } } };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ContentRootPath = root, EnvironmentName = "Testing", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new FixedOptions<RuntimeStateOptions>(runtime));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new FixedOptions<NodeCommunicationOptions>(protocol));
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<RuntimeStateStore>();
        builder.Services.AddSingleton<IRuntimeStateStore>(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddSingleton<RuntimeStateReplicator>();
        builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateReplicator>());
        await using var app = builder.Build();
        app.MapNodeCommunication(incoming);
        app.MapGet("/test/state", (RuntimeStateStore store) => Results.Json(new
        { pid = Environment.ProcessId, snapshots = store.GetSnapshots(), status = store.GetStatus() }));
        app.MapPost("/test/increment", (Increment input, RuntimeStateStore store) =>
            Results.Json(store.IncrementWindow("process.counter", "shared", input.Window, TimeSpan.FromHours(1), input.Amount)));
        app.MapPost("/test/stop", (IHostApplicationLifetime lifetime) =>
        { lifetime.StopApplication(); return Results.Ok(); });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await File.WriteAllTextAsync(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(new { address, pid = Environment.ProcessId }));
        await app.WaitForShutdownAsync();
    }

    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-multiprocess-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        var children = new List<Child>();
        void Record(string message)
        { var line = $"{DateTimeOffset.UtcNow:O} {message}"; Console.WriteLine(line); log.WriteLine(line); }
        await using var pa = new FaultProxy();
        await using var pb = new FaultProxy();
        var window = DateTimeOffset.UtcNow;
        async Task<Child> Start(string id, FaultProxy inbound, FaultProxy outbound)
        {
            var dir = Path.Combine(root, id);
            Directory.CreateDirectory(dir);
            File.Delete(Path.Combine(dir, "ready.json"));
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = dir, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "node", id, dir,
                inbound.Port.ToString(), outbound.Port.ToString() }) start.ArgumentList.Add(arg);
            var process = Process.Start(start)!;
            var child = new Child(process, dir);
            children.Add(child);
            Record($"START node={id} pid={process.Id} directory={dir}");
            await Until(() => Task.FromResult(File.Exists(Path.Combine(dir, "ready.json"))), "ready " + id, 30);
            using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "ready.json")));
            Check(ready.RootElement.GetProperty("pid").GetInt32() == process.Id, "ready PID ownership");
            child.Address = ready.RootElement.GetProperty("address").GetString()!;
            inbound.TargetPort = new Uri(child.Address).Port;
            Record($"READY node={id} pid={process.Id} address={child.Address} proxy={inbound.Port}");
            return child;
        }
        async Task<long> Value(Child child, string domain = "process.counter")
        {
            using var state = JsonDocument.Parse(await Http.GetStringAsync(child.Address + "/test/state"));
            return state.RootElement.GetProperty("snapshots").EnumerateArray()
                .Where(x => x.GetProperty("domain").GetString() == domain)
                .Sum(x => x.GetProperty("counterValue").GetInt64());
        }
        async Task IncrementNode(Child child, long amount)
        { using var response = await Http.PostAsJsonAsync(child.Address + "/test/increment", new Increment(window, amount)); response.EnsureSuccessStatusCode(); }
        async Task Converged(Child a, Child b, long expected)
        {
            await Until(async () => await Value(a) == expected && await Value(b) == expected, "converge " + expected, 80);
            Record($"PASS convergence a={await Value(a)} b={await Value(b)} expected={expected}");
        }
        try
        {
            Record($"BEGIN artifactRoot={root}; actual store/authentication/endpoint/replicator; two child processes");
            var a = await Start("a", pa, pb);
            var b = await Start("b", pb, pa);
            await IncrementNode(a, 5);
            await IncrementNode(b, 7);
            await Converged(a, b, 12);

            var now = DateTimeOffset.UtcNow;
            var mutation = new RuntimeStateMutation("process.duplicate", "counter", "a", 1, now, now.AddHours(1), false,
                null, new Dictionary<string, long> { ["a"] = 40 });
            var message = new NodeMessage(2, "process-test", "a", "b", Guid.NewGuid().ToString("D"), "duplicate",
                "runtime.merge", now, now.AddMinutes(10), JsonSerializer.SerializeToElement(new[] { mutation, mutation }, Json));
            var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            using var original = Signed(pb.Port, message, body);
            var headers = original.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray());
            using (var response = await Http.SendAsync(original)) response.EnsureSuccessStatusCode();
            async Task Replay()
            {
                using var request = Signed(pb.Port, message, body);
                request.Headers.Clear();
                foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                using var response = await Http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                Check(response.StatusCode == HttpStatusCode.Unauthorized && text.Contains("node_replay"), "signed replay rejected: " + text);
            }
            await Replay();
            for (var i = 0; i < 3; i++)
            {
                using var request = Signed(pb.Port, message, body);
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
            Check(await Value(b, "process.duplicate") == 40, "duplicate count remains 40");
            Record("PASS same-signature replay=401 node_replay; fresh-signature duplicate batch count=40");

            foreach (var denied in new[]
            {
                (Mutation: mutation with { Domain = "process.forbidden" }, Code: "runtime_domain_forbidden"),
                (Mutation: mutation with { Key = "outside-test-group" }, Code: "runtime_group_forbidden"),
                (Mutation: mutation with { NodeId = "untrusted-source" }, Code: "runtime_source_forbidden"),
                (Mutation: mutation with { CounterComponents = new Dictionary<string, long> { ["untrusted-source"] = 999 } }, Code: "runtime_counter_source_forbidden")
            })
            {
                var batch = new[] { mutation with { CounterComponents = new Dictionary<string, long> { ["a"] = 100 } }, denied.Mutation };
                var envelope = message with { MessageId = Guid.NewGuid().ToString("D"), Payload = JsonSerializer.SerializeToElement(batch, Json) };
                using var request = Signed(pb.Port, envelope, JsonSerializer.SerializeToUtf8Bytes(envelope, Json));
                using var response = await Http.SendAsync(request);
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(response.StatusCode == HttpStatusCode.Forbidden && error.RootElement.GetProperty("errorCode").GetString() == denied.Code,
                    "explicit runtime ACL rejection " + denied.Code);
                Check(await Value(b, "process.duplicate") == 40, "denied batch must not partially merge the first authorized mutation");
                Record("PASS explicit ACL 403 " + denied.Code + "; whole batch unchanged count=40");
            }

            // Stop both forwarding directions and close already established TCP connections.
            pa.Block(); pb.Block();
            await IncrementNode(a, 3);
            await IncrementNode(b, 4);
            await Task.Delay(3000);
            Check(await Value(a) == 15 && await Value(b) == 16, "partition isolates live nodes");
            Check(!a.Process.HasExited && !b.Process.HasExited && pa.Rejected + pb.Rejected > 0, "actual network failure observed");
            Record($"PASS partition live PIDs={a.Process.Id},{b.Process.Id}; a=15 b=16 rejectedConnections={pa.Rejected + pb.Rejected}");
            pa.Unblock(); pb.Unblock();
            await Converged(a, b, 19);
            Record("PASS automatic anti-entropy recovery without another write");

            pa.Block(); pb.Block();
            using (var response = await Http.PostAsync(b.Address + "/test/stop", null)) response.EnsureSuccessStatusCode();
            await b.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Check(b.Process.ExitCode == 0, "graceful exit");
            Record($"STOP graceful pid={b.Process.Id} exit=0");
            b = await Start("b", pb, pa);
            Check(await Value(b) == 19 && await Value(b, "process.duplicate") == 40, "isolated graceful disk recovery");
            Record("PASS graceful restart recovered count=19 duplicate=40 with network blocked");
            // Wait for the real journal to expose the latest count before killing; this does not claim fsync/power-loss safety.
            await IncrementNode(b, 2);
            await Until(async () =>
            {
                foreach (var file in Directory.GetFiles(Path.Combine(b.Directory, "runtime"), "*.jsonl"))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        try { var m = JsonSerializer.Deserialize<RuntimeStateMutation>(line, Json);
                            if (m?.Domain == "process.counter" && m.CounterComponents?.Values.Sum() == 21) return true; }
                        catch (JsonException) { }
                    }
                }
                return false;
            }, "journal count=21 visible", 15);
            var killed = b.Process.Id;
            b.Process.Kill();
            await b.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Record($"KILL owned pid={killed} exit={b.Process.ExitCode}");
            b = await Start("b", pb, pa);
            Check(await Value(b) == 21 && await Value(b, "process.duplicate") == 40, "isolated crash disk recovery");
            Record("PASS forced-kill restart recovered count=21 duplicate=40 with network blocked");
            await IncrementNode(b, 6);
            Check(await Value(b) == 27, "post-crash local increment");
            pa.Unblock(); pb.Unblock();
            await Replay();
            Record("PASS durable replay rejection after graceful and forced restart");
            await Converged(a, b, 27);
            await Task.Delay(32000);
            Check(await Value(a) == 27 && await Value(b) == 27, "repeated anti-entropy does not inflate counts");
            Record("PASS repeated anti-entropy stable a=27 b=27");
            Record("RESULT PASS (scoped two-node loopback process fault coverage)");
        }
        catch (Exception ex) { Record("RESULT FAIL " + ex); throw; }
        finally
        {
            foreach (var child in children)
            {
                if (!child.Process.HasExited)
                {
                    Record($"CLEANUP kill owned pid={child.Process.Id}");
                    child.Process.Kill();
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                await child.Logs;
                child.Process.Dispose();
            }
            Record("END all owned children exited; artifacts retained");
        }
    }

    static HttpRequestMessage Signed(int port, NodeMessage message, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}" + NodeMessageAuthentication.Prefix + message.Type)
        { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        NodeMessageAuthentication.Sign(request, message, "test", Secret, body);
        return request;
    }
    static RuntimeStateReplicationPolicy TestPolicy() => new()
    {
        AllowedDomains = ["process.counter", "process.duplicate"], AllowedProtectionGroups = ["harness"],
        TrustedForwardedSources = ["a", "b"]
    };
    static void Check(bool condition, string description)
    { if (!condition) throw new InvalidOperationException(description); }
    static async Task Until(Func<Task<bool>> predicate, string description, int seconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
        { if (await predicate()) return; await Task.Delay(200); }
        throw new TimeoutException(description);
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
            using var writer = new StreamWriter(Path.Combine(Directory, $"{Process.Id}.{name}.log"));
            while (await reader.ReadLineAsync() is { } line) { await writer.WriteLineAsync(line); await writer.FlushAsync(); }
        }
    }
    public sealed record Increment(DateTimeOffset Window, long Amount);
    sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

// Transparent TCP forwarding preserves the production signature, HTTP path and Host header.
sealed class FaultProxy : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<TcpClient, byte> sockets = new();
    readonly ConcurrentBag<Task> connections = new();
    readonly Task accept;
    volatile bool blocked;
    public volatile int TargetPort;
    long rejected;
    public long Rejected => Interlocked.Read(ref rejected);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FaultProxy() { listener.Start(); accept = Accept(); }
    public void Block() { blocked = true; foreach (var socket in sockets.Keys) socket.Dispose(); }
    public void Unblock() => blocked = false;
    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                sockets.TryAdd(client, 0);
                connections.Add(Forward(client));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    async Task Forward(TcpClient incoming)
    {
        using var outgoing = new TcpClient();
        sockets.TryAdd(outgoing, 0);
        try
        {
            if (blocked || TargetPort == 0) { Interlocked.Increment(ref rejected); return; }
            await outgoing.ConnectAsync(IPAddress.Loopback, TargetPort, stop.Token);
            var up = incoming.GetStream().CopyToAsync(outgoing.GetStream(), stop.Token);
            var down = outgoing.GetStream().CopyToAsync(incoming.GetStream(), stop.Token);
            await Task.WhenAny(up, down);
            incoming.Dispose(); outgoing.Dispose();
            await Task.WhenAll(up, down);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally { incoming.Dispose(); sockets.TryRemove(incoming, out _); sockets.TryRemove(outgoing, out _); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); Block(); listener.Stop();
        await accept; await Task.WhenAll(connections); stop.Dispose();
    }
}
