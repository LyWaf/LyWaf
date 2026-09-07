using LyWaf.Middleware;
using LyWaf.Services.Files;
using LyWaf.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace LyWaf.Tests;

public sealed class FileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lywaf-files-test-{Guid.NewGuid():N}");
    private readonly FileService _service;
    private readonly FileEveryConfig _config;

    public FileServiceTests()
    {
        Directory.CreateDirectory(_root);
        _service = new FileService(new StaticOptionsMonitor<FileProviderOptions>(new()));
        _config = new FileEveryConfig { BasePath = _root, MaxFileSize = 1024 };
    }

    [Fact]
    public async Task ResolvesFileButRejectsTraversalHiddenAndSensitivePaths()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "public.txt"), "public");
        await File.WriteAllTextAsync(Path.Combine(_root, ".secret"), "hidden");
        await File.WriteAllTextAsync(Path.Combine(_root, "app.config"), "secret");
        var request = Request();

        Assert.NotNull(await _service.GetFileRealAsync(request, _config, "/public.txt", "public.txt"));
        Assert.Null(await _service.GetFileRealAsync(request, _config, "/../outside.txt", "../outside.txt"));
        Assert.Null(await _service.GetFileRealAsync(request, _config, "/.secret", ".secret"));
        Assert.Null(await _service.GetFileRealAsync(request, _config, "/app.config", "app.config"));
    }

    [Fact]
    public async Task RejectsSymbolicLinksByDefault()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"lywaf-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "outside");
        var link = Path.Combine(_root, "linked.txt");
        try
        {
            try { File.CreateSymbolicLink(link, outside); }
            catch (IOException) when (OperatingSystem.IsWindows()) { return; }
            Assert.Null(await _service.GetFileRealAsync(Request(), _config, "/linked.txt", "linked.txt"));
            _config.FollowLinks = true;
            Assert.NotNull(await _service.GetFileRealAsync(Request(), _config, "/linked.txt", "linked.txt"));
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            File.Delete(outside);
        }
    }

    [Fact]
    public void LyConfigMapsStaticFileSecurityAndCachingOptions()
    {
        var parsed = LyConfigParser.Parse("""
            :8080 {
                file_server {
                    root = "./public"
                    follow_links = true
                    show_hidden = true
                    cache_control = "public, max-age=120"
                    denied_extensions = [".secret", ".key"]
                }
            }
            """);
        var transformed = LyToAppSettingsConverter.TransformToAppSettings(parsed);
        var fileServer = Assert.IsType<Dictionary<string, object>>(transformed["FileServer"]);
        var items = Assert.IsType<Dictionary<string, object>>(fileServer["Items"]);
        var item = Assert.IsType<Dictionary<string, object>>(Assert.Single(items).Value);

        Assert.Equal(true, item["FollowLinks"]);
        Assert.Equal(true, item["ShowHidden"]);
        Assert.Equal("public, max-age=120", item["CacheControl"]);
        Assert.Equal(2, Assert.IsType<HashSet<string>>(item["DeniedExtensions"]).Count);
    }

    [Theory]
    [InlineData("bytes=2-5", 2, 5)]
    [InlineData("bytes=7-", 7, 9)]
    [InlineData("bytes=-3", 7, 9)]
    public async Task NormalizesSingleRange(string value, long start, long end)
    {
        var path = await WriteAsync("range.txt", "0123456789");
        Assert.True(RangeHeaderValue.TryParse(value, out var range));

        var result = await _service.GetFileAsync(_config, value, path, range);

        Assert.Equal(206, result.StatusCode);
        var item = Assert.Single(result.Ranges);
        Assert.Equal(start, item.Start);
        Assert.Equal(end, item.End);
        Assert.Equal(end - start + 1, result.ContentLength);
    }

    [Fact]
    public async Task SupportsMultipleRangesWithoutMaterializingFile()
    {
        var path = await WriteAsync("multi.txt", "0123456789");
        Assert.True(RangeHeaderValue.TryParse("bytes=0-1,8-9", out var range));

        var result = await _service.GetFileAsync(_config, "/multi.txt", path, range);

        Assert.Equal(206, result.StatusCode);
        Assert.Equal(2, result.Ranges.Count);
        Assert.Null(result.ContentLength);
        Assert.NotNull(result.PhysicalPath);
        Assert.StartsWith("multipart/byteranges; boundary=", result.ContentType);
    }

    [Fact]
    public async Task UnsatisfiableRangeReturns416()
    {
        var path = await WriteAsync("short.txt", "1234");
        Assert.True(RangeHeaderValue.TryParse("bytes=10-20", out var range));

        var result = await _service.GetFileAsync(_config, "/short.txt", path, range);

        Assert.Equal(416, result.StatusCode);
        Assert.Equal(4, result.TotalLength);
    }

    [Fact]
    public async Task MaxFileSizeIsMeasuredInBytes()
    {
        var path = await WriteAsync("large.txt", "12345");
        _config.MaxFileSize = 4;

        var result = await _service.GetFileAsync(_config, "/large.txt", path);

        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task ConditionalRequestsMatchQuotedEtagAndSecondPrecisionDate()
    {
        var path = await WriteAsync("etag.txt", "etag");
        var metadata = await _service.GetFileMetadataAsync(path);
        var etagContext = new DefaultHttpContext();
        etagContext.Request.Headers.IfNoneMatch = metadata.ETag;
        var dateContext = new DefaultHttpContext();
        dateContext.Request.Headers.IfModifiedSince = metadata.LastModified.ToString("R");

        Assert.True(FileProviderMiddleware.HandleConditionalRequest(etagContext, metadata, "public, max-age=60"));
        Assert.Equal(304, etagContext.Response.StatusCode);
        Assert.Equal(metadata.ETag, etagContext.Response.Headers.ETag);
        Assert.True(FileProviderMiddleware.HandleConditionalRequest(dateContext, metadata, string.Empty));
    }

    [Fact]
    public async Task IfRangeMustMatchCurrentValidator()
    {
        var path = await WriteAsync("if-range.txt", "body");
        var metadata = await _service.GetFileMetadataAsync(path);
        var matching = Request();
        matching.Headers.IfRange = metadata.ETag;
        var stale = Request();
        stale.Headers.IfRange = "\"old\"";

        Assert.True(FileProviderMiddleware.IfRangeMatches(matching, metadata));
        Assert.False(FileProviderMiddleware.IfRangeMatches(stale, metadata));
    }

    [Fact]
    public async Task PrecompressedSelectionHonorsQualityAndPreference()
    {
        await WriteAsync("app.js", "plain");
        await WriteAsync("app.js.gz", "gzip");
        await WriteAsync("app.js.br", "brotli");
        _config.PreCompressed = true;
        var request = Request();
        request.Headers.AcceptEncoding = "gzip;q=1, br;q=1";

        var result = await _service.GetFileRealAsync(request, _config, "/app.js", "app.js");

        Assert.NotNull(result);
        Assert.Equal("br", result.PreCompressed);
        Assert.EndsWith(".br", result.Path);
    }

    private static HttpRequest Request() => new DefaultHttpContext().Request;
    private async Task<string> WriteAsync(string name, string content)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
