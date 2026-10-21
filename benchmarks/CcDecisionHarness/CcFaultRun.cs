using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

static class CcFaultRun
{
    public static async Task Run(string output, int period, int subjects = 1)
    {
        if (period is not (1 or 10)) throw new ArgumentOutOfRangeException(nameof(period));
        if (subjects is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(subjects));
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new IOException("Use a fresh evidence directory.");
        Directory.CreateDirectory(root);
        var binary = Assembly.GetExecutingAssembly().Location;
        var hashes = Directory.GetFiles(Path.GetDirectoryName(binary)!, "*.dll").Order().ToDictionary(p => Path.GetFileName(p)!,
            p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        await File.WriteAllTextAsync(Path.Combine(root, "snapshot.json"), JsonSerializer.Serialize(new
            { binary, hashes, period, subjects, os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString() }));
        var sockets = Enumerable.Range(0, 3).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var s in sockets) s.Start();
        var ports = sockets.Select(s => ((IPEndPoint)s.LocalEndpoint).Port).ToArray();
        foreach (var s in sockets) s.Stop();
        var processes = new List<Process>();
        var captures = new List<Task>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var evidence = new List<object>();
        string Url(int i, string path) => $"http://127.0.0.1:{ports[i]}{path}";
        async Task Post(int i, string path)
        { using var r = await http.PostAsync(Url(i, path), null, deadline.Token); r.EnsureSuccessStatusCode(); }
        async Task<JsonElement[]> Read()
        {
            var result = new JsonElement[3];
            for (var i = 0; i < 3; i++) result[i] = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync(Url(i, "/cohort"), deadline.Token));
            return result;
        }
        async Task<JsonElement[]> Wait(string stage, Func<JsonElement[], bool> test, int milliseconds = 15000)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var states = await Read();
                evidence.Add(new { stage, elapsedMs = watch.ElapsedMilliseconds, states });
                if (test(states)) { Console.WriteLine($"PASS {stage} elapsedMs={watch.ElapsedMilliseconds}"); return states; }
                if (watch.ElapsedMilliseconds > milliseconds) throw new InvalidOperationException($"Timeout: {stage}");
                await Task.Delay(40, deadline.Token);
            }
        }
        static bool Complete(JsonElement s) => s.GetProperty("coverage").GetProperty("status").GetString() == "Complete";
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var id = ((char)('a' + i)).ToString(); var dir = Path.Combine(root, id); Directory.CreateDirectory(dir);
                var start = new ProcessStartInfo("dotnet") { WorkingDirectory = dir, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.Environment["DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"] = "false";
                foreach (var arg in new[] { binary, "child", id, root, string.Join(',', ports), "--durable", "--cohort", "--subjects=" + subjects, period.ToString() }) start.ArgumentList.Add(arg);
                var child = Process.Start(start)!; processes.Add(child);
                captures.Add(Capture(child.StandardOutput, Path.Combine(dir, "stdout.log")));
                captures.Add(Capture(child.StandardError, Path.Combine(dir, "stderr.log")));
            }
            for (var i = 0; i < 3; i++)
                while (!File.Exists(Path.Combine(root, ((char)('a' + i)).ToString(), "ready")))
                { if (processes[i].HasExited) throw new InvalidOperationException("Child exited"); await Task.Delay(50, deadline.Token); }
            if (processes.Select(p => p.Id).Distinct().Count() != 3) throw new InvalidOperationException("Expected three OS processes");
            await Wait("zero-watermarks", s => s.All(Complete), period * 1000 + 10000);
            await Post(2, "/fault/partition/1");
            await Wait("partition-invalidates-complete", s => !Complete(s[0]) && !Complete(s[1]));
            await Post(2, "/fault/partition/0");
            await Wait("partition-recovery", s => s.All(Complete));
            await Post(2, "/fault/clock/1500");
            await Wait("clock-skew-invalidates-complete", s => !Complete(s[0]) && !Complete(s[1]));
            await Post(2, "/fault/clock/0");
            await Wait("clock-recovery", s => s.All(Complete));
            await Post(2, "/fault/revision/101");
            await Wait("gray-revision-partial", s => !Complete(s[0]) && !Complete(s[1]));
            await Post(2, "/fault/revision/100");
            await Wait("gray-reactivation-next-window", s => s.All(Complete), period * 1000 + 10000);
            // Align seeds inside one fixed UTC window; all subsequent observation is read-only.
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Task.Delay((int)(period * 1000 - now % (period * 1000) + 25), deadline.Token);
            var seedWatch = Stopwatch.StartNew(); var seedStart = DateTimeOffset.UtcNow;
            await Task.WhenAll(Enumerable.Range(0, 3).Select(i => Post(i, $"/seed/{new[] { 40, 35, 30 }[i]}")));
            var seededStates = new JsonElement[3];
            for (var i = 0; i < 3; i++) seededStates[i] = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync(Url(i, "/state"), deadline.Token));
            evidence.Add(new { stage = "seed-window-check", elapsedMs = seedWatch.ElapsedMilliseconds, seedStart, states = seededStates });
            Console.WriteLine($"SEEDED elapsedMs={seedWatch.ElapsedMilliseconds}");
            var decided = await Wait("background-105-no-new-requests", s => s.All(x => x.GetProperty("logs").GetArrayLength() == 1), period * 1000 + 3000);
            var ids = decided.Select(s => s.GetProperty("logs")[0].GetProperty("decisionId").GetString()).Distinct().ToArray();
            if (ids.Length != 1) throw new InvalidOperationException("Unstable DecisionId");
            await Wait("boot-bound-execution", s => s.All(x => x.GetProperty("execution").GetProperty("allNodesConfirmed").GetBoolean()));
            for (var i = 0; i < 3; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url(i, "/protected")); request.Headers.Host = "a.example";
                using var response = await http.SendAsync(request, deadline.Token);
                if (response.StatusCode != HttpStatusCode.Forbidden) throw new InvalidOperationException($"Node {i} did not enforce 403");
            }
            Console.WriteLine("PASS three independent processes: stable decision, boot-bound execution, actual 403");
            for (var i = 0; i < 3; i++) await Post(i, "/connect");
            var receiptWatch = Stopwatch.StartNew();
            while (true)
            {
                var receipts = new JsonElement[3];
                for (var i = 0; i < 3; i++) receipts[i] = JsonSerializer.Deserialize<JsonElement>(await http.GetStringAsync(Url(i, "/state"), deadline.Token));
                evidence.Add(new { stage = "durable-receipts", elapsedMs = receiptWatch.ElapsedMilliseconds, states = receipts });
                if (receipts.All(s => s.GetProperty("confirmations").GetArrayLength() == 1 &&
                    s.GetProperty("confirmations")[0].GetProperty("allNodesConfirmed").GetBoolean())) break;
                if (receiptWatch.Elapsed > TimeSpan.FromSeconds(30)) throw new InvalidOperationException("Durable receipts remain pending");
                await Task.Delay(100, deadline.Token);
            }
            Console.WriteLine($"PASS durable receipts independently confirmed elapsedMs={receiptWatch.ElapsedMilliseconds}");
        }
        finally
        {
            foreach (var (p, i) in processes.Select((p, i) => (p, i)))
            {
                try { using var r = await http.PostAsync(Url(i, "/stop"), null); } catch (HttpRequestException) { } catch (TaskCanceledException) { }
                try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { p.Kill(true); await p.WaitForExitAsync(); }
                p.Dispose();
            }
            await Task.WhenAll(captures);
            await File.WriteAllTextAsync(Path.Combine(root, "observations.json"), JsonSerializer.Serialize(evidence));
        }
    }

    private static async Task Capture(StreamReader reader, string path)
    {
        await using var writer = new StreamWriter(path) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
    }
}
