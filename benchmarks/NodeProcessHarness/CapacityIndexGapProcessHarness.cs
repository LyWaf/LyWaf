using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;

static partial class CapacityProcessHarness
{
    static void ConfigureIndexGapHost(WebApplication app, string root)
    {
        app.MapPost("/test/hold-index", (ConfigVersionService versions) =>
        {
            typeof(ConfigVersionService).GetProperty("MainFileReplacedBeforeIndex", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(versions, (Action<string>)(action =>
                {
                    File.WriteAllText(Path.Combine(root, "index-held"), action);
                    using var hold = new ManualResetEventSlim();
                    if (!hold.Wait(TimeSpan.FromMinutes(2))) throw new TimeoutException("Owner did not kill child");
                }));
            return Results.Ok();
        });
    }

    public static async Task RunIndexGaps(string linuxExecutable)
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-index-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        void Record(string line) { Console.WriteLine(line); log.WriteLine($"{DateTimeOffset.UtcNow:O} {line}"); }
        Record("ROOT " + root);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Host = "localhost:18937";
        async Task<JsonElement> Send(Child child, NodeMessage message, int status)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            using var request = new HttpRequestMessage(HttpMethod.Post, child.Address + NodeMessageAuthentication.Prefix + message.Type)
                { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            NodeMessageAuthentication.Sign(request, message, "test", Secret, bytes);
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Record($"HTTP {message.Type} {(int)response.StatusCode} {body}");
            Check((int)response.StatusCode == status, "HTTP status");
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        foreach (var linux in new[] { false, true })
        foreach (var stage in new[] { "commit", "rollback" })
        {
            var directory = Path.Combine(root, (linux ? "linux-" : "windows-") + stage);
            Directory.CreateDirectory(directory);
            Child? child = null;
            try
            {
                child = await Start(directory, Record, linux ? linuxExecutable : null);
                const string content = "SpeedLimit:\n  Value: new\n";
                await Send(child, Message("configuration.prepare", new PrepareConfigurationRequest("deployment", "v",
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))), content)), 200);
                if (stage == "rollback") await Send(child, Message("configuration.commit", new DistributionCommandRequest("deployment")), 200);
                using (var arm = await http.PostAsync(child.Address + "/test/hold-index", null)) arm.EnsureSuccessStatusCode();
                var command = Message("configuration." + stage, new DistributionCommandRequest("deployment"));
                var sending = Send(child, command, 200);
                await Until(() => File.Exists(Path.Combine(directory, "index-held")));
                Check(!sending.IsCompleted, "no ACK in main/index gap");
                Check(File.ReadAllText(Path.Combine(directory, "config.yaml")) == (stage == "commit" ? content : Original), "main file already replaced");
                Record("HOLD " + File.ReadAllText(Path.Combine(directory, "index-held")));
                child.Kill();
                try { await sending; throw new InvalidOperationException("Killed operation ACKed"); }
                catch (HttpRequestException) { }
                await child.DisposeAsync(); child = await Start(directory, Record, linux ? linuxExecutable : null);
                var before = Evidence(directory);
                for (var i = 0; i < 2; i++)
                {
                    var result = await Send(child, Message("configuration.result", command), 409);
                    Check(result.GetProperty("errorCode").GetString() == "node_outcome_unknown", "missing index proof stays Unknown");
                }
                Check(Evidence(directory) == before, "queries preserve prepared/archive/pin evidence without new effects");
                Check(File.ReadAllText(Path.Combine(directory, "config.yaml")) == (stage == "commit" ? content : Original), "queries preserve replaced main");
                Record($"PASS {(linux ? "linux" : "windows")} {stage}: main replaced/index absent, no ACK, Unknown after restart, repeated queries read-only");
            }
            finally { if (child is not null) await child.DisposeAsync(); }
        }
        Record("RESULT PASS 4/4 owned-process main/index kill windows");
    }
}
