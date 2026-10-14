using System.Globalization;
using LyWaf.Localization;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Sys.HttpUpload;
using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void PluginResourcesAreLoadedFromTheirAssemblyAndNotTheHostCatalog()
    {
        var upload = PluginLocalization.Load(new HttpUploadPlugin());
        var auth = PluginLocalization.Load(new LyWaf.Plugins.Sys.SiteAuth.SiteAuthPlugin());
        Assert.Equal("Upload root directory", upload["en-US"]["plugins.http-upload.Directory.label"]);
        Assert.Equal("Cookie name", auth["en-US"]["fields.CookieName"]);
        Assert.False(TextLocalizer.HasKey("fields.CookieName"));
        Assert.False(upload["en-US"].ContainsKey("fields.CookieName"));
        var field = PluginConfigurationSchema.Describe(typeof(HttpUploadOptions)).Single(field => field.Path == "Directory");
        Assert.NotEqual(field.Label, PluginLocalization.Localize(field, upload).Label);
    }
    [Theory]
    [InlineData(null, "zh-CN")]
    [InlineData("en-GB,en;q=0.8", "en-US")]
    [InlineData("zh;q=0.2,en;q=0.9", "en-US")]
    [InlineData("en;q=0,zh-CN", "zh-CN")]
    [InlineData("fr-FR", "zh-CN")]
    [InlineData("bad;q=broken", "zh-CN")]
    public void NegotiatesSupportedLanguage(string? header, string expected) =>
        Assert.Equal(expected, TextLocalizer.ResolveLanguage(header));

    [Fact]
    public void UnsupportedLanguageUsesConfiguredDefault() =>
        Assert.Equal("en-US", TextLocalizer.ResolveLanguage("de", "en-US"));

    [Fact]
    public async Task ConcurrentRequestsHaveIsolatedLanguageAndPreserveProtocol()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseManagementLocalization(7030, "zh-CN");
        app.Run(async context =>
        {
            await Task.Yield();
            var schema = PluginConfigurationSchema.Describe(typeof(HttpUploadOptions));
            var key = Assert.Single(schema, field => field.Path == "Key");
            Assert.True(key.Sensitive);
            Assert.Equal("fields.Key", key.LabelKey);
            var error = ManagementApiContract.CreateError(context, "internal_error", "管理 API 内部错误");
            Assert.Equal("internal_error", error.Error.Code);
            Assert.Equal(context.TraceIdentifier, error.Error.TraceId);
            context.Items["message"] = error.Message;
            context.Items["culture"] = CultureInfo.CurrentCulture.Name;
        });
        var pipeline = app.Build();
        var culture = CultureInfo.CurrentCulture.Name;
        var original = CultureInfo.CurrentUICulture;
        var contexts = new[] { "en-US", "zh-CN" }.Select(language =>
        {
            var context = new DefaultHttpContext();
            context.Connection.LocalPort = 7030;
            context.Request.Headers.AcceptLanguage = language;
            return context;
        }).ToArray();
        await Task.WhenAll(contexts.Select(pipeline.Invoke));
        Assert.NotEqual(contexts[0].Items["message"], contexts[1].Items["message"]);
        Assert.All(contexts, context => Assert.Equal(culture, context.Items["culture"]));
        Assert.Equal(original, CultureInfo.CurrentUICulture);
    }

    [Fact]
    public void UnknownTextIsPreserved()
    {
        Assert.Equal("custom.path", TextLocalizer.Get("custom.path"));
        Assert.Equal("user supplied text", TextLocalizer.Display("user supplied text"));
    }
}
