using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LyWaf.Middleware;
using LyWaf.Services.AntiBot;
using LyWaf.Services.Captcha;
using LyWaf.Services.Metrics;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;

if (args.Length > 0) { await Harness.Node(args); return; }
await Harness.Run();

static class Harness
{
    static void Check(bool success, string name)
    { if (!success) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
    static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    static async Task Stop(Process process)
    {
        if (!process.HasExited) process.Kill(true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
    public static async Task Node(string[] args)
    {
        var endpoints = JsonSerializer.Deserialize<string[]>(Environment.GetEnvironmentVariable("LYWAF_CAPTCHA_FIXTURE_ENDPOINTS")!)!;
        var secret = Environment.GetEnvironmentVariable("LYWAF_CAPTCHA_FIXTURE_KEY")!;
        var node = int.Parse(args[1]); var root = args[3];
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(endpoints[node]);
        var config = new Dictionary<string, string?>
        {
            ["CaptchaStore:Provider"] = "Authority", ["CaptchaStore:ClusterId"] = args[2],
            ["CaptchaStore:AuthorityNodeId"] = "node0", ["CaptchaStore:AuthorityEndpoint"] = endpoints[0],
            ["CaptchaStore:StateDirectory"] = Path.Combine(root, "node" + node, "captcha"),
            ["CaptchaStore:TimeoutMilliseconds"] = "10000",
            ["NodeCommunication:Enabled"] = "true", ["NodeCommunication:RequireHttps"] = "false",
            ["NodeCommunication:NodeId"] = "node" + node, ["NodeCommunication:ClusterId"] = args[2],
            ["NodeCommunication:StateDirectory"] = Path.Combine(root, "node" + node, "wire")
        };
        for (var i = 0; i < 3; i++) if (i != node)
        {
            var prefix = "NodeCommunication:Peers:node" + i;
            config[prefix + ":ActiveKeyId"] = "key"; config[prefix + ":Keys:key"] = secret;
            config[prefix + ":AllowedOperations:0"] = "captcha.store.issue";
            config[prefix + ":AllowedOperations:1"] = "captcha.store.take";
        }
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddOptions<NodeCommunicationOptions>().Bind(builder.Configuration.GetSection("NodeCommunication"));
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddOptions<RuntimeStateOptions>().Configure(o => { o.NodeId = "node" + node; o.StateDirectory = Path.Combine(root, "node" + node, "runtime"); });
        builder.Services.AddSingleton<IRuntimeStateStore, RuntimeStateStore>();
        builder.Services.AddOptions<MetricsOptions>(); builder.Services.AddSingleton<PrometheusMetrics>();
        builder.Services.AddLyWafCaptcha(builder.Configuration);
        builder.Services.AddOptions<AntiBotOptions>().Configure(o =>
        {
            o.Enabled = true; o.Mode = "Challenge"; o.ChallengeScore = 1; o.ProofLeadingZeroNibbles = 1;
            o.Secret = secret; o.ChallengePath = "/pow/verify";
        });
        builder.Services.AddSingleton<IAntiBotAnalyzer, AntiBotAnalyzer>();
        builder.Services.AddSingleton<IAntiBotChallengeService, AntiBotChallengeService>();
        var app = builder.Build(); app.UseMiddleware<CaptchaMiddleware>();
        app.UseWhen(c => c.Request.Path.StartsWithSegments("/pow"), branch => branch.UseMiddleware<AntiBotMiddleware>());
        app.MapNodeCommunication(new Uri(endpoints[node]).Port);
        app.MapGet("/ready", () => Results.Ok());
        await app.RunAsync();
    }
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-file-captcha-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Console.WriteLine("Evidence directory: " + root);
        var endpoints = new[] { Port(), Port(), Port() }.Select(p => "http://127.0.0.1:" + p).ToArray();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var cluster = Guid.NewGuid().ToString("N");
        var children = new List<Process>(); var logs = new List<Task>(); var checks = new List<string>();
        Process Start(int node)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "--node", node.ToString(), cluster, root }) start.ArgumentList.Add(arg);
            start.Environment["LYWAF_CAPTCHA_FIXTURE_ENDPOINTS"] = JsonSerializer.Serialize(endpoints);
            start.Environment["LYWAF_CAPTCHA_FIXTURE_KEY"] = secret;
            var process = Process.Start(start)!;
            var id = process.Id;
            logs.Add(Copy(process.StandardOutput, Path.Combine(root, $"node-{node}-{id}.stdout")));
            logs.Add(Copy(process.StandardError, Path.Combine(root, $"node-{node}-{id}.stderr")));
            children.Add(process); return process;
        }
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Host = "captcha.test:7030"; http.DefaultRequestHeaders.UserAgent.ParseAdd("HeadlessChrome/140");
        async Task Ready(int node)
        {
            for (var i = 0; i < 60; i++)
            {
                try { using var response = await http.GetAsync(endpoints[node] + "/ready"); if (response.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(200);
            }
            throw new InvalidOperationException("Node startup failed; inspect child logs");
        }
        var authorityRoot = Path.Combine(root, "node0", "captcha");
        async Task<(string Token, JsonElement Entry)> Issue(int node)
        {
            using var response = await http.GetAsync(endpoints[node] + "/__captcha/challenge");
            Check(response.IsSuccessStatusCode, "challenge issued");
            var challenge = await response.Content.ReadFromJsonAsync<JsonElement>(); var token = challenge.GetProperty("token").GetString()!;
            var path = Path.Combine(authorityRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) + ".json");
            using var file = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            return (token, file.RootElement.GetProperty("Entry").Clone());
        }
        async Task<HttpStatusCode> Verify(int node, (string Token, JsonElement Entry) challenge, bool wrong = false)
        {
            using var response = await http.PostAsJsonAsync(endpoints[node] + "/__captcha/verify", new
            {
                token = challenge.Token, answer = wrong ? "wrong" : challenge.Entry.GetProperty("Answer").GetString(),
                behavior = new { duration = 2000, mouseTrackCount = 4, sliderOffset = wrong ? -100 : challenge.Entry.GetProperty("SliderTarget").GetInt32() }
            });
            return response.StatusCode;
        }
        async Task<(string Token, long Counter)> Proof(int node)
        {
            using var response = await http.GetAsync(endpoints[node] + "/pow/start");
            Check(response.StatusCode == HttpStatusCode.Forbidden, "proof challenge issued");
            var html = await response.Content.ReadAsStringAsync();
            var token = JsonSerializer.Deserialize<string>(Regex.Match(html, "const token=(\"[^\"]+\")").Groups[1].Value)!;
            long counter = 0;
            while (!Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token + ":" + counter))).StartsWith('0')) counter++;
            return (token, counter);
        }
        async Task<HttpStatusCode> VerifyProof(int node, (string Token, long Counter) proof)
        {
            using var body = new StringContent(JsonSerializer.Serialize(new { token = proof.Token, counter = proof.Counter }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(endpoints[node] + "/pow/verify", body); return response.StatusCode;
        }
        try
        {
            var authority = Start(0); Start(1); Start(2);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(Ready));
            var first = await Issue(1);
            Check(await Verify(2, first) == HttpStatusCode.OK && await Verify(0, first) == HttpStatusCode.BadRequest,
                "remote node issues, another verifies, authority rejects replay"); checks.Add("cross-node single use");
            var race = await Issue(0);
            var results = await Task.WhenAll(Enumerable.Range(0, 96).Select(i => Verify(i % 3, race)));
            Console.WriteLine("Captcha statuses: " + string.Join(", ", results.GroupBy(s => s).Select(g => $"{(int)g.Key}={g.Count()}")));
            Check(results.Count(s => s == HttpStatusCode.OK) == 1 && results.Count(s => s == HttpStatusCode.BadRequest) == 95,
                "96 concurrent verifications succeed once"); checks.Add("96-way captcha race");
            var wrong = await Issue(1);
            Check(await Verify(2, wrong, true) == HttpStatusCode.BadRequest && await Verify(0, wrong) == HttpStatusCode.BadRequest,
                "wrong answer consumes challenge");
            var proof = await Proof(1);
            var proofs = await Task.WhenAll(Enumerable.Range(0, 48).Select(i => VerifyProof(i % 3, proof)));
            Check(proofs.Count(s => s == HttpStatusCode.OK) == 1 && proofs.Count(s => s == HttpStatusCode.Forbidden) == 47,
                "48 concurrent proof submissions succeed once"); checks.Add("48-way proof race");
            var pending = await Issue(1); var consumed = await Issue(2);
            Check(await Verify(1, consumed) == HttpStatusCode.OK, "consume before authority restart");
            using var oldCounter = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(authorityRoot, "sequence")));
            var sequence = oldCounter.RootElement.GetProperty("Sequence").GetInt64();
            await Stop(authority);
            Check(await Verify(1, pending) == HttpStatusCode.ServiceUnavailable, "authority outage fails closed");
            using (var unavailable = await http.GetAsync(endpoints[2] + "/__captcha/challenge"))
                Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "issuance unavailable without authority");
            Start(0); await Ready(0);
            Check(await Verify(2, pending) == HttpStatusCode.OK, "pending challenge survives authority SIGKILL");
            Check(await Verify(1, consumed) == HttpStatusCode.BadRequest && await VerifyProof(2, proof) == HttpStatusCode.Forbidden,
                "consumed captcha and proof stay consumed after restart");
            using var newCounter = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(authorityRoot, "sequence")));
            Check(newCounter.RootElement.GetProperty("Sequence").GetInt64() > sequence, "persistent sequence never reused after restart");
            checks.Add("authority crash/restart, outage, monotonic sequence");
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            { success = true, checks, nodePids = children.Select(p => p.Id),
                assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CaptchaService).Assembly.Location))) }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (var child in children) { await Stop(child); child.Dispose(); }
            await Task.WhenAll(logs);
            Console.WriteLine("All owned node processes stopped; no external store was used");
        }
    }
    private static async Task Copy(StreamReader reader, string path)
    { await File.WriteAllTextAsync(path, await reader.ReadToEndAsync()); }
}
