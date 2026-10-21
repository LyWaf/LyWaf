using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;

static class ReloadProcessHarness
{
    const string Secret = "isolated-config-process-key-never-use-in-production-1234";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-reload-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        void Record(string value) { Console.WriteLine(value); log.WriteLine($"{DateTimeOffset.UtcNow:O} {value}"); }
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        var failures = new List<string>();
        NodePeerOptions Peer(string state, params string[] operations) => new() { Role = "Controller", AdmissionState = state,
            ActiveKeyId = "old", Keys = new() { ["old"] = Secret }, AllowedOperations = operations.ToList() };
        var names = new[] { "rotation", "readonly", "reload-conflict", "active-writer", "draining-rollback", "draining-prepare", "pending", "revoked", "disabled" };
        foreach (var name in names)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            var config = new NodeCommunicationOptions { Enabled = true, Role = "Node", ClusterId = "config-process-test", NodeId = "node",
                RequireHttps = false, AllowLegacyProtocol = false, StateDirectory = Path.Combine(dir, "protocol"),
                Peers = new() { ["controller"] = Peer("Active", "node.status", "configuration.prepare", "configuration.commit", "configuration.rollback") } };
            if (name != "rotation")
            {
                config.Peers["second"] = name switch
                {
                    "readonly" or "reload-conflict" => Peer("Active", "node.status"),
                    "active-writer" => Peer("Active", "node.status", "configuration.commit"),
                    "draining-rollback" => Peer("Draining", "node.status", "configuration.rollback"),
                    "draining-prepare" => Peer("Draining", "node.status", "configuration.prepare"),
                    "pending" => Peer("PendingApproval", "configuration.commit"),
                    "revoked" => Peer("Revoked", "configuration.commit"),
                    _ => Peer("Active", "configuration.commit")
                };
                if (name == "disabled") config.Peers["second"].Enabled = false;
            }
            async Task Write(string stage)
            {
                // JSON is valid YAML and is parsed by the production draft-aware YAML provider.
                var text = JsonSerializer.Serialize(new { NodeCommunication = config }, Json);
                await File.WriteAllTextAsync(Path.Combine(dir, stage + ".json"), text);
                var path = Path.Combine(dir, "protocol.yaml");
                await File.WriteAllTextAsync(path + ".tmp", text);
                File.Move(path + ".tmp", path, true);
            }
            await Write("initial");
            for (var generation = 0; generation < (name == "rotation" ? 2 : 1); generation++)
            {
                var ready = Path.Combine(dir, "ready.json");
                File.Delete(ready);
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
                foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "configuration-child", "node", dir, "18937", "--reload" }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                Record($"START case={name} generation={generation} ownedPid={process.Id} root={dir}");
                try
                {
                    for (var i = 0; i < 150 && !File.Exists(ready) && !process.HasExited; i++) await Task.Delay(100);
                    if (name is "active-writer" or "draining-rollback")
                    {
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        if (process.ExitCode == 0 || !(await stderr).Contains("Only one admitted configuration writer")) throw new Exception("Startup did not reject multiple writers");
                        Record("PASS " + name + " production startup validator rejected");
                        continue;
                    }
                    var address = JsonDocument.Parse(await File.ReadAllTextAsync(ready)).RootElement.GetProperty("address").GetString()!;
                    async Task Send(string stage, string key, int status, string sender = "controller", string operation = "node.status")
                    {
                        var message = new NodeMessage(2, "config-process-test", sender, "node", Guid.NewGuid().ToString("D"), "never-prepared", operation,
                            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), JsonSerializer.SerializeToElement(new { deploymentId = "never-prepared" }));
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
                        using var request = new HttpRequestMessage(HttpMethod.Post, address + NodeMessageAuthentication.Prefix + operation);
                        request.Headers.Host = "127.0.0.1:18937";
                        request.Content = new ByteArrayContent(bytes);
                        NodeMessageAuthentication.Sign(request, message, key, key == "new" ? Secret + "new" : Secret, bytes);
                        using var response = await http.SendAsync(request);
                        var body = await response.Content.ReadAsStringAsync();
                        await File.AppendAllTextAsync(Path.Combine(dir, "wire.jsonl"), JsonSerializer.Serialize(new { generation, stage, sender, operation, key, status = (int)response.StatusCode, body }) + "\n");
                        if ((int)response.StatusCode != status) throw new Exception($"{stage}: expected {status}, actual {(int)response.StatusCode}: {body}");
                        Record($"PASS {name}/{stage} sender={sender} operation={operation} key={key} status={status}");
                    }
                    async Task Reload(string stage)
                    {
                        await Write(stage);
                        using var response = await http.PostAsync(address + "/test/reload", null);
                        var body = await response.Content.ReadAsStringAsync();
                        await File.WriteAllTextAsync(Path.Combine(dir, stage + "-reload.txt"), (int)response.StatusCode + "\n" + body);
                        response.EnsureSuccessStatusCode();
                    }
                    if (name == "rotation")
                    {
                        if (generation == 0)
                        {
                            await Send("initial-old", "old", 200);
                            await Send("before-add-new", "new", 401);
                            config.Peers["controller"].Keys["new"] = Secret + "new";
                            await Reload("overlap");
                            await Send("overlap-old", "old", 200);
                            await Send("overlap-new", "new", 200);
                            config.Peers["controller"].ActiveKeyId = "new";
                            config.Peers["controller"].Keys.Remove("old");
                            await Reload("revoked");
                        }
                        await Send("revoked-old", "old", 401);
                        await Send("current-new", "new", 200);
                    }
                    else
                    {
                        await Send("primary-read", "old", 200);
                        await Send("primary-write-reaches-business-nack", "old", 409, "controller", "configuration.commit");
                        if (name == "readonly")
                        {
                            await Send("second-read", "old", 200, "second");
                            await Send("second-write-denied", "old", 403, "second", "configuration.commit");
                        }
                        if (name == "draining-prepare") await Send("second-prepare-denied", "old", 403, "second", "configuration.prepare");
                        if (name is "pending" or "revoked" or "disabled")
                            await Send("second-write-denied", "old", name == "disabled" ? 401 : 403, "second", "configuration.commit");
                        if (name == "reload-conflict")
                        {
                            config.Peers["second"].AllowedOperations.Add("configuration.commit");
                            await Write("conflicting-writers");
                            using var rejected = await http.PostAsync(address + "/test/reload", null);
                            await File.WriteAllTextAsync(Path.Combine(dir, "conflicting-reload.txt"), (int)rejected.StatusCode + "\n" + await rejected.Content.ReadAsStringAsync());
                            if (rejected.IsSuccessStatusCode) throw new Exception("Invalid writer reload unexpectedly accepted");
                            Record($"OBSERVATION invalid writer reload HTTP {(int)rejected.StatusCode}");
                            await Send("after-invalid-reload-write", "old", 409, "controller", "configuration.commit");
                            await Send("after-invalid-reload-read", "old", 200);
                            await Send("after-invalid-reload-second-write-denied", "old", 403, "second", "configuration.commit");
                            config.Peers["second"].AllowedOperations.Remove("configuration.commit");
                            await Reload("restored-readonly");
                            await Send("restored-read", "old", 200);
                            await Send("restored-secondary-read", "old", 200, "second");
                            await Send("restored-secondary-write-denied", "old", 403, "second", "configuration.commit");
                            Record("PASS invalid writer rejected; previous valid options retained; valid reload recovered; second writer still forbidden");
                        }
                        Record("PASS startup accepted " + name);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(name + ": " + ex.Message);
                    Record("FAIL " + name + " " + ex);
                }
                finally
                {
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync();
                    await File.WriteAllTextAsync(Path.Combine(dir, $"{generation}-stdout.log"), await stdout);
                    await File.WriteAllTextAsync(Path.Combine(dir, $"{generation}-stderr.log"), await stderr);
                    Record($"CLEAN ownedPid={process.Id} exit={process.ExitCode}");
                }
            }
        }
        Record(failures.Count == 0 ? "RESULT PASS production reload/key persistence and startup writer matrix"
            : "RESULT FAIL " + string.Join(" | ", failures));
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }
}
