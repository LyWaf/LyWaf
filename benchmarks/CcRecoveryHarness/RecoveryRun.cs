using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;

static class RecoveryRun
{
    internal static async Task Run(string? output, bool release)
    {
        var root = Path.GetFullPath(output ?? Path.Combine(Path.GetTempPath(), "lywaf-cc-recovery-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(root)) throw new IOException("Evidence directory must be fresh");
        Directory.CreateDirectory(root);
        var owner = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(root, "owner.token"), owner);
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            await File.WriteAllTextAsync(Path.Combine(root, "private.pem"), key.ExportPkcs8PrivateKeyPem());
            await File.WriteAllTextAsync(Path.Combine(root, "public.pem"), key.ExportSubjectPublicKeyInfoPem());
        }
        var binary = Assembly.GetExecutingAssembly().Location;
        var binaries = Directory.GetFiles(Path.GetDirectoryName(binary)!, "*.dll").Order().ToDictionary(p => Path.GetFileName(p)!,
            p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        await Save("snapshot.json", new { binary, binaries, os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), release });
        Console.WriteLine("EVIDENCE " + root);
        var listeners = Enumerable.Range(0, 6).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var socket in listeners) socket.Start();
        var ports = listeners.Select(s => ((IPEndPoint)s.LocalEndpoint).Port).ToArray();
        foreach (var socket in listeners) socket.Stop();
        var backend = ports[..3]; var gateways = ports[3..];
        var children = new Process?[3]; var all = new List<Process>(); var captures = new List<Task>();
        var launches = new int[3]; var lifecycle = new List<object>(); var observations = new List<object>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        http.DefaultRequestHeaders.Add("X-Harness-Owner", owner);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        await using var proxy = await RecoveryProxy.Start(root, gateways, backend);
        string Url(int i, string path) => $"http://127.0.0.1:{backend[i]}{path}";
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(root, name), JsonSerializer.Serialize(value, RecoveryNode.Json));
        async Task Post(int i, string path)
        { using var response = await http.PostAsync(Url(i, path), null, deadline.Token); response.EnsureSuccessStatusCode(); }
        async Task<JsonElement> Read(int i)
        { return JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync(Url(i, "/state"), deadline.Token)); }
        static bool Complete(JsonElement state) => state.GetProperty("coverage").GetProperty("status").GetString() == "Complete";
        static bool Confirmed(JsonElement state) => state.GetProperty("execution").ValueKind == JsonValueKind.Object
            && state.GetProperty("execution").GetProperty("allNodesConfirmed").GetBoolean();
        static string Boot(JsonElement state) => state.GetProperty("identity").GetProperty("bootId").GetString()!;
        async Task<JsonElement[]> Wait(string stage, Func<JsonElement[], bool> predicate, int timeout = 30000, int[]? nodes = null)
        {
            nodes ??= [0, 1, 2]; var watch = Stopwatch.StartNew();
            while (true)
            {
                var states = await Task.WhenAll(nodes.Select(Read));
                observations.Add(new { stage, elapsedMs = watch.ElapsedMilliseconds, nodes, states });
                if (predicate(states)) { Console.WriteLine($"PASS {stage} {watch.ElapsedMilliseconds}ms"); return states; }
                if (watch.ElapsedMilliseconds > timeout) throw new InvalidOperationException("Timeout: " + stage);
                await Task.Delay(100, deadline.Token);
            }
        }
        async Task NeverComplete(string stage, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            do
            {
                var state = await Read(0); observations.Add(new { stage, elapsedMs = watch.ElapsedMilliseconds, state });
                if (Complete(state) || Confirmed(state)) throw new InvalidOperationException(stage + ": stale evidence restored confirmation");
                await Task.Delay(75, deadline.Token);
            } while (watch.ElapsedMilliseconds < milliseconds);
            Console.WriteLine("PASS " + stage);
        }
        async Task Start(int i)
        {
            var id = RecoveryNode.Ids[i]; var directory = Path.Combine(root, id); Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            start.Environment["DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] = "false";
            foreach (var arg in new[] { binary, "child", id, root, backend[i].ToString(), string.Join(',', gateways) }) start.ArgumentList.Add(arg);
            var child = Process.Start(start)!; children[i] = child; all.Add(child); var launch = ++launches[i];
            lifecycle.Add(new { action = "start", id, launch, pid = child.Id, utc = DateTimeOffset.UtcNow });
            captures.Add(Capture(child.StandardOutput, Path.Combine(directory, $"stdout-{launch}.log")));
            captures.Add(Capture(child.StandardError, Path.Combine(directory, $"stderr-{launch}.log")));
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (child.HasExited) throw new InvalidOperationException($"Child {id} exited: {child.ExitCode}");
                try
                {
                    var ready = await Read(i);
                    if (ready.GetProperty("pid").GetInt32() != child.Id) throw new InvalidOperationException("Unexpected process on reserved port");
                    lifecycle.Add(new { action = "ready", id, launch, pid = child.Id, boot = Boot(ready) }); break;
                }
                catch (HttpRequestException) when (watch.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(100, deadline.Token); }
            }
        }
        async Task Kill(int i, string stage)
        {
            var child = children[i] ?? throw new InvalidOperationException("Not an owned child");
            // The Process object was returned by this runner's Process.Start; never look up names or unrelated PIDs.
            lifecycle.Add(new { action = "kill", stage, pid = child.Id, utc = DateTimeOffset.UtcNow });
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync(deadline.Token); children[i] = null;
            lifecycle.Add(new { action = "exited", stage, pid = child.Id, code = child.ExitCode });
        }
        try
        {
            for (var i = 0; i < 3; i++) await Start(i);
            await Wait("initial-zero-complete", s => s.All(Complete));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Task.Delay((int)(10000 - now % 10000 + 40), deadline.Token);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Post(i, "/seed/" + new[] { 40, 35, 30 }[i])));
            var decided = await Wait("background-decision", s => s.All(x => x.GetProperty("decision").ValueKind == JsonValueKind.Object));
            var ids = decided.Select(x => x.GetProperty("decision").GetProperty("decisionId").GetString()).Distinct().ToArray();
            if (ids.Length != 1) throw new InvalidOperationException("Different decisions");
            var decisionId = ids[0];
            var baseline = await Wait("initial-boot-confirmation", s => s.All(Confirmed));
            await Save("baseline.json", baseline);

            proxy.Arm(); await proxy.Held.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            await Post(0, "/query/c/false");
            await Wait("local-query-revocation", s => !Complete(s[0]) && !Confirmed(s[0]), nodes: [0]);
            proxy.Release();
            await NeverComplete("late-reply-after-local-revocation", 1200);
            await Post(0, "/query/c/true");
            await Wait("local-query-regrant", s => s.All(Complete));
            await Post(2, "/query/a/false");
            await Wait("remote-query-revocation", s => !Complete(s[0]), nodes: [0]);
            await NeverComplete("remote-revocation-remains-incomplete", 1000);
            await Post(2, "/query/a/true");
            await Wait("remote-query-regrant", s => s.All(Complete));

            var oldBoot = Boot(await Read(2));
            proxy.Arm(); await proxy.Held.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token);
            await Kill(2, "decision-node-restart");
            await Task.Delay(650, deadline.Token);
            await Wait("offline-invalidates-boot-confirmation", s => !Complete(s[0]) && !Confirmed(s[0]), nodes: [0]);
            await Start(2);
            var newBoot = Boot(await Read(2));
            if (newBoot == oldBoot) throw new InvalidOperationException("Boot did not change");
            proxy.BlockQueries = true; proxy.Release();
            await NeverComplete("late-old-boot-reply-cannot-restore-complete", 1400);
            proxy.BlockQueries = false;
            var recovered = await Wait("new-boot-fresh-confirmation", s => s.All(Complete) && s.All(Confirmed)
                && s.All(x => x.GetProperty("execution").GetProperty("boots").GetProperty("c").GetString() == newBoot));
            if (recovered.Any(x => x.GetProperty("decision").GetProperty("decisionId").GetString() != decisionId))
                throw new InvalidOperationException("Restart changed logical decision");
            await Save("restart.json", new { oldBoot, newBoot, recovered });

            if (release)
            {
                for (var i = 0; i < 3; i++) await Post(i, "/connect");
                await proxy.WaitApplied("c", "a", deadline.Token);
                var oldMessage = proxy.AppliedRequests["c>a"];
                await Kill(2, "offline-before-release");
                await Post(0, "/release");
                await Wait("online-release-generation", s => s.All(x => x.GetProperty("generation").GetProperty("version").GetInt64() == 1), nodes: [0, 1]);
                await Task.Delay(6500, deadline.Token);
                await Start(2); await Post(2, "/connect");
                await Wait("offline-node-bootstrap-release-fence", s => s[0].GetProperty("generation").GetProperty("version").GetInt64() == 1
                    && proxy.Bootstrapped.Contains("c>a") && Rebased(),
                    timeout: 60000, nodes: [2]);
                var state = await Read(2);
                if (state.GetProperty("penalties").GetArrayLength() != 0) throw new InvalidOperationException("Old penalty survived bootstrap");
                using var response = await http.GetAsync(Url(2, "/protected"), deadline.Token);
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("Released node still enforces old penalty");
                await Save("released-bootstrap.json", state);
                var message = JsonSerializer.Deserialize<NodeMessage>(oldMessage, RecoveryNode.Json)! with
                { MessageId = Guid.NewGuid().ToString("D"), CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) };
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message, RecoveryNode.Json);
                using var stale = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{gateways[0]}" + NodeMessageAuthentication.Prefix + "runtime.sync")
                    { Content = new ByteArrayContent(bytes) };
                stale.Content.Headers.ContentType = new("application/json");
                NodeMessageAuthentication.Sign(stale, message, "test", RecoveryNode.Secret, bytes);
                using var rejected = await http.SendAsync(stale, deadline.Token);
                var rejection = JsonSerializer.Deserialize<JsonElement>(await rejected.Content.ReadAsStringAsync(deadline.Token));
                await Save("old-epoch-response.json", rejection);
                if (rejection.GetProperty("status").GetString() != "SnapshotRequired") throw new InvalidOperationException("Old stream epoch accepted");
                var after = await Read(2);
                if (after.GetProperty("generation").GetProperty("version").GetInt64() != 1 || after.GetProperty("penalties").GetArrayLength() != 0)
                    throw new InvalidOperationException("Late old stream resurrected penalty");
                Console.WriteLine("PASS offline-release-bootstrap-no-old-penalty");

                bool Rebased()
                {
                    if (!proxy.Manifests.TryGetValue("c>a", out var manifest)) return false;
                    foreach (var file in Directory.GetFiles(Path.Combine(root, "c", "runtime", "runtime-sync"), "out-*.json"))
                    {
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var document = JsonDocument.Parse(stream);
                        var value = document.RootElement.GetProperty("value");
                        if (value.GetProperty("receiverNodeId").GetString() == "a"
                            && value.GetProperty("epoch").GetString() == manifest.GetProperty("header").GetProperty("newEpoch").GetString()
                            && value.GetProperty("bootstrapDigest").GetString() == manifest.GetProperty("header").GetProperty("digest").GetString())
                            return true;
                    }
                    return false;
                }
            }
            await Save("result.json", new { passed = true, release, decisionId });
        }
        catch (Exception ex) { await Save("failure.json", new { error = ex.ToString() }); throw; }
        finally
        {
            proxy.BlockQueries = false; proxy.Release();
            for (var i = 0; i < 3; i++)
            {
                var child = children[i]; if (child is null || child.HasExited) continue;
                try { using var stop = await http.PostAsync(Url(i, "/stop"), null); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync(); }
                lifecycle.Add(new { action = "cleanup-exited", pid = child.Id, code = child.ExitCode });
            }
            await Task.WhenAll(captures);
            await Save("lifecycle.json", lifecycle); await Save("observations.json", observations);
            foreach (var child in all) child.Dispose();
            foreach (var pair in binaries)
                if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(binary)!, pair.Key!)))) != pair.Value)
                    throw new InvalidOperationException("Binary changed during run");
        }
    }

    private static async Task Capture(StreamReader reader, string path)
    {
        await using var writer = new StreamWriter(path) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
    }
}

