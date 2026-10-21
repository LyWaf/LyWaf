using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;

static class V7ProcessHarness
{
    const string Secret = "isolated-config-process-key-never-use-in-production-1234";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-v7-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var log = new StreamWriter(Path.Combine(root, "execution.log")) { AutoFlush = true };
        void Record(string value) { Console.WriteLine(value); log.WriteLine($"{DateTimeOffset.UtcNow:O} {value}"); }
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, "configuration-child", "node", root, "18937", "--v7" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Record($"START root={root} ownedPid={process.Id}");
        try
        {
            var ready = Path.Combine(root, "ready.json");
            for (var i = 0; i < 150 && !File.Exists(ready) && !process.HasExited; i++) await Task.Delay(200);
            var address = JsonDocument.Parse(await File.ReadAllTextAsync(ready)).RootElement.GetProperty("address").GetString()!;
            var uri = new Uri(address);
            async Task<string> Send(string label, string target, string key = "test", string? forwarded = null)
            {
                var message = new NodeMessage(2, "config-process-test", "controller", "node", Guid.NewGuid().ToString("D"),
                    "v7-probe", "node.status", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), JsonSerializer.SerializeToElement(new { }));
                var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
                using var signed = new HttpRequestMessage(HttpMethod.Post, address + NodeMessageAuthentication.Prefix + "node.status");
                NodeMessageAuthentication.Sign(signed, message, key, key == "old" ? Secret + "old" : Secret, body);
                var headers = new StringBuilder($"POST {target} HTTP/1.1\r\nHost: 127.0.0.1:18937\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n");
                foreach (var header in signed.Headers) headers.Append(header.Key).Append(": ").Append(string.Join(",", header.Value)).Append("\r\n");
                if (forwarded is not null) headers.Append("X-Original-URL: ").Append(forwarded).Append("\r\n");
                headers.Append("\r\n");
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(uri.Host, uri.Port);
                var stream = tcp.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()));
                await stream.WriteAsync(body);
                using var reader = new StreamReader(stream);
                var response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await File.WriteAllTextAsync(Path.Combine(root, label + ".http"), headers + Encoding.UTF8.GetString(body) + "\n\n" + response);
                Record(label + " " + response.Split('\n')[0].Trim());
                return response;
            }
            var path = NodeMessageAuthentication.Prefix + "node.status";
            var baseline = await Send("canonical", path);
            if (!baseline.StartsWith("HTTP/1.1 200") || !baseline.Contains("\"state\":") || !baseline.Contains("\"traceId\":")) throw new Exception("Baseline or original top-level response fields missing");
            if (!(await Send("encoded-unreserved", path.Replace("node.status", "%6eode.status"))).Contains("node_invalid_path")) throw new Exception("Encoded alias accepted");
            if (!(await Send("encoded-slash", path.Replace("node-agent/", "node-agent%2F"))).StartsWith("HTTP/1.1 404")) throw new Exception("Encoded slash accepted");
            if (!(await Send("duplicate-slash", path.Replace("node-agent/", "node-agent//"))).StartsWith("HTTP/1.1 404")) throw new Exception("Duplicate slash accepted");
            if (!(await Send("query", path + "?x=1")).Contains("node_invalid_path")) throw new Exception("Query accepted");
            await Send("rewrite-original-header", path, forwarded: "/public/rewritten");
            if (!(await Send("rotation-overlap-old", path, "old")).StartsWith("HTTP/1.1 200")) throw new Exception("Old overlap key failed");
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            (await http.PostAsync(address + "/test/revoke", null)).EnsureSuccessStatusCode();
            if (!(await Send("rotation-revoked-old", path, "old")).Contains("node_unknown_identity")) throw new Exception("Revoked key accepted");
            if (!(await Send("rotation-current", path)).StartsWith("HTTP/1.1 200")) throw new Exception("Current key failed");
            async Task<JsonElement> Business(string operation, object payload, string deployment, bool legacy)
            {
                var message = new NodeMessage(2, "config-process-test", "controller", "node", Guid.NewGuid().ToString("D"), deployment,
                    "configuration." + operation, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), JsonSerializer.SerializeToElement(payload, Json));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(legacy ? payload : (object)message, Json);
                using var request = new HttpRequestMessage(HttpMethod.Post, address + (legacy ? "/api/distribution/agent/" + operation : NodeMessageAuthentication.Prefix + message.Type));
                request.Headers.Host = "127.0.0.1:18937";
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new("application/json");
                if (legacy) DistributionAuthentication.Sign(request, operation, Secret, bytes);
                else NodeMessageAuthentication.Sign(request, message, "test", Secret, bytes);
                using var response = await http.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                await File.AppendAllTextAsync(Path.Combine(root, "business.jsonl"), JsonSerializer.Serialize(new { operation, legacy, deployment, status = (int)response.StatusCode, body = text }) + "\n");
                var result = JsonDocument.Parse(text).RootElement.Clone();
                if (!response.IsSuccessStatusCode || !result.GetProperty("ack").GetBoolean()) throw new Exception("Business ACK failed: " + text);
                if (!legacy && (!result.TryGetProperty("stage", out _) || !result.TryGetProperty("configVersionId", out _) || !result.TryGetProperty("traceId", out _))) throw new Exception("V2 top-level fields lost");
                return result;
            }
            foreach (var legacyFirst in new[] { true, false })
            {
                var deployment = "v7-" + Guid.NewGuid().ToString("N");
                var content = $"SimpleRes:\n  marker:\n    Content: {deployment}\nNodeLocal:\n  Identity: node\n";
                var prepare = new PrepareConfigurationRequest(deployment, deployment, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(), content);
                await Business("prepare", prepare, deployment, legacyFirst);
                await Business("prepare", prepare, deployment, !legacyFirst);
                var first = await Business("commit", new DistributionCommandRequest(deployment), deployment, legacyFirst);
                var duplicate = await Business("commit", new DistributionCommandRequest(deployment), deployment, !legacyFirst);
                var stateText = await http.GetStringAsync(address + "/test/node");
                await File.WriteAllTextAsync(Path.Combine(root, $"business-{legacyFirst}-state.json"), stateText);
                var state = JsonDocument.Parse(stateText).RootElement;
                var applied = state.GetProperty("versions").EnumerateArray().Count(x => x.GetProperty("action").GetString() == "distribution:" + deployment);
                if (applied != 1 || first.GetProperty("configVersionId").GetString() != duplicate.GetProperty("configVersionId").GetString()) throw new Exception("Duplicate business execution");
                Record($"PASS business legacyFirst={legacyFirst} prepare/commit via both protocols; appliedVersions=1");
            }
            // A real TCP proxy rewrites only the request line, preserving signed headers/body.
            foreach (var rewriteTo in new[] { path, path.Replace("node.status", "node.handshake") })
            {
                var message = new NodeMessage(2, "config-process-test", "controller", "node", Guid.NewGuid().ToString("D"), "rewrite", "node.status", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), JsonSerializer.SerializeToElement(new { }));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var relay = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    using var input = new MemoryStream();
                    var one = new byte[1];
                    while (true)
                    {
                        if (await stream.ReadAsync(one) == 0) throw new IOException("Proxy EOF");
                        input.WriteByte(one[0]);
                        if (input.Length >= 4 && Encoding.ASCII.GetString(input.GetBuffer(), (int)input.Length - 4, 4) == "\r\n\r\n") break;
                    }
                    var headers = Encoding.ASCII.GetString(input.ToArray());
                    var payload = new byte[bytes.Length];
                    await stream.ReadExactlyAsync(payload);
                    using var upstream = new TcpClient();
                    await upstream.ConnectAsync(uri.Host, uri.Port);
                    await upstream.GetStream().WriteAsync(Encoding.ASCII.GetBytes("POST " + rewriteTo + " HTTP/1.1" + headers[headers.IndexOf("\r\n", StringComparison.Ordinal)..]));
                    await upstream.GetStream().WriteAsync(payload);
                    await upstream.GetStream().CopyToAsync(stream);
                });
                using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/public/status");
                request.Headers.Host = "127.0.0.1:18937";
                request.Headers.ConnectionClose = true;
                request.Content = new ByteArrayContent(bytes);
                NodeMessageAuthentication.Sign(request, message, "test", Secret, bytes);
                using var response = await http.SendAsync(request);
                var bodyText = await response.Content.ReadAsStringAsync();
                await relay.WaitAsync(TimeSpan.FromSeconds(10));
                var expected = rewriteTo == path ? 200 : 401;
                await File.AppendAllTextAsync(Path.Combine(root, "proxy.jsonl"), JsonSerializer.Serialize(new { external = "/public/status", rewriteTo, status = (int)response.StatusCode, body = bodyText }) + "\n");
                if ((int)response.StatusCode != expected) throw new Exception("Unexpected proxy rewrite outcome");
                Record($"PASS real proxy rewrite to={rewriteTo} status={expected}");
            }
            Record("RESULT PASS rotation/business duplicate/proxy rewrite; rawtarget alias behavior recorded separately");
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "stdout.log"), await stdout);
            await File.WriteAllTextAsync(Path.Combine(root, "stderr.log"), await stderr);
            Record($"CLEAN ownedPid={process.Id} exit={process.ExitCode}");
        }
    }
}
