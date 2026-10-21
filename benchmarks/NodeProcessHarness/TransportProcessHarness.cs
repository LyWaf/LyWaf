using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

static class TransportProcessHarness
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
    const string Secret = "transport-harness-key-not-for-production-123456789";

    public static async Task Host(string[] args)
    {
        var root = Path.GetFullPath(args[1]);
        if (args[0] == "tls-client") { await TlsClient(root); return; }
        var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(Path.Combine(root, "settings.json")), Json)!;
        var runtime = new RuntimeStateOptions
        {
            Enabled = true, NodeId = settings.Id, StateDirectory = Path.Combine(root, "runtime"), QueueCapacity = 100,
            ReplicationQueueMaxEntries = 2000, ReplicationQueueMaxBytes = 2 * 1024 * 1024,
            ReplicationQueuePerQueueMaxBytes = 128 * 1024, ReplicationMaxMutationBytes = 32768,
            ProtectionGroupKeyPrefixes = new() { ["test-group"] = ["shared|"] },
            Peers = settings.Peers.ToDictionary(p => p.Key, p => new RuntimeStatePeerOptions
            {
                ProtocolVersion = 2, Endpoint = p.Value,
                Send = settings.Id == "n00" ? Grant("process.scale") : new(),
                Receive = settings.Id == "n00" ? new() : Grant(settings.Https ? "process.tls" : "process.scale")
            })
        };
        if (!RuntimeStateReplicationAuthorization.IsValidConfiguration(runtime)) throw new InvalidOperationException("Invalid test grants");
        var protocol = new NodeCommunicationOptions
        {
            Enabled = true, NodeId = settings.Id, ClusterId = "transport-test", RequireHttps = settings.Https,
            AllowLegacyProtocol = false, MaxMessageBytes = 32768, StateDirectory = Path.Combine(root, "protocol"),
            Peers = settings.Peers.ToDictionary(p => p.Key, _ => new NodePeerOptions
            { ActiveKeyId = "test", Keys = new() { ["test"] = Secret }, AllowedOperations = ["runtime.merge"] })
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root, EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
        using var certificate = settings.Https ? X509CertificateLoader.LoadPkcs12FromFile(settings.Certificate!, settings.Password) : null;
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen =>
        { if (certificate is not null) listen.UseHttps(certificate); }));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new FixedOptions<RuntimeStateOptions>(runtime));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new FixedOptions<NodeCommunicationOptions>(protocol));
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<RuntimeStateStore>();
        builder.Services.AddSingleton<IRuntimeStateStore>(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateStore>());
        builder.Services.AddSingleton<RuntimeStateReplicator>();
        builder.Services.AddHostedService(s => s.GetRequiredService<RuntimeStateReplicator>());
        await using var app = builder.Build();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!settings.Blocked) release.TrySetResult();
        long blockedRequests = 0;
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == NodeMessageAuthentication.Prefix + "runtime.merge" && !release.Task.IsCompleted)
            {
                Interlocked.Increment(ref blockedRequests);
                await release.Task.WaitAsync(context.RequestAborted);
            }
            await next(context);
        });
        app.MapPost("/test/release", () => { release.TrySetResult(); return Results.Ok(); });
        app.MapPost("/test/load", (Load load, RuntimeStateStore store) =>
        {
            if (load.Count is < 1 or > 1000) return Results.BadRequest();
            for (var i = 0; i < load.Count; i++) store.IncrementWindow("process.scale", "shared", load.Window, TimeSpan.FromHours(1));
            return Results.Ok();
        });
        app.MapGet("/test/state", (RuntimeStateStore store, RuntimeStateReplicator replicator) => Results.Json(new Sample(
            settings.Id, store.GetSnapshots().Sum(s => s.CounterValue ?? 0), store.GetStatus(), replicator.DroppedMutations,
            replicator.AuthorizationRejectedMutations, Queues(replicator), Interlocked.Read(ref blockedRequests),
            GC.GetTotalMemory(false), Environment.WorkingSet)));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        // Endpoint data sources are dynamic. Publish readiness only after mapping the actual OS-assigned port.
        app.MapNodeCommunication(new Uri(address).Port);
        await File.WriteAllTextAsync(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(new { address, pid = Environment.ProcessId }, Json));
        await app.WaitForShutdownAsync();
    }

    static RuntimeStateReplicationPolicy Grant(string domain) => new()
    { AllowedDomains = [domain], AllowedProtectionGroups = ["test-group"] };

    // Read-only test instrumentation. The 19-worker dictionary stops changing after warmup because the peer set is fixed.
    static QueueSample Queues(RuntimeStateReplicator replicator)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var global = typeof(RuntimeStateReplicator).GetField("_queue", flags)!.GetValue(replicator)!;
        var pool = typeof(RuntimeStateReplicator).GetField("_queuePool", flags)!.GetValue(replicator)!;
        var gate = pool.GetType().GetField("_gate", flags)!.GetValue(pool)!;
        var workers = (IDictionary)typeof(RuntimeStateReplicator).GetField("_workers", flags)!.GetValue(replicator)!;
        var count = workers.Count;
        var depths = new Dictionary<string, int>();
        int Entries(object queue)
        {
            var budget = queue.GetType().GetField("_budget", flags)!.GetValue(queue)!;
            return (int)budget.GetType().GetProperty("Entries", flags)!.GetValue(budget)!;
        }
        // Charges include staging/in-flight leases; atomic budget counters can change during this bounded sample.
        lock (gate)
        {
            if (count == 19)
                foreach (DictionaryEntry pair in workers)
                {
                    var queue = pair.Value!.GetType().GetProperty("Queue")!.GetValue(pair.Value)!;
                    depths[(string)pair.Key] = Entries(queue);
                }
            var snapshot = typeof(RuntimeStateReplicator).GetProperty("QueueStatus", flags)!.GetValue(replicator)!;
            var json = JsonSerializer.SerializeToElement(snapshot, Json);
            return new(Entries(global), count, depths,
                json.GetProperty("entries").GetInt32(), json.GetProperty("bytes").GetInt64(),
                json.GetProperty("ordinaryEntries").GetInt32(), json.GetProperty("ordinaryBytes").GetInt64(),
                json.GetProperty("capacityDropped").GetInt64(), json.GetProperty("contentionDropped").GetInt64());
        }
    }

    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-transport-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        void Record(string message)
        { var line = $"{DateTimeOffset.UtcNow:O} {message}"; Console.WriteLine(line); log.WriteLine(line); }
        Record($"BEGIN root={root} OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription} runtime={Environment.Version}");
        var children = new List<Child>();
        async Task<Child> Start(string mode, string directory, Settings? settings = null)
        {
            Directory.CreateDirectory(directory);
            if (settings is not null) await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, Json));
            var info = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, mode, directory }) info.ArgumentList.Add(arg);
            var child = new Child(Process.Start(info)!, directory);
            children.Add(child);
            Record($"START mode={mode} id={settings?.Id ?? "tls-client"} ownedPid={child.Process.Id} directory={directory}");
            if (mode != "tls-client")
            {
                await Until(() => Task.FromResult(File.Exists(Path.Combine(directory, "ready.json"))), "ready", 30);
                using var ready = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "ready.json")));
                Check(ready.RootElement.GetProperty("pid").GetInt32() == child.Process.Id, "PID identity");
                child.Address = ready.RootElement.GetProperty("address").GetString()!;
                Record($"READY pid={child.Process.Id} address={child.Address}");
            }
            return child;
        }
        async Task Stop(Child child)
        {
            if (!child.Process.HasExited)
            {
                child.Process.Kill();
                await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Record($"CLEANUP killed ownedPid={child.Process.Id}");
            }
            await child.Logs;
        }
        try
        {
            var tls = Path.Combine(root, "tls");
            Directory.CreateDirectory(tls);
            using var ca = Authority("Harness trusted root");
            using var other = Authority("Harness untrusted root");
            await File.WriteAllBytesAsync(Path.Combine(tls, "root.cer"), ca.Export(X509ContentType.Cert));
            var endpoints = new Dictionary<string, string>();
            var tlsChildren = new List<Child>();
            foreach (var id in new[] { "trusted", "untrusted", "wrong-name" })
            {
                using var leaf = ServerCertificate(id == "untrusted" ? other : ca, id == "wrong-name" ? "wrong.invalid" : "localhost");
                var path = Path.Combine(tls, id + ".pfx");
                var password = Guid.NewGuid().ToString("N");
                await File.WriteAllBytesAsync(path, leaf.Export(X509ContentType.Pfx, password));
                var child = await Start("tls-node", Path.Combine(tls, id), new(id, true, path, password, false,
                    new() { ["tls-client"] = "https://localhost" }));
                tlsChildren.Add(child);
                endpoints[id] = child.Address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
                Record($"CERT id={id} sha256={leaf.GetCertHashString(HashAlgorithmName.SHA256)} issuer={leaf.Issuer}");
            }
            var clientRoot = Path.Combine(tls, "client");
            Directory.CreateDirectory(clientRoot);
            await File.WriteAllTextAsync(Path.Combine(clientRoot, "tls.json"), JsonSerializer.Serialize(new TlsSettings(Path.Combine(tls, "root.cer"), endpoints), Json));
            var client = await Start("tls-client", clientRoot);
            await client.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            await client.Logs;
            Check(client.Process.ExitCode == 0, "TLS client assertions; inspect child log");
            Record("PASS TLS " + await File.ReadAllTextAsync(Path.Combine(clientRoot, "results.json")));
            foreach (var denied in tlsChildren.Skip(1))
            {
                var journal = Path.Combine(denied.Directory, "runtime", "runtime-state.jsonl");
                Check(!File.Exists(journal) || new FileInfo(journal).Length == 0, "TLS-rejected requests must not mutate node state");
            }
            foreach (var child in tlsChildren) await Stop(child);

            var scale = Path.Combine(root, "scale");
            Directory.CreateDirectory(scale);
            var sinks = new List<Child>();
            var peers = new Dictionary<string, string>();
            for (var i = 1; i < 20; i++)
            {
                var id = $"n{i:00}";
                var sink = await Start("scale-node", Path.Combine(scale, id), new(id, false, null, null, i > 1,
                    new() { ["n00"] = "http://127.0.0.1" }));
                sinks.Add(sink); peers[id] = sink.Address;
            }
            var source = await Start("scale-node", Path.Combine(scale, "n00"), new("n00", false, null, null, false, peers));
            Check(sinks.All(s => !s.Process.HasExited) && !source.Process.HasExited, "20 simultaneously live node processes");
            var window = DateTimeOffset.UtcNow;
            async Task Add(int count)
            { using var response = await Http.PostAsJsonAsync(source.Address + "/test/load", new Load(window, count)); response.EnsureSuccessStatusCode(); }
            async Task<Sample> SampleNode(Child node) => (await Http.GetFromJsonAsync<Sample>(node.Address + "/test/state", Json))!;
            await Add(1);
            await Until(async () => (await SampleNode(source)).Queues.Workers == 19 && (await SampleNode(sinks[0])).Value == 1, "19 workers and healthy delivery", 15);
            foreach (var sink in sinks.Skip(1))
                await Until(async () => (await SampleNode(sink)).BlockedRequests > 0, "slow peer has an actual blocked HTTP request", 10);
            var baseline = await SampleNode(source);
            var samples = new List<Sample> { baseline };
            var elapsed = Stopwatch.StartNew();
            for (var round = 0; round < 10; round++)
            {
                await Add(1000);
                await Task.Delay(100);
                var sample = await SampleNode(source);
                samples.Add(sample);
                Check(sample.Queues.Global <= 50 && sample.Queues.Peers.Count == 19 && sample.Queues.Peers.Values.All(depth => depth <= 50), "bounded ordinary replication queues including leases");
                Check(sample.Queues.Entries <= 2000 && sample.Queues.Bytes <= 2 * 1024 * 1024
                    && sample.Queues.OrdinaryEntries <= 1000 && sample.Queues.OrdinaryBytes <= 1024 * 1024,
                    "global entry/serialized-byte budgets including in-flight leases");
                Check(sample.Status.QueueDepth <= 100 && sample.Status.ActiveEntries == 1, "bounded journal queue and one active key");
                Check(sample.WorkingSet < 1024L * 1024 * 1024, "source sampled working set under 1 GiB experiment limit");
            }
            elapsed.Stop();
            var loaded = await SampleNode(source);
            Check(loaded.Value == 10001 && loaded.Dropped > 0 && loaded.Queues.CapacityDropped > 0 && samples.Any(s => s.Queues.Peers.Values.Any(depth => depth == 50)),
                "finite load saturated bounded peer queues and dropped updates");
            await Until(async () => (await SampleNode(sinks[0])).Value > 1, "healthy peer progresses while others blocked", 10);
            var healthy = await SampleNode(sinks[0]);
            var totalWorkingSet = sinks.Append(source).Sum(c => { c.Process.Refresh(); return c.Process.WorkingSet64; });
            Check(totalWorkingSet < 4L * 1024 * 1024 * 1024, "20-process working-set snapshot under 4 GiB experiment limit");
            Record($"PASS SCALE liveNodes=20 fanout=19 slow=18 healthy=1 writes=10001 loadMs={elapsed.ElapsedMilliseconds} queueCapacity=100 ordinaryPerQueueLimit=50 maxPeerCharges={samples.Max(s => s.Queues.Peers.Values.Max())} maxPoolCharges={samples.Max(s => s.Queues.Entries)} maxPoolBytes={samples.Max(s => s.Queues.Bytes)} poolEntryLimit=2000 poolByteLimit=2097152 dropped={loaded.Dropped} capacityDropped={loaded.Queues.CapacityDropped} journalDropped={loaded.Status.DroppedMutations} healthyValue={healthy.Value} sourcePeakSampleBytes={samples.Max(s => s.WorkingSet)} aggregateWorkingSetSnapshotBytes={totalWorkingSet}");
            foreach (var sink in sinks.Skip(1))
            { using var response = await Http.PostAsync(sink.Address + "/test/release", null); response.EnsureSuccessStatusCode(); }
            var releaseTime = Stopwatch.StartNew();
            await Until(async () =>
            {
                foreach (var sink in sinks) if ((await SampleNode(sink)).Value != 10001) return false;
                return true;
            }, "all 19 real receivers converge through anti-entropy", 80);
            var final = new List<Sample>();
            foreach (var node in sinks.Append(source)) final.Add(await SampleNode(node));
            await File.WriteAllTextAsync(Path.Combine(scale, "capacity-results.json"), JsonSerializer.Serialize(new
            { topology = "one source, nineteen receivers; not full mesh", queueCapacity = 100, ordinaryPerQueueLimit = 50,
                poolEntryLimit = 2000, poolByteLimit = 2097152, samples, final,
                totalWorkingSet, loadMs = elapsed.ElapsedMilliseconds, recoveryMs = releaseTime.ElapsedMilliseconds }, Json));
            Check(final.All(s => s.Value == 10001 && s.Status.ActiveEntries == 1), "20-node final values");
            Check(final.Single(s => s.Id == "n00").AuthorizationRejected == 0, "source explicit Send grants");
            Check(final.Where(s => s.Id != "n00").All(s => s.Queues.Workers == 0), "receive-only peers never start reverse workers");
            Record($"PASS SCALE recoveryMs={releaseTime.ElapsedMilliseconds} nodes=20 allCounters=10001 activeKeysPerNode=1 sourceAuthorizationRejected=0 receiveOnlyOutboundDenials={final.Where(s => s.Id != "n00").Sum(s => s.AuthorizationRejected)}");
            Record("RESULT PASS TLS and finite 20-node star capacity experiment (current Windows host only)");
        }
        catch (Exception ex) { Record("RESULT FAIL " + ex); throw; }
        finally
        {
            foreach (var child in children) { await Stop(child); child.Process.Dispose(); }
            Record("END all owned children exited; artifacts retained; system certificate stores unchanged");
        }
    }

    static X509Certificate2 Authority(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }
    static X509Certificate2 ServerCertificate(X509Certificate2 authority, string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        using var publicCertificate = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        return publicCertificate.CopyWithPrivateKey(key);
    }
    static async Task TlsClient(string root)
    {
        var settings = JsonSerializer.Deserialize<TlsSettings>(await File.ReadAllTextAsync(Path.Combine(root, "tls.json")), Json)!;
        using var authority = X509CertificateLoader.LoadCertificateFromFile(settings.RootCertificate);
        using var transport = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        transport.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust, VerificationFlags = X509VerificationFlags.NoFlag,
            RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true
        };
        transport.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(authority);
        transport.SslOptions.CertificateChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        // No validation callback: the platform validates chain, validity, EKU and hostname. The isolated CA has no CRL.
        Check(transport.SslOptions.RemoteCertificateValidationCallback is null, "no certificate validation bypass");
        using var http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(10) };
        var results = new Dictionary<string, string>();
        foreach (var id in new[] { "trusted", "untrusted", "wrong-name" })
        {
            var now = DateTimeOffset.UtcNow;
            var mutation = new RuntimeStateMutation("process.tls", "shared|tls", "tls-client", 1, now, now.AddMinutes(10), false,
                null, new Dictionary<string, long> { ["tls-client"] = 7 });
            var message = new NodeMessage(2, "transport-test", "tls-client", id, Guid.NewGuid().ToString("D"), "tls-test", "runtime.merge",
                now, now.AddMinutes(5), JsonSerializer.SerializeToElement(mutation, Json));
            var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoints[id] + NodeMessageAuthentication.Prefix + message.Type)
            { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            NodeMessageAuthentication.Sign(request, message, "test", Secret, body);
            try
            {
                using var response = await http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                Check(id == "trusted", "untrusted or hostname-mismatched certificate must fail TLS");
                var sample = (await http.GetFromJsonAsync<Sample>(settings.Endpoints[id] + "/test/state", Json))!;
                Check(sample.Value == 7, "trusted TLS request really merged signed mutation");
                results[id] = "HTTP 200; real signed runtime.merge counter=7";
            }
            catch (HttpRequestException ex) when (id != "trusted" && ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            { results[id] = ex.HttpRequestError + ": " + ex.InnerException?.Message; }
        }
        using var defaultClient = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var unexpected = await defaultClient.GetAsync(settings.Endpoints["trusted"] + "/test/state");
            throw new InvalidOperationException("Without isolated root trust the self-signed CA must not be accepted");
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        { results["no-custom-trust"] = ex.HttpRequestError + ": " + ex.InnerException?.Message; }
        Check(results.Count == 4, "all TLS positive and negative assertions recorded");
        await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, Json));
    }

    static async Task Until(Func<Task<bool>> predicate, string name, int seconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds)) { if (await predicate()) return; await Task.Delay(200); }
        throw new TimeoutException(name);
    }
    static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
    sealed record Settings(string Id, bool Https, string? Certificate, string? Password, bool Blocked, Dictionary<string, string> Peers);
    sealed record TlsSettings(string RootCertificate, Dictionary<string, string> Endpoints);
    public sealed record Load(DateTimeOffset Window, int Count);
    public sealed record QueueSample(int Global, int Workers, Dictionary<string, int> Peers,
        int Entries, long Bytes, int OrdinaryEntries, long OrdinaryBytes, long CapacityDropped, long ContentionDropped);
    public sealed record Sample(string Id, long Value, RuntimeStateStatus Status, long Dropped, long AuthorizationRejected,
        QueueSample Queues, long BlockedRequests, long ManagedBytes, long WorkingSet);
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
        { Process = process; Directory = directory; Logs = Task.WhenAll(Copy(process.StandardOutput, "stdout"), Copy(process.StandardError, "stderr")); }
        async Task Copy(StreamReader reader, string name)
        {
            using var writer = new StreamWriter(Path.Combine(Directory, $"{Process.Id}.{name}.log")) { AutoFlush = true };
            while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
        }
    }
}
