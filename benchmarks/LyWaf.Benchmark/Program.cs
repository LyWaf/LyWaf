using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length == 0 || args[0] is "help" or "--help")
{
    Console.WriteLine("serve [--urls http://127.0.0.1:19080]");
    Console.WriteLine("load --url URL [--protocol 1.1|2] [--duration 15] [--concurrency 64] [--slow-ms 0] [--websocket] [--insecure]");
    return;
}

if (args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
{
    await BenchmarkServer.RunAsync(args[1..]);
    return;
}

if (args[0].Equals("load", StringComparison.OrdinalIgnoreCase))
{
    var result = await LoadRunner.RunAsync(LoadOptions.Parse(args[1..]));
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = result.Errors == 0 ? 0 : 2;
    return;
}

throw new ArgumentException($"Unknown mode: {args[0]}");

/// <summary>
/// 提供各代理实现共用的确定性上游，避免业务应用差异污染代理基准结果。
/// </summary>
internal static class BenchmarkServer
{
    private static readonly byte[] LargeResponse = new byte[1024 * 1024];

    public static async Task RunAsync(string[] args)
    {
        var urls = Argument(args, "--urls", "http://127.0.0.1:19080");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(urls);
        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/small", () => Results.Text("01234567890123456789012345678901", "text/plain"));
        app.MapGet("/large", () => Results.Bytes(LargeResponse, "application/octet-stream"));
        app.MapGet("/compress", () => Results.Text(new string('a', 256 * 1024), "text/plain"));
        app.MapGet("/slow", async () => { await Task.Delay(100); return Results.Text("slow"); });
        app.MapGet("/fault/status/{code:int}", (int code) => Results.StatusCode(Math.Clamp(code, 400, 599)));
        app.MapGet("/fault/delay/{milliseconds:int}", async (int milliseconds, CancellationToken token) =>
        {
            await Task.Delay(Math.Clamp(milliseconds, 0, 60_000), token);
            return Results.Text("delayed");
        });
        app.MapGet("/fault/abort", (HttpContext context) =>
        {
            context.Abort();
            return Task.CompletedTask;
        });
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var message = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (message.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
                    break;
                }
                await socket.SendAsync(buffer.AsMemory(0, message.Count), message.MessageType, message.EndOfMessage, context.RequestAborted);
            }
        });
        await app.RunAsync();
    }

    private static string Argument(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
}

/// <summary>
/// 描述单轮负载，包括协议、并发、持续时间、慢读取和 WebSocket 模式。
/// </summary>
internal sealed record LoadOptions(Uri Url, Version Protocol, int DurationSeconds, int Concurrency, int SlowReadMs, bool WebSocket, bool Insecure)
{
    public static LoadOptions Parse(string[] args)
    {
        string Value(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        var urlValue = Value("--url", "");
        if (urlValue.Length == 0) throw new ArgumentException("--url is required");
        var url = new Uri(urlValue);
        var protocol = Value("--protocol", "1.1") == "2" ? HttpVersion.Version20 : HttpVersion.Version11;
        return new(url, protocol, Math.Clamp(int.Parse(Value("--duration", "15")), 1, 604800),
            Math.Clamp(int.Parse(Value("--concurrency", "64")), 1, 10000),
            Math.Clamp(int.Parse(Value("--slow-ms", "0")), 0, 60000), args.Contains("--websocket"), args.Contains("--insecure"));
    }
}

/// <summary>
/// 单轮基准结果。延迟单位为毫秒，吞吐按成功和失败请求总数计算。
/// </summary>
internal sealed record LoadResult(string Url, string Protocol, int Concurrency, double DurationSeconds,
    long Requests, long Errors, double ErrorRate, double RequestsPerSecond,
    double P50Ms, double P95Ms, double P99Ms, long Bytes, IReadOnlyDictionary<string, long> ErrorReasons);

/// <summary>
/// 执行有界并发负载并聚合延迟。每个工作线程复用连接，直方图大小固定，不保存逐请求样本。
/// </summary>
internal static class LoadRunner
{
    public static async Task<LoadResult> RunAsync(LoadOptions options)
    {
        var histogram = new LatencyHistogram();
        var errorReasons = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        long requests = 0, errors = 0, bytes = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = options.Concurrency };
        if (options.Insecure) handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var wall = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, options.Concurrency).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var count = options.WebSocket
                        ? await WebSocketRoundTrip(options.Url, options.Insecure, stop.Token)
                        : await HttpRoundTrip(client, options, stop.Token);
                    Interlocked.Add(ref bytes, count);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);
                    errorReasons.AddOrUpdate(ErrorReason(ex), 1, static (_, count) => count + 1);
                }
                finally
                {
                    if (!stop.IsCancellationRequested)
                    {
                        histogram.Record(Stopwatch.GetElapsedTime(started));
                        Interlocked.Increment(ref requests);
                    }
                }
            }
        })).ToArray();
        await Task.WhenAll(workers);
        wall.Stop();
        var completed = requests;
        var failed = Math.Min(errors, completed);
        return new(options.Url.ToString(), options.WebSocket ? "WebSocket" : options.Protocol.ToString(),
            options.Concurrency, wall.Elapsed.TotalSeconds, completed, failed,
            completed == 0 ? 0 : (double)failed / completed, completed / wall.Elapsed.TotalSeconds,
            histogram.Percentile(0.50), histogram.Percentile(0.95), histogram.Percentile(0.99), bytes,
            errorReasons);
    }

    private static string ErrorReason(Exception exception)
    {
        if (exception is WebSocketException webSocket) return $"websocket_{webSocket.WebSocketErrorCode}";
        if (exception is HttpRequestException http && http.StatusCode is { } status) return "http_" + (int)status;
        return exception.GetType().Name;
    }

    private static async Task<int> HttpRoundTrip(HttpClient client, LoadOptions options, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.Url)
        { Version = options.Protocol, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[16 * 1024];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (options.SlowReadMs > 0) await Task.Delay(options.SlowReadMs, token);
        }
        return total;
    }

    private static async Task<int> WebSocketRoundTrip(Uri uri, bool insecure, CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        if (insecure) socket.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" };
        await socket.ConnectAsync(builder.Uri, token);
        var payload = "lywaf"u8.ToArray();
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, token);
        var received = await socket.ReceiveAsync(new byte[64], token);
        socket.Abort();
        return received.Count;
    }
}

/// <summary>
/// 固定 1ms 精度、最大 60 秒的有界延迟直方图，用于计算 P50、P95 和 P99。
/// </summary>
internal sealed class LatencyHistogram
{
    private readonly long[] _buckets = new long[60_001];
    private long _count;

    public void Record(TimeSpan elapsed)
    {
        var bucket = Math.Clamp((int)Math.Ceiling(elapsed.TotalMilliseconds), 0, _buckets.Length - 1);
        Interlocked.Increment(ref _buckets[bucket]);
        Interlocked.Increment(ref _count);
    }

    public double Percentile(double percentile)
    {
        var count = Volatile.Read(ref _count);
        if (count == 0) return 0;
        var target = (long)Math.Ceiling(count * percentile);
        long seen = 0;
        for (var index = 0; index < _buckets.Length; index++)
            if ((seen += Volatile.Read(ref _buckets[index])) >= target) return index;
        return _buckets.Length - 1;
    }
}
