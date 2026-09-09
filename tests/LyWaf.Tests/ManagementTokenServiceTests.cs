using LyWaf.Services.Auth;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementTokenServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lywaf-token-tests-{Guid.NewGuid():N}");
    private string StorePath => Path.Combine(_directory, "tokens.json");

    public ManagementTokenServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Create_ReturnsSecretOnce_AndPersistsOnlyHash()
    {
        var service = new ManagementTokenService(StorePath);
        var created = service.Create("automation", ["read"], DateTime.UtcNow.AddHours(1));

        Assert.StartsWith("lywaf_pat_", created.Secret);
        Assert.DoesNotContain(created.Secret, File.ReadAllText(StorePath));
        Assert.Equal("automation", service.Authenticate(created.Secret)?.Name);
    }

    [Fact]
    public void Revoke_ImmediatelyRejectsToken()
    {
        var service = new ManagementTokenService(StorePath);
        var created = service.Create("deploy", ["write"], null);

        Assert.True(service.Revoke(created.Token.Id));
        Assert.Null(service.Authenticate(created.Secret));
        Assert.True(service.List().Single().Revoked);
    }

    [Fact]
    public void ExpiredToken_IsRejected()
    {
        var service = new ManagementTokenService(StorePath);
        Assert.Throws<ArgumentException>(() =>
            service.Create("expired", ["read"], DateTime.UtcNow.AddSeconds(-1)));
    }

    [Fact]
    public void InvalidScopes_DefaultToRead()
    {
        var service = new ManagementTokenService(StorePath);
        var created = service.Create("reader", ["unknown"], null);

        Assert.Equal(["read"], created.Token.Scopes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
