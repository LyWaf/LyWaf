using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LyWaf.Services.Metrics;
using LyWaf.Services.Retry;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using LyWaf.Services.WafInfo;
using LyWaf.Transformer;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

if (args.Length > 0) { await Harness.Child(args); return; }
await Harness.Verify();

static class Harness
{
    const string Secret = "tls-process-only-pairwise-key-at-least-32bytes";
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-cc-tls-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var owned = new List<Process>();
        var logs = new List<Task>();
        var evidence = new List<object>();
        Console.WriteLine("Evidence: " + root);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var pfx = Path.Combine(root, "ephemeral.pfx");
        await File.WriteAllBytesAsync(pfx, cert.Export(X509ContentType.Pfx));
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == cert.Thumbprint })
            { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            async Task<string> Start(string id, string downstream)
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
                foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, id, root, pfx, downstream }) start.ArgumentList.Add(arg);
                var process = Process.Start(start)!; owned.Add(process);
                logs.Add(Copy(process.StandardOutput, Path.Combine(root, id + ".stdout.log")));
                logs.Add(Copy(process.StandardError, Path.Combine(root, id + ".stderr.log")));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                var ready = Path.Combine(root, id + ".ready");
                while (!File.Exists(ready)) { Require(!process.HasExited, id + " exited before ready"); await Task.Delay(50, timeout.Token); }
                var url = await File.ReadAllTextAsync(ready, timeout.Token);
                var status = await client.GetFromJsonAsync<Status>(url + "/status", timeout.Token);
                Require(status!.Pid == process.Id, "Readiness PID mismatch");
                return url;
            }
            var c = await Start("c", ""); var b = await Start("b", c); var a = await Start("a", b);
            Require(owned.Select(p => p.Id).Distinct().Count() == 3, "Need three OS processes");
            for (var i = 1; i <= 3; i++)
            {
                using var external = new HttpRequestMessage(HttpMethod.Get, a + "/api");
                external.Headers.TryAddWithoutValidation(CcIngressOwnership.Header, "untrusted-client-marker");
                using var response = await client.SendAsync(external);
                Require(response.StatusCode == HttpStatusCode.OK, "YARP request failed");
                var sa = (await client.GetFromJsonAsync<Status>(a + "/status"))!;
                var sb = (await client.GetFromJsonAsync<Status>(b + "/status"))!;
                var sc = (await client.GetFromJsonAsync<Status>(c + "/status"))!;
                Require(sa.Count == i && sb.Count == 0 && sc.Count == 0, "Entry counted more than once");
                Require(sa.Deliveries == i && sb.Deliveries == i && sc.Deliveries == i * 3, "Expected exactly three backend attempts per client request");
                evidence.Add(new { Test = "retry", ClientRequests = i, A = sa, B = sb, C = sc });
                Console.WriteLine($"PASS client={i} entry={sa.Count} downstream={sb.Count}/{sc.Count} backendAttempts={sc.Deliveries}");
            }
            var baseline = (await client.GetFromJsonAsync<Status>(c + "/status"))!;
            Require(!string.IsNullOrEmpty(baseline.Marker), "Missing real forwarded marker");
            var validMarker = baseline.Marker;
            using (var control = new HttpRequestMessage(HttpMethod.Get, c + "/api"))
            {
                control.Headers.TryAddWithoutValidation(CcIngressOwnership.Header, validMarker);
                using var response = await client.SendAsync(control);
                var after = (await client.GetFromJsonAsync<Status>(c + "/status"))!;
                Require(after.Count == baseline.Count && after.Deliveries == baseline.Deliveries + 1,
                    "Valid marker control was not trusted");
                evidence.Add(new { Test = "valid-marker-control", Before = baseline.Count, After = after.Count });
                baseline = after;
                Console.WriteLine("PASS valid marker control suppresses downstream counting");
            }
            foreach (var fault in new[] { "expired", "wrong-source", "wrong-scope" })
            {
                var body = JsonNode.Parse(Convert.FromBase64String(validMarker!.Split('.')[0]))!;
                // Keep the real policy identity and produce a valid MAC so rejection is not just signature failure.
                body["ExpiresAt"] = DateTimeOffset.UtcNow.AddSeconds(10);
                if (fault == "expired") body["ExpiresAt"] = DateTimeOffset.UtcNow.AddSeconds(-1);
                if (fault == "wrong-source") body["Sender"] = "a"; // c authorizes b only.
                if (fault == "wrong-scope") body["Claims"]![0]!["Scope"] = "group:other";
                var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
                var marker = Convert.ToBase64String(bytes) + "." + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), bytes));
                using var message = new HttpRequestMessage(HttpMethod.Get, c + "/api");
                message.Headers.TryAddWithoutValidation(CcIngressOwnership.Header, marker);
                using var response = await client.SendAsync(message);
                var after = (await client.GetFromJsonAsync<Status>(c + "/status"))!;
                Require(after.Count == baseline.Count + 1 && after.Deliveries == baseline.Deliveries + 1, fault + " marker suppressed counting");
                evidence.Add(new { Test = fault, HttpStatus = (int)response.StatusCode, Before = baseline.Count, After = after.Count });
                baseline = after;
                Console.WriteLine("PASS untrusted " + fault + " counted locally");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (var process in owned) if (!process.HasExited) process.Kill(entireProcessTree: true);
            foreach (var process in owned) await process.WaitForExitAsync();
            await Task.WhenAll(logs);
            await File.WriteAllTextAsync(Path.Combine(root, "cleanup.json"), JsonSerializer.Serialize(owned.Select(p => new { p.Id, p.HasExited })));
            foreach (var process in owned) process.Dispose();
            File.Delete(pfx);
        }
    }
    static async Task Copy(StreamReader reader, string path) => await File.WriteAllTextAsync(path, await reader.ReadToEndAsync());

    public static async Task Child(string[] args)
    {
        var (id, root, pfx, next) = (args[0], args[1], args[2], args[3]);
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
        var runtime = new RuntimeStateOptions { Enabled = true, NodeId = id, StateDirectory = Path.Combine(root, id) };
        using var store = new RuntimeStateStore(new Monitor<RuntimeStateOptions>(runtime));
        var ingress = new CcIngressOptions { Enabled = true };
        CcIngressPeer Grant(string destination) => new() { Secret = Secret, DestinationAuthority = destination,
            RemoteAddresses = ["127.0.0.1"], RuleIds = ["tls-process"], Scopes = ["group:tls"] };
        if (id != "a") ingress.Peers[id == "b" ? "a" : "b"] = Grant("");
        if (next.Length > 0) ingress.Peers[id == "a" ? "b" : "c"] = Grant(next);
        var ownership = new CcIngressOwnership(new Monitor<CcIngressOptions>(ingress), new Monitor<RuntimeStateOptions>(runtime));
        var statistics = DispatchProxy.Create<IStatisticService, Rules>();
        ((Rules)(object)statistics).Rule = new() { Id = "tls-process", ProtectionGroup = "tls", Threshold = 1000, Period = 3600 };
        var checker = new CcRuleChecker(statistics, null!, store, ingress: ownership);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.UseHttps(cert)));
        builder.Services.AddSingleton(ownership);
        if (next.Length > 0)
        {
            builder.Services.AddSingleton<IWafInfoService>(new WafFixture());
            builder.Services.AddSingleton(new PrometheusMetrics(new Monitor<MetricsOptions>(new())));
            builder.Services.AddReverseProxy().LoadFromMemory(
                [new RouteConfig { RouteId = "api", ClusterId = "next", Match = new() { Path = "/api" } }],
                [new ClusterConfig { ClusterId = "next", Destinations = new Dictionary<string, DestinationConfig> { ["next"] = new() { Address = next } },
                    // Test-only ephemeral loopback certificate. No production configuration or system trust changes.
                    HttpClient = new() { DangerousAcceptAnyServerCertificate = true },
                    Metadata = new Dictionary<string, string> { ["Retry.Enabled"] = "true", ["Retry.MaxAttempts"] = "3",
                        ["Retry.BaseBackoffMs"] = "0", ["Retry.BudgetMinRetriesPerSecond"] = "100" } }]).AddTransforms<WafTrans>();
            builder.Services.AddSingleton<IForwarderHttpClientFactory, RetryForwarderHttpClientFactory>();
        }
        await using var app = builder.Build();
        int deliveries = 0; string? marker = null;
        app.Use(async (context, continuation) =>
        {
            if (context.Request.Path == "/api")
            {
                Interlocked.Increment(ref deliveries);
                marker = context.Request.Headers[CcIngressOwnership.Header].ToString();
                checker.CheckRequest(context, "192.0.2.1");
            }
            await continuation(context);
        });
        app.MapGet("/status", () => new Status(Environment.ProcessId, store.GetSnapshots("cc.access").Sum(s => s.CounterValue ?? 0), deliveries, marker));
        if (next.Length == 0) app.MapGet("/api", () => deliveries % 3 == 0 ? Results.Ok() : Results.StatusCode(503));
        else app.MapReverseProxy();
        await app.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(root, id + ".ready"), app.Urls.Single());
        await app.WaitForShutdownAsync();
    }
    public record Status(int Pid, long Count, int Deliveries, string? Marker);
    public class Rules : DispatchProxy
    {
        public AdvancedCcRule Rule = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IStatisticService.GetAdvancedCcRules)
            ? new List<AdvancedCcRule> { Rule } : throw new NotSupportedException();
    }
    sealed class Monitor<T>(T value) : IOptionsMonitor<T> { public T CurrentValue => value; public T Get(string? name) => value; public IDisposable? OnChange(Action<T, string?> listener) => null; }
    sealed class WafFixture : IWafInfoService
    {
        public WafInfoOptions GetOptions() => new();
        public X509Certificate2 GetCertByName(string name) => throw new NotSupportedException();
        public X509Certificate2? TryGetCertByName(string name) => null;
    }
}
