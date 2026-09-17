using LyWaf.Plugins.Core;
using LyWaf.Plugins.Sys;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LyWaf.Config;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace LyWaf.Tests;

public class PluginScopedConfigurationTests
{
    [Fact]
    public void DocumentedLyExampleLoadsThroughRealProvider()
    {
        var example = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/examples/plugin-scopes.ly"));
        var configuration = new ConfigurationBuilder().Add(new LyConfigSource { FilePath = example, Optional = false }).Build();
        Assert.Equal("analysis", configuration["Plugins:Load:0"]);
        using var resolver = new PluginScopedConfiguration(configuration,
            [new ImageProcessPlugin().Metadata, new PluginMetadata { Id = "analysis", Name = "analysis" }, new PluginMetadata { Id = "web-tamper", Name = "web-tamper" }],
            NullLogger<PluginScopedConfiguration>.Instance);
        var options = resolver.Get<ImageProcessOptions>(Request(), "image-process");
        Assert.Equal(60, options.DefaultQuality);
        Assert.Equal(800, options.MaxWidth);
        Assert.Equal(["png"], options.SupportedFormats);
        Assert.False(resolver.Get<ImageProcessOptions>(Request(path: "/original/a.png"), "image-process").Enabled);
    }

    [Fact]
    public async Task ImageMiddlewareActuallyUsesScopedWidthAndEnablement()
    {
        var data = Config();
        data["Sites:images:Routes:thumb:Plugins:image-process:MaxWidth"] = "2";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        using var resolver = Create(configuration);
        using var services = new ServiceCollection().AddSingleton<IPluginRequestConfiguration>(resolver).BuildServiceProvider();
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        var middleware = new ImageProcessMiddleware(async context =>
        {
            context.Response.ContentType = "image/png";
            await context.Response.Body.WriteAsync(bytes);
        }, configuration);
        foreach (var host in new[] { "img.example.com", "other.example.com" })
        {
            var context = Request(host);
            context.RequestServices = services;
            context.Request.QueryString = new QueryString("?w=8&f=png");
            using var output = new MemoryStream();
            context.Response.Body = output;
            await middleware.InvokeAsync(context);
            using var decoded = SKBitmap.Decode(output.ToArray());
            Assert.Equal(host == "img.example.com" ? 2 : 8, decoded.Width);
        }
    }

