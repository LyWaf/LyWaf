using System.IO.Compression;
using System.Text;
using LyWaf.Services.ResponseWaf;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class ResponseWafTests
{
    [Fact]
    public async Task ObservePreservesBodyAndRecordsFinding()
    {
        var context = Context();
        var middleware = Middleware(new() { Enabled = true, Action = "Observe" }, async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("SqlException: syntax error at or near users");
        });

        await middleware.InvokeAsync(context);

        Assert.Contains("SqlException", Body(context));
        Assert.IsType<ResponseWafFinding>(context.Items["ResponseWaf.Finding"]);
    }

    [Fact]
    public async Task BlockReplacesSensitiveResponseAndRemovesEntityHeaders()
    {
        var context = Context();
        var middleware = Middleware(new() { Enabled = true, Action = "Block", BlockStatusCode = 502 }, async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers.ETag = "unsafe";
            await ctx.Response.WriteAsync("{\"password\":\"supersecret123\"}");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(502, context.Response.StatusCode);
        Assert.Contains("response_waf_blocked", Body(context));
        Assert.DoesNotContain("supersecret123", Body(context));
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task RedactMasksSecretsAndOptionalPersonalData()
    {
        var context = Context();
        var middleware = Middleware(new()
        {
            Enabled = true, Action = "Redact", DetectPersonalData = true
        }, async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.WriteAsync("api_key=abcdefghijk user@example.com 13800138000");
        });

        await middleware.InvokeAsync(context);

        var body = Body(context);
        Assert.Contains("[REDACTED]", body);
        Assert.Contains("[REDACTED EMAIL]", body);
        Assert.Contains("[REDACTED PHONE]", body);
        Assert.DoesNotContain("abcdefghijk", body);
    }

    [Theory]
    [InlineData("Status", null, "500")]
    [InlineData("Header", "X-Debug", "enabled")]
    public async Task CustomRulesMatchStatusAndHeaders(string field, string? header, string value)
    {
        var context = Context();
        var options = new ResponseWafOptions
        {
            Enabled = true,
            Rules = { new() { Id = "custom", Field = field, HeaderName = header, Operator = "Equal", Value = value } }
        };
        var middleware = Middleware(options, async ctx =>
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.Headers["X-Debug"] = "enabled";
            await ctx.Response.WriteAsync("ordinary response");
        });

        await middleware.InvokeAsync(context);

        Assert.Equal("custom", Assert.IsType<ResponseWafFinding>(context.Items["ResponseWaf.Finding"]).RuleId);
    }

    [Fact]
    public async Task InspectsCompressedResponseButDoesNotRedactCompressedBytes()
    {
        var context = Context();
        var compressed = Compress("password=supersecret123");
        var middleware = Middleware(new() { Enabled = true, Action = "Block" }, async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers.ContentEncoding = "gzip";
            await ctx.Response.Body.WriteAsync(compressed);
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(502, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Content-Encoding"));
        Assert.Contains("response_waf_blocked", Body(context));
    }

    [Fact]
    public async Task LargeAndFlushedResponsesBypassBuffering()
    {
        var largeContext = Context();
        var large = "password=supersecret123" + new string('x', 2048);
        await Middleware(new() { Enabled = true, Action = "Block", MaxInspectBytes = 1024 },
            ctx => ctx.Response.WriteAsync(large)).InvokeAsync(largeContext);
        Assert.Equal(large, Body(largeContext));
        Assert.False(largeContext.Items.ContainsKey("ResponseWaf.Finding"));

        var streamContext = Context();
        await Middleware(new() { Enabled = true, Action = "Block" }, async ctx =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("password=supersecret123");
            await ctx.Response.Body.FlushAsync();
        }).InvokeAsync(streamContext);
        Assert.Contains("supersecret123", Body(streamContext));
        Assert.False(streamContext.Items.ContainsKey("ResponseWaf.Finding"));
    }

    private static ResponseWafMiddleware Middleware(ResponseWafOptions options, RequestDelegate terminal)
    {
        var monitor = new StaticOptionsMonitor<ResponseWafOptions>(options);
        return new(terminal, monitor, new ResponseWafDetector(monitor));
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true).ReadToEnd();
    }

    private static byte[] Compress(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(value));
        return output.ToArray();
    }
}