sealed class RecoveryProxy : IAsyncDisposable
{
    private WebApplication _app = null!;
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, bool> _applied = new();
    internal readonly ConcurrentDictionary<string, string> AppliedRequests = new();
    internal readonly ConcurrentDictionary<string, JsonElement> Manifests = new();
    internal readonly ConcurrentBag<string> Bootstrapped = new();
    private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _armed;
    internal volatile bool BlockQueries;
    internal Task Held => _held.Task;
    internal void Arm() { lock (_gate) { _held = new(TaskCreationOptions.RunContinuationsAsynchronously); _release = new(TaskCreationOptions.RunContinuationsAsynchronously); _armed = true; } }
    internal void Release() => _release.TrySetResult();
    internal async Task WaitApplied(string sender, string receiver, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!_applied.ContainsKey(sender + ">" + receiver))
        { if (watch.Elapsed > TimeSpan.FromSeconds(40)) throw new InvalidOperationException("No durable ACK before offline"); await Task.Delay(100, token); }
    }
    internal static async Task<RecoveryProxy> Start(string root, int[] gateways, int[] backend)
    {
        var p = new RecoveryProxy();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root, EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { foreach (var port in gateways) k.Listen(IPAddress.Loopback, port); });
        p._app = builder.Build();
        var wire = Path.Combine(root, "wire.jsonl"); var serial = 0;
        p._app.Run(async context =>
        {
            var receiver = RecoveryNode.Ids[Array.IndexOf(gateways, context.Connection.LocalPort)];
            var sender = context.Request.Headers["X-LyWaf-Sender"].ToString();
            var query = context.Request.Path.Value?.EndsWith("cc.execution.query", StringComparison.Ordinal) == true;
            if (p.BlockQueries && query && receiver == "c" && sender == "a") { context.Response.StatusCode = 503; return; }
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                $"http://127.0.0.1:{backend[Array.IndexOf(RecoveryNode.Ids, receiver)]}{context.Request.Path}{context.Request.QueryString}");
            using var body = new MemoryStream(); await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            var bytes = body.ToArray(); request.Content = new ByteArrayContent(bytes);
            foreach (var header in context.Request.Headers)
                if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            try
            {
                using var response = await p._http.SendAsync(request, context.RequestAborted);
                var responseBytes = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
                var text = System.Text.Encoding.UTF8.GetString(responseBytes);
                var status = "";
                try { using var document = JsonDocument.Parse(responseBytes); if (document.RootElement.TryGetProperty("status", out var value)) status = value.GetString(); } catch (JsonException) { }
                var pair = sender + ">" + receiver;
                if (!query && status == "Applied") { p.AppliedRequests[pair] = System.Text.Encoding.UTF8.GetString(bytes); p._applied[pair] = true; }
                if (!query && status == "SnapshotOffered")
                { using var document = JsonDocument.Parse(responseBytes); p.Manifests[pair] = document.RootElement.GetProperty("manifest").Clone(); }
                if (!query && status == "Bootstrapped") p.Bootstrapped.Add(pair);
                Task? hold = null;
                lock (p._gate)
                {
                    File.AppendAllText(wire, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, sender, receiver,
                        path = context.Request.Path.Value, http = (int)response.StatusCode, request = System.Text.Encoding.UTF8.GetString(bytes), response = text }, RecoveryNode.Json) + Environment.NewLine);
                    if (p._armed && query && sender == "a" && receiver == "c" && response.IsSuccessStatusCode)
                    {
                        p._armed = false; hold = p._release.Task;
                        File.WriteAllBytes(Path.Combine(root, $"held-reply-{++serial}.json"), responseBytes);
                    }
                }
                context.Response.StatusCode = (int)response.StatusCode; context.Response.ContentType = "application/json";
                context.Response.ContentLength = responseBytes.Length;
                if (hold is not null)
                {
                    await context.Response.StartAsync(context.RequestAborted); p._held.TrySetResult();
                    await hold.WaitAsync(context.RequestAborted);
                }
                await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted);
                if (hold is not null)
                    lock (p._gate) File.AppendAllText(Path.Combine(root, "held-delivery.jsonl"), JsonSerializer.Serialize(new
                        { utc = DateTimeOffset.UtcNow, sender, receiver, bytes = responseBytes.Length,
                            sha256 = Convert.ToHexString(SHA256.HashData(responseBytes)) }, RecoveryNode.Json) + Environment.NewLine);
            }
            catch (HttpRequestException) { if (!context.Response.HasStarted) context.Response.StatusCode = 503; }
            catch (OperationCanceledException) { if (!context.Response.HasStarted) context.Response.StatusCode = 504; }
        });
        await p._app.StartAsync(); return p;
    }
    public async ValueTask DisposeAsync()
    { Release(); await _app.StopAsync(); await _app.DisposeAsync(); _http.Dispose(); }
}
