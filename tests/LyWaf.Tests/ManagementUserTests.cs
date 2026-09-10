using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Auth;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementUserTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "lywaf-auth-" + Guid.NewGuid().ToString("N"));
    private readonly string path;
    private readonly AuthService auth;

    public ManagementUserTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "auth.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new AuthConfig
        {
            Username = "owner", PasswordHash = Hash("owner-password"),
            JwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        auth = new AuthService(path);
        auth.Initialize();
    }

    [Fact]
    public void MigratesOwnerAndProtectsLastAdministrator()
    {
        var owner = Assert.Single(auth.GetUsers());
        Assert.Equal("owner", owner.Username);
        Assert.Empty(owner.PasswordHash);
        Assert.False(auth.SetUserDisabled(owner.Id, true));
        Assert.False(auth.SetUserRole(owner.Id, "auditor"));
        Assert.True(Login("owner", "owner-password").Success);
    }

    [Fact]
    public void CreatedUserDoesNotExposeOrMutateStoredCredentials()
    {
        var user = auth.CreateUser("reader", "reader-password", "auditor");
        Assert.Empty(user.PasswordHash);
        user.Role = "admin";
        user.Disabled = true;
        Assert.Equal("auditor", auth.GetUsers().Single(item => item.Id == user.Id).Role);
        Assert.True(Login("reader", "reader-password").Success);
        Assert.Empty(ManagementPermissions.ForRole("nonexistent-role"));
    }

    [Fact]
    public void RevocationIsPerUserAndPersistsAcrossRestart()
    {
        var reader = auth.CreateUser("reader", "reader-password", "auditor");
        var ownerToken = Login("owner", "owner-password").Token!;
        var readerToken = Login("reader", "reader-password").Token!;
        Assert.DoesNotContain("write", auth.GetIdentity(readerToken)!.Permissions);
        Assert.True(auth.RevokeUserSessions(reader.Id));
        var restarted = new AuthService(path);
        restarted.Initialize();
        Assert.False(restarted.ValidateToken(readerToken));
        Assert.True(restarted.ValidateToken(ownerToken));
    }

    [Fact]
    public void PasswordResetInvalidatesOldPasswordAndSessions()
    {
        var reader = auth.CreateUser("reader", "reader-password", "auditor");
        var token = Login("reader", "reader-password").Token!;
        var replacement = auth.ResetUserPassword(reader.Id)!;
        Assert.False(auth.ValidateToken(token));
        Assert.False(auth.ValidateCredentials("reader", "reader-password").Success);
        Assert.True(auth.ValidateCredentials("reader", replacement).Success);
        Assert.True(auth.SetUserDisabled(reader.Id, true));
        Assert.False(auth.ValidateCredentials("reader", replacement).Success);
    }

    [Fact]
    public void CorruptedConfigurationIsNotOverwritten()
    {
        File.WriteAllText(path, "{broken");
        Assert.ThrowsAny<Exception>(() => new AuthService(path).Initialize());
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    private AuthResult Login(string name, string password, string? secondFactor = null)
    {
        var timestamp = auth.GetServerTimestamp();
        return auth.Login(name, Hash(Hash(password) + timestamp), timestamp, secondFactor);
    }

    [Fact]
    public void CustomRoleChangeRevokesSessionsAndSurvivesRestart()
    {
        auth.SaveRole("operator", ["read", "write"]);
        auth.CreateUser("operator-user", "operator-password", "operator");
        var token = Login("operator-user", "operator-password").Token!;
        Assert.Contains("write", auth.GetIdentity(token)!.Permissions);
        auth.SaveRole("operator", ["read"]);
        Assert.False(auth.ValidateToken(token));
        Assert.False(auth.DeleteRole("operator"));
        var restarted = new AuthService(path);
        restarted.Initialize();
        Assert.DoesNotContain("write", restarted.ValidateCredentials("operator-user", "operator-password").Permissions);
        Assert.Throws<ArgumentException>(() => auth.SaveRole("admin", ["read"]));
    }

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(20000000000L, "65353130")]
    public void TotpMatchesRfc6238(long timestamp, string expected)
    {
        Assert.Equal(expected, AuthService.TotpCode(Encoding.ASCII.GetBytes("12345678901234567890"), timestamp / 30, 8));
    }

    [Fact]
    public void StepUpProofIsBoundToSessionAndRevocation()
    {
        var token = Login("owner", "owner-password").Token!;
        var proof = auth.CreateStepUp("owner", "owner-password", null, token);
        Assert.True(auth.ValidateStepUp(token, proof));
        Assert.False(auth.ValidateStepUp(token, proof + "0"));
        auth.CreateUser("reader", "reader-password", "auditor");
        Assert.False(auth.ValidateStepUp(Login("reader", "reader-password").Token!, proof));
        auth.RevokeUserSessions(auth.GetUsers().Single(u => u.Username == "owner").Id);
        Assert.False(auth.ValidateStepUp(token, proof));
    }

    [Fact]
    public void TotpRevokesSessionsAndRecoveryCodesAreSingleUse()
    {
        var oldToken = Login("owner", "owner-password").Token!;
        var enrollment = auth.BeginTotp("owner", "owner-password");
        var buffer = 0; var bits = 0; var bytes = new List<byte>();
        foreach (var c in enrollment.Secret)
        {
            buffer = (buffer << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c);
            bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); }
        }
        var code = AuthService.TotpCode(bytes.ToArray(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var recovery = auth.ConfirmTotp("owner", "owner-password", code);
        Assert.Equal(10, recovery.Length);
        Assert.False(auth.ValidateToken(oldToken));
        Assert.False(auth.ValidateCredentials("owner", "owner-password").Success);
        Assert.False(Login("owner", "owner-password", code).Success);
        Assert.True(Login("owner", "owner-password", recovery[0]).Success);
        Assert.False(Login("owner", "owner-password", recovery[0]).Success);
        Assert.DoesNotContain(enrollment.Secret, File.ReadAllText(path));
        Assert.DoesNotContain(recovery[1], File.ReadAllText(path));
        Assert.True(auth.DisableTotp("owner", "owner-password", recovery[1]));
        Assert.True(auth.ValidateCredentials("owner", "owner-password").Success);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() => Directory.Delete(directory, true);
}
