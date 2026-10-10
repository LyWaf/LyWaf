using System.IO.Compression;
using LyWaf.Plugins.Sys.HttpUpload;
using Xunit;
using LyWaf.Plugins.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace LyWaf.Tests;

public sealed class HttpUploadTests
{
    [Fact]
    public async Task RealKestrelAcceptsUploadAboveDefaultThirtyMillionBytes()
    {
        var settings = new HttpUploadOptions { Enabled = true, RequireHttps = false,
            Key = new string('k', 32), MaxUploadBytes = 40000000 };
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IPluginManager>(new Manager(true));
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<HttpUploadOptions>>(
            new StaticOptionsMonitor<HttpUploadOptions>(settings));
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        new HttpUploadPlugin().ConfigureProxyStage(app, PluginProxyStage.First);
        app.Run(async context => {
            await context.Request.Body.CopyToAsync(Stream.Null);
            context.Response.StatusCode = 204;
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var body = new ByteArrayContent(new byte[31000000]);
            using var response = await client.PostAsync("/_upload", body);
            Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("/_upload", true, 500065536)]
    [InlineData("/ordinary", true, 30000000)]
    [InlineData("/_upload", false, 30000000)]
    public async Task UploadBodyLimitIsAppliedBeforeNextReader(string path, bool enabled, long expected)
    {
        var settings = new HttpUploadOptions { Enabled = enabled, Key = new string('k', 32), MaxUploadBytes = 500000000 };
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "POST";
        context.Request.Scheme = "https";
        var feature = new BodyLimit();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(feature);
        var called = false;
        await new UploadRequestLimitMiddleware(ctx => {
            called = true;
            Assert.Equal(expected, feature.MaxRequestBodySize);
            return Task.CompletedTask;
        }).InvokeAsync(context, new Manager(true), new StaticOptionsMonitor<HttpUploadOptions>(settings));
        Assert.True(called);
    }

    private sealed class BodyLimit : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = 30000000;
    }

    [Theory]
    [InlineData("1048576000", "5242880000", true)]
    [InlineData("1099511627776", "1099511627776", true)]
    [InlineData("1099511627777", "5242880000", false)]
    [InlineData("0", "5242880000", false)]
    [InlineData("1048576000", "9223372036854775807", false)]
    public void LargeLimitsWithBlankKeyReturnValidationResults(string upload, string extracted, bool valid)
    {
        const string saved = "saved-key-012345678901234567890123456789";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Plugins:http-upload:Key"] = saved }).Build();
        object?[] args = [new HttpUploadPlugin(), new Dictionary<string, string> {
            ["Key"] = "", ["NonceDirectory"] = "plugin_data/http-upload/nonces",
            ["MaxUploadBytes"] = upload, ["MaxExtractedBytes"] = extracted
        }, null, config];
        var method = typeof(LyWaf.Control.ControlApi).GetMethod("PreparePluginConfiguration",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var errors = (Dictionary<string, string[]>)method.Invoke(null, args)!;
        Assert.Equal(valid, errors.Count == 0);
        Assert.Equal(saved, ((Dictionary<string, string>)args[2]!)["Key"]);
        if (!valid) Assert.Contains(errors.Keys, key => key is "maxUploadBytes" or "maxExtractedBytes" or "MaxUploadBytes" or "MaxExtractedBytes");
    }

    [Theory]
    [InlineData("")]
    [InlineData("new-key-012345678901234567890123456789")]
    public void KeyCanBeReplacedOrPreserved(string submitted)
    {
        const string saved = "saved-key-012345678901234567890123456789";
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Plugins:http-upload:Key"] = saved }).Build();
        var plugin = new HttpUploadPlugin();
        object?[] args = [plugin, new Dictionary<string, string> {
            ["Key"] = submitted, ["NonceDirectory"] = "plugin_data/http-upload/nonces"
        }, null, config];
        var method = typeof(LyWaf.Control.ControlApi).GetMethod("PreparePluginConfiguration",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var errors = (Dictionary<string, string[]>)method.Invoke(null, args)!;
        Assert.Empty(errors);
        Assert.Equal(submitted.Length == 0 ? saved : submitted, ((Dictionary<string, string>)args[2]!)["Key"]);
    }

    [Theory]
    [InlineData("upload.example.com", "upload.example.com", true)]
    [InlineData("UPLOAD.example.com.", "upload.example.com", true)]
    [InlineData("other.example.com", "upload.example.com", false)]
    [InlineData("upload.example.com.evil.test", "upload.example.com", false)]
    [InlineData("other.example.com", "*", true)]
    public void UploadHostAllowlistMatchesExactly(string host, string allowed, bool expected) =>
        Assert.Equal(expected, HttpUploadMiddleware.IsHostAllowed(host, [allowed]));

    [Fact]
    public async Task UnlistedHostIsRejectedBeforeReadingBody()
    {
        Assert.False(HttpUploadMiddleware.IsHostAllowed("example.com", []));
        var settings = new HttpUploadOptions { Enabled = true, Key = new string('k', 32), Hosts = ["upload.example.com"] };
        var middleware = new HttpUploadMiddleware(_ => throw new Exception("Must not forward"),
            new StaticOptionsMonitor<HttpUploadOptions>(settings), new Manager(true), NullLogger<HttpUploadMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/_upload";
        context.Request.Host = new HostString("other.example.com", 443);
        context.Request.Scheme = "https";
        await middleware.InvokeAsync(context);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.True(HttpUploadMiddleware.IsHostAllowed(new HostString("upload.example.com", 8443).Host, settings.Hosts));
    }

    [Theory]
    [InlineData("cc", true, "cc")]
    [InlineData("cc", false, "cc/a.zip")]
    [InlineData("cc/releases", true, "cc/releases")]
    [InlineData("", true, "a.zip")]
    public void ExplicitZipDirectoryDoesNotAddArchiveName(string directory, bool extract, string expected)
    {
        var root = Path.GetFullPath("upload-test-root");
        Assert.Equal(Path.Combine(root, expected.Replace('/', Path.DirectorySeparatorChar)),
            HttpUploadMiddleware.ResolveDestination(root, directory, "a.zip", extract));
    }

    [Theory]
    [InlineData("../cc")]
    [InlineData("/cc")]
    [InlineData("C:/cc")]
    [InlineData("cc\\..\\outside")]
    [InlineData("cc//nested")]
    public void DestinationCannotEscapeConfiguredRoot(string directory) =>
        Assert.Throws<InvalidDataException>(() => HttpUploadMiddleware.ResolveDestination(
            Path.GetTempPath(), directory, "a.zip", true));

    [Theory]
    [InlineData(false, true, 403)]
    [InlineData(true, false, 503)]
    public async Task RejectsUnauthorizedRequests(bool https, bool granted, int expected)
    {
        var settings = new HttpUploadOptions { Enabled = true, Key = new string('k', 32) };
        var middleware = new HttpUploadMiddleware(_ => throw new Exception("Should not forward"),
            new StaticOptionsMonitor<HttpUploadOptions>(settings), new Manager(granted),
            NullLogger<HttpUploadMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/_upload";
        context.Request.Method = "POST";
        context.Request.Scheme = https ? "https" : "http";
        await middleware.InvokeAsync(context);
        Assert.Equal(expected, context.Response.StatusCode);
    }

    private sealed class Manager(bool granted) : IPluginManager
    {
        public ILyWafPlugin? GetPlugin(string id) => null;
        public IReadOnlyList<ILyWafPlugin> GetAllPlugins() => [];
        public IReadOnlyList<ILyWafPlugin> GetSystemPlugins() => [];
        public bool IsSystemPlugin(string id) => false;
        public Task EnablePluginAsync(string id) => Task.CompletedTask;
        public Task DisablePluginAsync(string id) => Task.CompletedTask;
        public Task ReloadPluginAsync(string id) => Task.CompletedTask;
        public PluginState GetPluginState(string id) => PluginState.Running;
        public bool IsPermissionGranted(string id, PluginPermission permission) => granted;
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:\\escape")]
    [InlineData("foo:bar")]
    [InlineData("CON.txt")]
    [InlineData("..")]
    [InlineData("bad.")]
    [InlineData(".upload-reserved")]
    public void RejectsUnsafeNames(string name) => Assert.False(HttpUploadMiddleware.IsSafeName(name));

    [Fact]
    public async Task StreamLimitIsEnforcedWithoutContentLength()
    {
        using var source = new MemoryStream(new byte[100]);
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<HttpUploadMiddleware.UploadLimitException>(() =>
            HttpUploadMiddleware.CopyBoundedAsync(source, output, 99, default));
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData("../escape", 100, false)]
    [InlineData("/absolute", 100, false)]
    [InlineData("a\\..\\escape", 100, false)]
    [InlineData("folder/file.txt", 100, true)]
    [InlineData("folder/file.txt", 1, false)]
    public async Task ZipPathsAndSizeAreValidated(string name, long maxSize, bool success)
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-upload-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "test.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("hello");
            var destination = Path.Combine(root, "result");
            Directory.CreateDirectory(destination);
            var task = HttpUploadMiddleware.ExtractAsync(zip, destination,
                new HttpUploadOptions { MaxExtractedBytes = maxSize }, default);
            if (success)
            {
                await task;
                Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "folder", "file.txt")));
            }
            else if (maxSize == 1) await Assert.ThrowsAsync<HttpUploadMiddleware.UploadLimitException>(() => task);
            else await Assert.ThrowsAsync<InvalidDataException>(() => task);
        }
        finally { Directory.Delete(root, true); }
    }
}
