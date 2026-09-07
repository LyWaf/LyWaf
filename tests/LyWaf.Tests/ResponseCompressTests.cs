using System.IO.Compression;
using System.Text;
using LyWaf.Middleware;
using LyWaf.Services.Compress;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class ResponseCompressTests
{
    [Fact]
    public async Task CompressesFromDiskBufferAndPreservesContent()
    {
        var text = string.Concat(Enumerable.Repeat("compressible-content-", 1000));
        var middleware = Create(minSize: 1);
        var context = Context("gzip");

        await middleware.InvokeAsync(context, async current =>
        {
            current.Response.ContentType = "text/plain";
            await current.Response.WriteAsync(text);
        });

        Assert.Equal("gzip", context.Response.Headers.ContentEncoding);
        context.Response.Body.Position = 0;
        await using var gzip = new GZipStream(context.Response.Body, CompressionMode.Decompress);
        Assert.Equal(text, await new StreamReader(gzip, Encoding.UTF8).ReadToEndAsync());
    }

    [Fact]
    public async Task SmallResponseIsCopiedWithoutCompression()
    {
        var middleware = Create(minSize: 1024);
        var context = Context("gzip");

        await middleware.InvokeAsync(context, current => current.Response.WriteAsync("small"));

        Assert.Empty(context.Response.Headers.ContentEncoding.ToString());
        context.Response.Body.Position = 0;
        Assert.Equal("small", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task InnerFailurePropagatesAndTemporaryFilesAreRemoved()
    {
        var before = TemporaryFiles();
        var middleware = Create(minSize: 1);
        var context = Context("gzip");

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context,
            _ => throw new InvalidOperationException("upstream failed")));

        Assert.Empty(TemporaryFiles().Except(before));
    }

    private static ResponseCompressMiddleware Create(int minSize) => new(
        new StaticOptionsMonitor<CompressOptions>(new()
        {
            Enabled = true,
            EnableBrotli = false,
            EnableGzip = true,
            MinSize = minSize,
            MimeTypes = ["text/plain"]
        }));

    private static DefaultHttpContext Context(string acceptEncoding)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptEncoding = acceptEncoding;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static HashSet<string> TemporaryFiles() => Directory
        .EnumerateFiles(Path.GetTempPath(), "lywaf-compress-*.tmp")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
