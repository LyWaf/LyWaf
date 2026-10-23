using System.Text;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Examples;
using LyWaf.Plugins.Examples.RequestLogger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class RequestLoggerPluginTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "lywaf-request-log-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Theory]
    [InlineData("20x", 209, true)]
    [InlineData("30x", 301, true)]
    [InlineData("4XX", 499, true)]
    [InlineData("503", 503, true)]
    [InlineData("503", 500, false)]
    [InlineData("2*", 200, false)]
    [InlineData("*", 99, false)]
    [InlineData("*", 599, true)]
    public void StatusPatterns(string pattern, int code, bool expected) => Assert.Equal(expected, RequestLoggerPlugin.StatusMatches(pattern, code));

    [Fact]
    public void FiltersAreCombined()
    {
        var rule = new MatchRule { Methods = "POST, PUT", StatusCodes = ["4xx"], MinDurationMs = 100 };
        Assert.True(RequestLoggerPlugin.ShouldLog(rule, "post", 403, TimeSpan.FromMilliseconds(100)));
        Assert.False(RequestLoggerPlugin.ShouldLog(rule, "GET", 403, TimeSpan.FromMilliseconds(100)));
        Assert.False(RequestLoggerPlugin.ShouldLog(rule, "POST", 200, TimeSpan.FromMilliseconds(100)));
        Assert.False(RequestLoggerPlugin.ShouldLog(rule, "POST", 403, TimeSpan.FromMilliseconds(99)));
        rule.SampleRate = 0;
        Assert.False(RequestLoggerPlugin.ShouldLog(rule, "POST", 403, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RedactionCoversNestedJsonQueryFormsAndCustomNames()
    {
        var redactor = new RequestLogRedactor(["private_value"]);
        Assert.True(redactor.IsSensitive("X-API-Key"));
        Assert.True(redactor.IsSensitive("Set-Cookie"));
        var json = redactor.Body("{\"password\":\"secret1\",\"nested\":[{\"private_value\":\"secret2\",\"safe\":42}]}", "application/json", 4096, false)!;
        Assert.DoesNotContain("secret1", json);
        Assert.DoesNotContain("secret2", json);
        Assert.Contains("42", json);
        Assert.Contains("REDACTED", json);
        Assert.DoesNotContain("secret3", redactor.Query("?token=secret3&safe=ok"));
        Assert.DoesNotContain("secret4", redactor.Body("password=secret4&safe=ok", "application/x-www-form-urlencoded", 4096, false));
    }

    [Theory]
    [InlineData("application/json", "{broken", false)]
    [InlineData("application/json", "{\"password\":\"do-not-log\"}", true)]
    [InlineData("text/plain", "do-not-log", false)]
    [InlineData("application/octet-stream", "do-not-log", false)]
    public void UnsafeBodiesAreOmitted(string type, string body, bool truncated)
    {
        var result = new RequestLogRedactor([]).Body(body, type, 256, truncated)!;
        Assert.StartsWith("[Body omitted:", result);
        Assert.DoesNotContain("do-not-log", result);
    }

    [Fact]
    public void MetadataCannotInjectTextRecordsOrPaths()
    {
        Assert.DoesNotContain('\n', RequestLogRedactor.Line("ok\r\nStatus: 200"));
        var prefix = RequestLoggerPlugin.SafePrefix("../../bad\\file:stream");
        Assert.DoesNotContain("..", prefix);
        Assert.DoesNotContain('/', prefix);
        Assert.DoesNotContain('\\', prefix);
        Assert.DoesNotContain(':', prefix);
    }

    [Fact]
    public async Task CapturesWithoutBufferingResponseAndWritesRedactedJson()
    {
        var plugin = Plugin();
        var options = (RequestLoggerOptions)plugin.Metadata.DefaultOptions!;
        options.MatchRules = [new() { Host = "*", LogHeaders = true, LogRequestBody = true, LogResponseBody = true }];
        await plugin.StartAsync(default);
        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            plugin.ConfigureProxyPipeline(app);
            var output = new MemoryStream();
            app.Run(async context =>
            {
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                Assert.Contains("request-secret", await reader.ReadToEndAsync());
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"password\":\"response-secret\",\"safe\":true}");
                await context.Response.Body.FlushAsync();
                Assert.True(output.Length > 0);
            });
            var context = Context();
            context.RequestServices = services;
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"token\":\"request-secret\",\"safe\":123}"));
            context.Request.QueryString = new("?password=query-secret&safe=ok");
            context.Request.Headers.Authorization = "Bearer header-secret";
            context.Response.Body = output;
            await app.Build()(context);
            Assert.Same(output, context.Response.Body);
            await Complete(plugin, context);
        }
        finally { await plugin.StopAsync(default); }
        var text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory, "*.jsonl")));
        foreach (var secret in new[] { "request-secret", "response-secret", "query-secret", "header-secret" }) Assert.DoesNotContain(secret, text);
        var record = JsonSerializer.Deserialize<RequestLogRecord>(text, RequestLogRecord.Json)!;
        Assert.Equal(200, record.Status);
        Assert.Contains("123", record.RequestBody);
        Assert.Contains("true", record.ResponseBody);
        Assert.Equal(1, plugin.WriterStatus.Written);
        Assert.Equal(0, plugin.WriterStatus.Pending);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 499)]
    public async Task CompletionRecordsClientCancellation(bool canceled, int expected)
    {
        var plugin = Plugin();
        await plugin.StartAsync(default);
        var context = Context();
        if (canceled) context.RequestAborted = new CancellationToken(true);
        await Complete(plugin, context);
        await plugin.StopAsync(default);
        var record = JsonSerializer.Deserialize<RequestLogRecord>(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory))), RequestLogRecord.Json)!;
        Assert.Equal(expected, record.Status);
        Assert.Equal(canceled, record.ClientAborted);
    }

    [Fact]
    public async Task InterceptIsNotSuppressedBySamplingAndHealthIsIgnored()
    {
        var plugin = Plugin();
        var options = (RequestLoggerOptions)plugin.Metadata.DefaultOptions!;
        options.MatchRules = [new() { Host = "*", SampleRate = 0 }];
        await plugin.StartAsync(default);
        var context = Context();
        context.Items["RequestLogger.InterceptRule"] = new MatchRule { Host = "*", FilePrefix = "intercept" };
        await Complete(plugin, context);
        await plugin.StopAsync(default);
        Assert.StartsWith("intercept_", Path.GetFileName(Assert.Single(Directory.GetFiles(directory))));
        options.MatchRules.Clear();
        await plugin.StartAsync(default);
        context = Context(); context.Request.Path = "/health";
        await Complete(plugin, context);
        await plugin.StopAsync(default);
        Assert.Equal(0, plugin.WriterStatus.Enqueued);
    }

    [Fact]
    public async Task ViewerSupportsJsonPaginationSearchAndMutations()
    {
        var plugin = Plugin();
        await plugin.StartAsync(default);
        for (var i = 0; i < 5; i++) { var context = Context(); context.Request.Path = "/item/" + i; await Complete(plugin, context); }
        var list = await Action(plugin, "list-files");
        var name = list.GetProperty("files")[0].GetProperty("name").GetString()!;
        var page = await Action(plugin, "read-entries", name, ("offset", "1"), ("limit", "2"));
        Assert.Equal(5, page.GetProperty("total").GetInt32());
        Assert.Equal("/item/3", page.GetProperty("entries")[0].GetProperty("url").GetString());
        var search = await Action(plugin, "read-entries", name, ("search", "/item/2"));
        Assert.Equal(1, search.GetProperty("total").GetInt32());
        var far = await Action(plugin, "read-entries", name, ("offset", "1000000"));
        Assert.Empty(far.GetProperty("entries").EnumerateArray());
        await Action(plugin, "delete-entry", name, ("index", "2"));
        Assert.Equal(4, (await Action(plugin, "read-entries", name)).GetProperty("total").GetInt32());
        await Action(plugin, "clear-file", name);
        Assert.Equal(0, (await Action(plugin, "read-entries", name)).GetProperty("total").GetInt32());
        await Action(plugin, "delete-file", name);
        Assert.Empty((await Action(plugin, "list-files")).GetProperty("files").EnumerateArray());
        await plugin.StopAsync(default);
    }

    [Theory]
    [InlineData("../req_secret.log")]
    [InlineData("req_file.log:stream")]
    [InlineData("unrelated.log")]
    public async Task ViewerRejectsUnownedPaths(string name)
    {
        var result = await Plugin().ExecuteActionAsync("view-logs", new() { ["action"] = "delete-file", ["file"] = name });
        Assert.False(result.Success);
    }

    [Fact]
    public async Task TextFormatAndCustomPrefixRemainReadable()
    {
        var plugin = Plugin();
        var options = (RequestLoggerOptions)plugin.Metadata.DefaultOptions!;
        options.Format = "text";
        options.MatchRules = [new() { Host = "*", FilePrefix = "custom" }];
        await plugin.StartAsync(default);
        try
        {
            await Complete(plugin, Context());
            var list = await Action(plugin, "list-files");
            var name = list.GetProperty("files")[0].GetProperty("name").GetString()!;
            Assert.StartsWith("req_custom_", name);
            var page = await Action(plugin, "read-entries", name);
            Assert.Equal(1, page.GetProperty("total").GetInt32());
            Assert.Equal(200, page.GetProperty("entries")[0].GetProperty("statusCode").GetInt32());
        }
        finally { await plugin.StopAsync(default); }
    }

    [Fact]
    public async Task LargeResponseIsForwardedAndTruncatedCaptureIsOmitted()
    {
        var plugin = Plugin();
        var options = (RequestLoggerOptions)plugin.Metadata.DefaultOptions!;
        options.MaxBodySize = 256;
        options.MatchRules = [new() { Host = "*", LogResponseBody = true }];
        await plugin.StartAsync(default);
        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            plugin.ConfigureProxyPipeline(app);
            var body = "{\"password\":\"" + new string('x', 8192) + "\"}";
            app.Run(async context => { context.Response.ContentType = "application/json"; await context.Response.WriteAsync(body); });
            var context = Context(); context.RequestServices = services;
            var output = new MemoryStream(); context.Response.Body = output;
            await app.Build()(context);
            Assert.Equal(body, Encoding.UTF8.GetString(output.ToArray()));
            await Complete(plugin, context);
        }
        finally { await plugin.StopAsync(default); }
        var record = JsonSerializer.Deserialize<RequestLogRecord>(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory))), RequestLogRecord.Json)!;
        Assert.True(record.ResponseBodyTruncated);
        Assert.Contains("omitted", record.ResponseBody);
        Assert.DoesNotContain("xxxxx", record.ResponseBody);
    }

    [Fact]
    public async Task DownstreamExceptionPropagatesButIsRecordedWithoutItsSensitiveMessage()
    {
        var plugin = Plugin();
        await plugin.StartAsync(default);
        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var app = new ApplicationBuilder(services);
            plugin.ConfigureProxyPipeline(app);
            app.Run(_ => throw new InvalidOperationException("private error detail"));
            var context = Context(); context.RequestServices = services;
            await Assert.ThrowsAsync<InvalidOperationException>(() => app.Build()(context));
            await Complete(plugin, context);
        }
        finally { await plugin.StopAsync(default); }
        var text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(directory)));
        var record = JsonSerializer.Deserialize<RequestLogRecord>(text, RequestLogRecord.Json)!;
        Assert.Equal(500, record.Status);
        Assert.Equal("InvalidOperationException", record.Failure);
        Assert.DoesNotContain("private error detail", text);
    }

    [Fact]
    public async Task WriterDrainsRotatesAndKeepsUnrelatedFiles()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "unrelated.log"), "keep");
        var writer = new RequestLogWriter(64, 65536, new(1, 1), name => name.StartsWith("req_"), _ => { });
        var policy = new RequestLogWritePolicy(20, 3, 14, 64, 10);
        for (var i = 0; i < 10; i++) Assert.True(writer.TryWrite(Path.Combine(directory, "req_test.log"), "abcdefghij\n", policy));
        await writer.StopAsync(default);
        Assert.Equal(10, writer.Status.Written);
        Assert.Equal(0, writer.Status.Pending);
        Assert.Equal(3, Directory.GetFiles(directory, "req_*").Length);
        Assert.True(File.Exists(Path.Combine(directory, "unrelated.log")));
    }

    [Fact]
    public async Task WriterReportsOverflowAndIoFailureWithoutCrashing()
    {
        Directory.CreateDirectory(directory);
        var blockedDirectory = Path.Combine(directory, "file");
        await File.WriteAllTextAsync(blockedDirectory, "not a directory");
        var writer = new RequestLogWriter(16, 16, new(1, 1), _ => true, _ => { });
        var policy = new RequestLogWritePolicy(4096, 3, 14, 64, 10);
        Assert.False(writer.TryWrite(Path.Combine(directory, "req_big.log"), new string('x', 17), policy));
        Assert.True(writer.TryWrite(Path.Combine(blockedDirectory, "req_test.log"), "entry", policy));
        await writer.StopAsync(default);
        Assert.Equal(1, writer.Status.Dropped);
        Assert.Equal(1, writer.Status.WriteFailures);
        Assert.Equal(0, writer.Status.Pending);
    }

    private RequestLoggerPlugin Plugin()
    {
        var plugin = new RequestLoggerPlugin();
        var options = (RequestLoggerOptions)plugin.Metadata.DefaultOptions!;
        options.LogDirectory = directory; options.Format = "json"; options.FlushIntervalMs = 10;
        return plugin;
    }

    [Fact]
    public async Task FullBatchesDoNotWaitForFlushInterval()
    {
        var writer = new RequestLogWriter(1024, 65536, new(1, 1), _ => true, _ => { });
        try
        {
            var policy = new RequestLogWritePolicy(1024 * 1024, 30, 14, 64, 10000);
            for (var i = 0; i < 512; i++) Assert.True(writer.TryWrite(Path.Combine(directory, "req_burst.log"), "entry\n", policy));
            await WaitForAsync(() => writer.Status.Written == 512);
            Assert.Equal(0, writer.Status.Dropped);
        }
        finally { await writer.StopAsync(default); }
        Assert.Equal(0, writer.Status.QueuedBytes);
    }

    [Fact]
    public async Task FlushBarrierAndStopReleasePartialBatchesImmediately()
    {
        var writer = new RequestLogWriter(16, 65536, new(1, 1), _ => true, _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var policy = new RequestLogWritePolicy(4096, 30, 14, 64, 10000);
        Assert.True(writer.TryWrite(Path.Combine(directory, "req_flush.log"), "first\n", policy));
        await writer.FlushAsync(timeout.Token);
        Assert.Equal(1, writer.Status.Written);
        Assert.True(writer.TryWrite(Path.Combine(directory, "req_flush.log"), "second\n", policy));
        await writer.StopAsync(timeout.Token);
        Assert.Equal(2, writer.Status.Written);
        Assert.Equal(0, writer.Status.Pending);
    }

    [Fact]
    public async Task PartialBatchesHonorDeadlineWithoutExplicitFlush()
    {
        var writer = new RequestLogWriter(16, 65536, new(1, 1), _ => true, _ => { });
        try
        {
            Assert.True(writer.TryWrite(Path.Combine(directory, "req_idle.log"), "entry\n", new(4096, 30, 14, 64, 30)));
            await WaitForAsync(() => writer.Status.Written == 1);
        }
        finally { await writer.StopAsync(default); }
    }

    [Fact]
    public async Task ByteBudgetIncludesRecordsWaitingForDiskLock()
    {
        var gate = new SemaphoreSlim(0, 1);
        var writer = new RequestLogWriter(16, 16, gate, _ => true, _ => { });
        try
        {
            var policy = new RequestLogWritePolicy(4096, 30, 14, 1, 10);
            Assert.True(writer.TryWrite(Path.Combine(directory, "req_bytes.log"), "0123456789", policy));
            await Task.Delay(30);
            Assert.Equal(10, writer.Status.QueuedBytes);
            Assert.False(writer.TryWrite(Path.Combine(directory, "req_bytes.log"), "0123456789", policy));
        }
        finally { gate.Release(); await writer.StopAsync(default); gate.Dispose(); }
        Assert.Equal(0, writer.Status.QueuedBytes);
        Assert.Equal(1, writer.Status.Dropped);
    }

    [Fact]
    public async Task CleanupFailureDoesNotInvalidateWrittenRecordsOrKillWriter()
    {
        Directory.CreateDirectory(directory);
        var old = Path.Combine(directory, "req_old.log");
        await File.WriteAllTextAsync(old, "old"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        var attempts = 0;
        var writer = new RequestLogWriter(16, 65536, new(1, 1), _ => true,
            _ => throw new InvalidOperationException("broken diagnostic sink"),
            deleteFile: _ => { attempts++; throw new IOException("delete failure"); });
        try
        {
            var policy = new RequestLogWritePolicy(4096, 1, 14, 1, 10);
            for (var i = 0; i < 2; i++)
            {
                Assert.True(writer.TryWrite(Path.Combine(directory, "req_current.log"), "entry\n", policy));
                await writer.FlushAsync();
            }
            Assert.Equal(2, writer.Status.Written);
            Assert.Equal(0, writer.Status.WriteFailures);
            Assert.Equal(1, writer.Status.CleanupFailures);
            Assert.Equal(1, attempts);
            Assert.Equal("IOException", writer.Status.LastErrorType);
            Assert.NotNull(writer.Status.LastErrorAt);
            Assert.True(writer.Status.Healthy);
        }
        finally { await writer.StopAsync(default); }
    }

    [Fact]
    public async Task RotationFailureKeepsAlreadyFlushedRecordCounts()
    {
        var attempts = 0;
        var writer = new RequestLogWriter(16, 65536, new(1, 1), _ => true, _ => { }, moveFile: (source, target) =>
        { if (++attempts == 1) throw new IOException("rotation failure"); File.Move(source, target); });
        try
        {
            var path = Path.Combine(directory, "req_rotate.log");
            var policy = new RequestLogWritePolicy(5, 30, 14, 2, 10000);
            Assert.True(writer.TryWrite(path, "one\n", policy));
            Assert.True(writer.TryWrite(path, "two\n", policy));
            await writer.FlushAsync();
            Assert.Equal(1, writer.Status.Written);
            Assert.Equal(1, writer.Status.WriteFailures);
            Assert.Equal(1, writer.Status.RotationFailures);
            Assert.True(writer.TryWrite(path, "end\n", policy));
            await writer.FlushAsync();
            Assert.Equal(2, writer.Status.Written);
            Assert.Equal(0, writer.Status.Pending);
        }
        finally { await writer.StopAsync(default); }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    public async Task OpenSnapshotDoesNotBlockWritesAndSurvivesClear(string format)
    {
        var plugin = Plugin(); ((RequestLoggerOptions)plugin.Metadata.DefaultOptions!).Format = format;
        await plugin.StartAsync(default);
        try
        {
            await Complete(plugin, Context());
            var list = await Action(plugin, "list-files");
            var name = list.GetProperty("files")[0].GetProperty("name").GetString()!;
            var snapshot = await plugin.OpenSnapshotAsync(name, default);
            await using var stream = snapshot.Stream;
            await Complete(plugin, Context());
            await Action(plugin, "list-files");
            Assert.Equal(2, plugin.WriterStatus.Written);
            await Action(plugin, "clear-file", name);
            await Complete(plugin, Context());
            await Action(plugin, "list-files");
            Assert.Equal(3, plugin.WriterStatus.Written);
            if (format == "json")
                Assert.Equal(1, (await RequestLogReader.ReadJsonAsync(stream, snapshot.Length, 0, 20, "", default)).Total);
            else
            {
                using var scanner = new LyWaf.Utils.ReverseLogScanner(stream, snapshot.Length);
                Assert.Equal(1, (await scanner.QuerySnapshotAsync(0, 20, "", default)).total);
            }
            Assert.Equal(1, (await Action(plugin, "read-entries", name)).GetProperty("total").GetInt32());
        }
        finally { await plugin.StopAsync(default); }
    }

    [Fact]
    public async Task JsonReverseReaderHandlesUtf8ChunkBoundariesMalformedLinesAndPhysicalIndices()
    {
        Directory.CreateDirectory(directory);
        var name = "req_utf8.jsonl";
        var body = new string('中', 30000);
        var first = "{\"timestamp\":\"2026-10-22T00:00:00Z\",\"method\":\"GET\",\"path\":\"/first\",\"requestBody\":\"" + body + "\",\"status\":200}";
        var last = "{\"timestamp\":\"2026-10-22T00:00:00Z\",\"method\":\"GET\",\"path\":\"/last\",\"status\":403}";
        await File.WriteAllTextAsync(Path.Combine(directory, name), "\ufeff" + first + "\r\nbroken\n\n" + last);
        var plugin = Plugin();
        var snapshot = await plugin.OpenSnapshotAsync(name, default);
        await using var stream = snapshot.Stream;
        var result = await RequestLogReader.ReadJsonAsync(stream, snapshot.Length, 0, 20, "", default);
        Assert.Equal(2, result.Total);
        Assert.Equal([3, 0], result.Entries.Select(e => e.Index));
        Assert.Equal(body, result.Entries[1].RequestBody);
        var page = await RequestLogReader.ReadJsonAsync(stream, snapshot.Length, 1, 1, "", default);
        Assert.Equal("/first", Assert.Single(page.Entries).Url);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RequestLogReader.ReadJsonAsync(stream, snapshot.Length, 0, 20, "", canceled.Token));
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET"; context.Request.Path = "/test";
        context.Request.Host = new("example.com"); context.Request.Protocol = "HTTP/1.1";
        return context;
    }
    private static Task Complete(RequestLoggerPlugin plugin, HttpContext context) => plugin.ProcessCompletedAsync(new()
    { Context = context, StatusCode = 200, Duration = TimeSpan.FromMilliseconds(15), VisitTime = DateTime.UtcNow });
    private static async Task<JsonElement> Action(RequestLoggerPlugin plugin, string action, string? file = null, params (string Key, string Value)[] extra)
    {
        var parameters = new Dictionary<string, string> { ["action"] = action };
        if (file is not null) parameters["file"] = file;
        foreach (var pair in extra) parameters[pair.Key] = pair.Value;
        var result = await plugin.ExecuteActionAsync("view-logs", parameters);
        Assert.True(result.Success, result.Message);
        return JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