    [Fact]
    public void ExplicitLoadSeparatesLifecycleFromRequestEnabled()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Config()).Build();
        var options = configuration.GetSection("Plugins").Get<PluginOptions>()!;
        var manager = new PluginManager(configuration, new PluginEventBus(), new StaticOptionsMonitor<PluginOptions>(options));
        manager.DiscoverPlugins();
        Assert.NotNull(manager.GetPlugin("image-process"));
        Assert.Null(manager.GetPlugin("analysis"));
        Assert.True(manager.GetPluginEnabledState("image-process", false, false));
        options.EnabledOverrides["image-process"] = false;
        Assert.False(manager.GetPluginEnabledState("image-process", false, false));
    }
    private static Dictionary<string, string?> Config() => new()
    {
        ["Plugins:Load:0"] = "image-process",
        ["Plugins:image-process:Enabled"] = "false",
        ["Plugins:image-process:DefaultQuality"] = "85",
        ["Sites:images:Hosts:0"] = "img.example.com",
        ["Sites:images:Plugins:image-process:Enabled"] = "true",
        ["Sites:images:Plugins:image-process:DefaultQuality"] = "80",
        ["Sites:images:Plugins:image-process:SupportedFormats:0"] = "png",
        ["Sites:images:Routes:thumb:Path"] = "/thumb/**",
        ["Sites:images:Routes:thumb:Plugins:image-process:DefaultQuality"] = "60",
        ["Sites:images:Routes:thumb:Plugins:image-process:MaxWidth"] = "800",
        ["Sites:images:Routes:thumb:Plugins:image-process:CacheSeconds"] = "0"
    };

    private static PluginScopedConfiguration Create(IConfiguration configuration) => new(configuration,
        [new ImageProcessPlugin().Metadata], NullLogger<PluginScopedConfiguration>.Instance);
    private static DefaultHttpContext Request(string host = "img.example.com", string path = "/thumb/a.png")
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        return context;
    }

    [Theory]
    [InlineData("other.example.com", "/thumb/a.png", false, 85, 4096, 3600)]
    [InlineData("img.example.com", "/a.png", true, 80, 4096, 3600)]
    [InlineData("img.example.com", "/thumbnail/a.png", true, 80, 4096, 3600)]
    [InlineData("img.example.com", "/thumb/a.png", true, 60, 800, 0)]
    [InlineData("IMG.example.com:8080", "/thumb", true, 60, 800, 0)]
    public void InheritsAndOverrides(string host, string path, bool enabled, int quality, int width, int cache)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Config()).Build();
        using var resolver = Create(configuration);
        var options = resolver.Get<ImageProcessOptions>(Request(host, path), "image-process");
        Assert.Equal(enabled, options.Enabled);
        Assert.Equal(quality, options.DefaultQuality);
        Assert.Equal(width, options.MaxWidth);
        Assert.Equal(cache, options.CacheSeconds);
        if (enabled) Assert.Equal(["png"], options.SupportedFormats);
    }

    [Fact]
    public void EmptyCollectionReplacesAndRequestOptionsAreDetached()
    {
        var data = Config();
        data["Sites:images:Routes:thumb:Plugins:image-process:SupportedFormats"] = "";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        using var resolver = Create(configuration);
        var first = resolver.Get<ImageProcessOptions>(Request(), "image-process");
        Assert.Empty(first.SupportedFormats);
        first.DefaultQuality = 2;
        Assert.Equal(60, resolver.Get<ImageProcessOptions>(Request(), "image-process").DefaultQuality);
    }

    [Fact]
    public void ReloadIsAtomicAndInvalidCandidateKeepsLastGoodSnapshot()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Config()).Build();
        using var resolver = Create(configuration);
        var request = Request();
        Assert.Equal(60, resolver.Get<ImageProcessOptions>(request, "image-process").DefaultQuality);
        configuration["Sites:images:Routes:thumb:Plugins:image-process:DefaultQuality"] = "50";
        configuration.Reload();
        Assert.Equal(60, resolver.Get<ImageProcessOptions>(request, "image-process").DefaultQuality);
        Assert.Equal(50, resolver.Get<ImageProcessOptions>(Request(), "image-process").DefaultQuality);
        configuration["Sites:images:Routes:thumb:Plugins:image-process:DefaultQuality"] = "101";
        configuration.Reload();
        Assert.NotNull(resolver.LastReloadError);
        Assert.Equal(50, resolver.Get<ImageProcessOptions>(Request(), "image-process").DefaultQuality);
    }

    [Theory]
    [InlineData("Sites:images:Plugins:analysis:Enabled", "true")]
    [InlineData("Sites:images:Plugins:image-process:MaxWidht", "800")]
    [InlineData("Sites:images:Routes:thumb:Path", "/thumb*bad")]
    [InlineData("Sites:images:Hosts:0", "http://img.example.com")]
    [InlineData("Plugins:Load:1", "missing-plugin")]
    public void InvalidConfigurationFailsStartup(string key, string value)
    {
        var data = Config(); data[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        Assert.Throws<InvalidOperationException>(() => Create(configuration));
    }

    [Fact]
    public void ExactHostAndLongestPathWin()
    {
        var data = Config();
        data["Sites:wildcard:Hosts:0"] = "*.example.com";
        data["Sites:wildcard:Plugins:image-process:DefaultQuality"] = "70";
        data["Sites:images:Routes:exact:Path"] = "/thumb/a.png";
        data["Sites:images:Routes:exact:Plugins:image-process:DefaultQuality"] = "40";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        using var resolver = Create(configuration);
        Assert.Equal(40, resolver.Get<ImageProcessOptions>(Request(), "image-process").DefaultQuality);
        Assert.Equal(70, resolver.Get<ImageProcessOptions>(Request("other.example.com"), "image-process").DefaultQuality);
    }

    public class RestrictedOptions
    {
        [PluginConfiguration(Scopes = PluginConfigurationScope.Global)]
        public string Directory { get; set; } = "data";
        [PluginConfiguration(RequiresRestart = true)]
        public int Workers { get; set; } = 1;
        [PluginConfiguration(Sensitive = true)]
        public string Credential { get; set; } = "secret";
    }

    [Fact]
    public void RestartOnlyChangesAreRejectedAndSecretsAreNotExposed()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var resolver = new PluginScopedConfiguration(config,
            [new PluginMetadata { Id = "test", Name = "test", SupportsScopedConfiguration = true, DefaultOptions = new RestrictedOptions() }],
            NullLogger<PluginScopedConfiguration>.Instance);
        Assert.False(resolver.Describe("test", "", "/").Config.ContainsKey("Credential"));
        config["Plugins:test:Workers"] = "2";
        config.Reload();
        Assert.NotNull(resolver.LastReloadError);
        Assert.Equal(1, resolver.Get<RestrictedOptions>(Request(), "test").Workers);
    }

    [Fact]
    public void GlobalOnlyFieldCannotBeOverridden()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sites:test:Hosts:0"] = "example.com", ["Sites:test:Plugins:test:Directory"] = "other"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new PluginScopedConfiguration(config,
            [new PluginMetadata { Id = "test", Name = "test", SupportsScopedConfiguration = true, DefaultOptions = new RestrictedOptions() }],
            NullLogger<PluginScopedConfiguration>.Instance));
    }
}
