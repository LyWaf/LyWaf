using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace LyWaf.Services.Auth;

public partial class AuthService
{
    /// <summary>五分钟二次验证凭证，仅对原人员会话有效，不能充当登录令牌。</summary>
    [MethodImpl(MethodImplOptions.Synchronized)]
    public string CreateStepUp(string username, string password, string? code, string sessionToken)
    {
        var user = RequirePassword(username, password);
        if (GetIdentity(sessionToken)?.Username != user.Username) throw new ArgumentException("会话无效");
        if (user.TotpSecret != null && !VerifySecondFactor(user, code))
        {
            RecordLoginFailure(username);
            throw new ArgumentException("动态码或恢复码无效");
        }
        ResetLoginAttempts(username);
        var expires = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return expires + "." + StepUpSignature(sessionToken, expires);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool ValidateStepUp(string sessionToken, string? proof)
    {
        if (proof is null || proof.Length > 100 || GetIdentity(sessionToken) is null) return false;
        var parts = proof.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expires <= now || expires > now + 300) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[1]),
            Encoding.ASCII.GetBytes(StepUpSignature(sessionToken, parts[0])));
    }

    private string StepUpSignature(string sessionToken, string expires) => Convert.ToHexStringLower(
        HMACSHA256.HashData(_jwtSecretBytes, Encoding.UTF8.GetBytes("management-step-up:" + ComputeSha256(sessionToken) + ":" + expires)));
}
