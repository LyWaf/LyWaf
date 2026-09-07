using LyWaf.Services.Acme;
using Xunit;

namespace LyWaf.Tests;

public sealed class CertificateIssuanceLockTests
{
    [Fact]
    public async Task SerializesSameDomainButNotDifferentDomains()
    {
        var path = Path.Combine(Path.GetTempPath(), "lywaf-lock-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var first = await CertificateIssuanceLock.TryAcquireAsync(path, "a.example.com",
                TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(first);
            await using var other = await CertificateIssuanceLock.TryAcquireAsync(path, "b.example.com",
                TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.NotNull(other);
            var blocked = await CertificateIssuanceLock.TryAcquireAsync(path, "a.example.com",
                TimeSpan.FromMilliseconds(150), CancellationToken.None);
            Assert.Null(blocked);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}
