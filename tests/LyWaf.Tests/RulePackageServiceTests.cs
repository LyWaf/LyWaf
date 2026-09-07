using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.WafRule;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class RulePackageServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lywaf-rule-packages-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048);

    [Fact]
    public async Task InstallsSignedPackageActivatesAndRollsBack()
    {
        var service = CreateService();
        await Install(service, Package("1.0.0", "rule-one"));
        await Install(service, Package("1.1.0", "rule-two"));

        service.Activate("1.0.0");
        Assert.Equal("rule-one", Assert.Single(service.GetRules(new DefaultHttpContext())).Id);
        service.Activate("1.1.0");
        Assert.Equal("1.0.0", service.GetState().PreviousVersion);
        service.Rollback();

        Assert.Equal("1.0.0", service.GetState().StableVersion);
        Assert.Equal("rule-one", Assert.Single(service.GetRules(new DefaultHttpContext())).Id);
    }

    [Fact]
    public async Task RejectsModifiedRulesAfterSigning()
    {
        var service = CreateService();
        var package = Package("2.0.0", "original");
        package.Rules[0].Rule.Id = "modified";

        await Assert.ThrowsAsync<InvalidDataException>(() => Install(service, package));
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task PersistsCanaryStateAndReloadsOnlyVerifiedPackages()
    {
        var service = CreateService();
        await Install(service, Package("3.0.0", "stable"));
        await Install(service, Package("3.1.0", "canary"));
        service.Activate("3.0.0");
        service.Activate("3.1.0", 25);

        var reloaded = CreateService();
        Assert.Equal("3.0.0", reloaded.GetState().StableVersion);
        Assert.Equal("3.1.0", reloaded.GetState().CanaryVersion);
        Assert.Equal(25, reloaded.GetState().CanaryPercentage);
        Assert.Equal(2, reloaded.List().Count);
    }

    private RulePackageService CreateService() => new(Options.Create(new RulePackageOptions
    {
        Directory = _directory,
        TrustedPublicKeys = new() { ["test"] = _key.ExportSubjectPublicKeyInfoPem() },
    }), new StaticHttpClientFactory());

    private SignedRulePackage Package(string version, string ruleId)
    {
        var package = new SignedRulePackage
        {
            PackageId = "community-security",
            Version = version,
            MinHostVersion = "0.0.0",
            PublishedAt = DateTimeOffset.Parse("2026-09-07T00:00:00Z"),
            KeyId = "test",
            Rules = [new PackagedWafRule
            {
                Rule = new WafCustomRule { Id = ruleId, Name = ruleId,
                    Conditions = [new WafCondition { Field = WafMatchField.UriPath, Value = "/admin" }] },
                Cves = ["CVE-2026-0001"], Explanation = "Blocks the vulnerable endpoint",
            }],
        };
        package.RulesSha256 = RulePackageService.ComputeRulesSha256(package.Rules);
        package.Signature = Convert.ToBase64String(_key.SignData(
            Encoding.UTF8.GetBytes(RulePackageService.SigningPayload(package)),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return package;
    }

    private static async Task<RulePackageInfo> Install(RulePackageService service, SignedRulePackage package)
    {
        await using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(package));
        return await service.InstallAsync(stream);
    }

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new EmptyHandler());
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
