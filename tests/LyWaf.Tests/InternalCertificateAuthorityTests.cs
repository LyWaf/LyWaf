using System.Security.Cryptography.X509Certificates;
using LyWaf.Services.Acme;
using Xunit;

namespace LyWaf.Tests;

public sealed class InternalCertificateAuthorityTests
{
    [Fact]
    public async Task CreatesPersistentRootAndReusableLeafCertificate()
    {
        var path = Path.Combine(Path.GetTempPath(), "lywaf-ca-" + Guid.NewGuid().ToString("N"));
        try
        {
            var authority = new InternalCertificateAuthority(new AcmeOptions
            {
                InternalCaEnabled = true, InternalCaPath = path, InternalCertificateDays = 7
            });
            using var first = await authority.GetOrCreateAsync("localhost", CancellationToken.None);
            using var second = await authority.GetOrCreateAsync("localhost", CancellationToken.None);

            Assert.Equal(first.Thumbprint, second.Thumbprint);
            Assert.True(first.HasPrivateKey);
            Assert.True(first.NotAfter > DateTime.UtcNow.AddDays(6));
            Assert.True(File.Exists(Path.Combine(path, "root-ca.pem")));
            Assert.True(File.Exists(Path.Combine(path, "root-ca.key")));
            Assert.Equal(X509ContentType.Cert, X509Certificate2.GetCertContentType(Path.Combine(path, "localhost.pem")));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task AcmeServiceLoadsInternalCertificateIntoRuntimeStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), "lywaf-acme-service-" + Guid.NewGuid().ToString("N"));
        var options = new AcmeOptions
        {
            InternalCaEnabled = true, InternalCaPath = path, CertificatePath = Path.Combine(path, "public"),
            CoordinationLockPath = Path.Combine(path, "locks"), Domains = ["localhost"], CheckIntervalHours = 24
        };
        using var service = new AcmeService(new StaticOptionsMonitor<AcmeOptions>(options));
        try
        {
            await service.StartAsync(CancellationToken.None);
            var first = service.GetCertificate("localhost");
            var second = await service.GetOrRequestCertificateAsync("localhost", CancellationToken.None);
            Assert.NotNull(first);
            Assert.Same(first, second);
            var status = Assert.Single(service.GetCertificateStatuses());
            Assert.Equal("internal", status.Source);
            Assert.Equal("valid", status.Status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
