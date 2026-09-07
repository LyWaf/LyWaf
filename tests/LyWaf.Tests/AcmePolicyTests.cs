using LyWaf.Services.Acme;
using Xunit;

namespace LyWaf.Tests;

public sealed class AcmePolicyTests
{
    [Fact]
    public void OnDemandRequiresAllowlistAndEnforcesHourlyLimit()
    {
        var options = new AcmeOptions
        {
            OnDemandEnabled = true,
            OnDemandAllowedDomains = ["api.example.com", "*.tenant.example.com"],
            OnDemandMaxRequestsPerHour = 2
        };
        var policy = new AcmeOnDemandPolicy(options);
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        Assert.True(policy.TryAcquire("API.EXAMPLE.COM.", now, out var exact));
        Assert.Equal("api.example.com", exact);
        Assert.True(policy.TryAcquire("a.tenant.example.com", now, out _));
        Assert.False(policy.TryAcquire("b.tenant.example.com", now, out _));
        Assert.False(policy.TryAcquire("example.net", now.AddHours(2), out _));
        Assert.True(policy.TryAcquire("b.tenant.example.com", now.AddHours(2), out _));
    }

    [Fact]
    public void DirectoryUrisPreserveFailoverOrderAndRemoveDuplicates()
    {
        var options = new AcmeOptions
        {
            DirectoryUrls = ["https://ca-a.example/directory", "https://ca-b.example/directory", "https://CA-A.example/directory"]
        };
        var values = AcmeOnDemandPolicy.GetDirectoryUris(options);
        Assert.Equal(2, values.Count);
        Assert.Equal("ca-a.example", values[0].Host);
        Assert.Equal("ca-b.example", values[1].Host);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("https://example.com")]
    [InlineData("")]
    public void InvalidPublicCertificateNamesAreRejected(string value) =>
        Assert.Equal("", AcmeOnDemandPolicy.NormalizeDomain(value));

    [Fact]
    public void LyConfigPreservesAdvancedAcmeOptions()
    {
        var parsed = Config.LyConfigParser.Parse("""
            Acme {
                Enabled = true
                DirectoryUrls = ["https://ca-a.example/directory", "https://ca-b.example/directory"]
                OnDemandEnabled = true
                OnDemandAllowedDomains = ["*.example.com"]
                OnDemandMaxRequestsPerHour = 4
                InternalCaEnabled = true
                CheckRevocationOnline = true
            }
            """);
        var mapped = Config.LyToAppSettingsConverter.TransformToAppSettings(parsed);
        var acme = Assert.IsType<Dictionary<string, object>>(mapped["Acme"]);
        Assert.Equal(true, acme["OnDemandEnabled"]);
        Assert.Equal(4, Convert.ToInt32(acme["OnDemandMaxRequestsPerHour"]));
        Assert.Equal(true, acme["InternalCaEnabled"]);
    }

    [Fact]
    public void RejectsUnsafeOnDemandAndPublicAcmeConfiguration()
    {
        Assert.False(AcmeOnDemandPolicy.IsValidOptions(new AcmeOptions { OnDemandEnabled = true }));
        Assert.False(AcmeOnDemandPolicy.IsValidOptions(new AcmeOptions
        {
            Enabled = true, AcceptTermsOfService = false, Email = "admin@example.com"
        }));
        Assert.True(AcmeOnDemandPolicy.IsValidOptions(new AcmeOptions
        {
            InternalCaEnabled = true, OnDemandEnabled = true, OnDemandAllowedDomains = ["*.example.test"]
        }));
    }
}
