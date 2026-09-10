using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

namespace LyWaf.Services.Auth;

/// <summary>短期绑定信息，密钥仅在本人重新验证密码后返回。</summary>
public sealed record TotpEnrollment(string Secret, string Uri, DateTime ExpiresAt);

public partial class AuthService
{
    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool IsTotpEnabled(string username)
    {
        ReloadConfigIfStale();
        return _config.Users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && u.TotpSecret != null);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public TotpEnrollment BeginTotp(string username, string password)
    {
        var user = RequirePassword(username, password);
        if (user.TotpSecret != null) throw new ArgumentException("请先解绑已有验证器");
        var secret = RandomNumberGenerator.GetBytes(20);
        user.PendingTotpSecret = EncryptTotp(secret);
        user.PendingTotpExpiresAt = DateTime.UtcNow.AddMinutes(10);
        SaveConfig();
        var base32 = EncodeBase32(secret);
        return new(base32, $"otpauth://totp/LyWaf:{Uri.EscapeDataString(user.Username)}?secret={base32}&issuer=LyWaf&algorithm=SHA1&digits=6&period=30",
            user.PendingTotpExpiresAt.Value);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public string[] ConfirmTotp(string username, string password, string code)
    {
        var user = RequirePassword(username, password);
        if (user.PendingTotpSecret is null || user.PendingTotpExpiresAt is null || user.PendingTotpExpiresAt <= DateTime.UtcNow)
            throw new ArgumentException("绑定已过期，请重新开始");
        var step = MatchTotp(DecryptTotp(user.PendingTotpSecret), code, -1);
        if (step < 0)
        {
            RecordLoginFailure(username);
            throw new ArgumentException("动态码无效");
        }
        var recovery = Enumerable.Range(0, 10).Select(_ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))).ToArray();
        user.RecoveryHashes = recovery.Select(ComputeSha256).ToList();
        user.TotpSecret = user.PendingTotpSecret;
        user.PendingTotpSecret = null;
        user.PendingTotpExpiresAt = null;
        user.LastTotpStep = step;
        user.SessionVersion++;
        SaveConfig();
        return recovery;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool DisableTotp(string username, string password, string code)
    {
        var user = RequirePassword(username, password);
        if (user.TotpSecret == null || !VerifySecondFactor(user, code))
        {
            RecordLoginFailure(username);
            return false;
        }
        user.TotpSecret = null;
        user.RecoveryHashes.Clear();
        user.LastTotpStep = -1;
        user.SessionVersion++;
        SaveConfig();
        return true;
    }

    private ManagementUser RequirePassword(string username, string password)
    {
        ReloadConfigIfStale();
        if (GetLockoutRemainingSeconds(username) > 0) throw new ArgumentException("失败次数过多，请稍后重试");
        var user = _config.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (user is null || user.Disabled || !string.Equals(user.PasswordHash, ComputeSha256(password), StringComparison.Ordinal))
        {
            RecordLoginFailure(username);
            throw new ArgumentException("密码错误");
        }
        return user;
    }

    private bool VerifySecondFactor(ManagementUser user, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var hash = ComputeSha256(code.Trim());
        if (user.RecoveryHashes.Remove(hash)) { SaveConfig(); return true; }
        var step = MatchTotp(DecryptTotp(user.TotpSecret!), code, user.LastTotpStep);
        if (step < 0) return false;
        user.LastTotpStep = step;
        SaveConfig();
        return true;
    }

    private static long MatchTotp(byte[] secret, string code, long lastStep)
    {
        var current = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        for (var step = current - 1; step <= current + 1; step++)
            if (step > lastStep && CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(TotpCode(secret, step)), System.Text.Encoding.ASCII.GetBytes(code))) return step;
        return -1;
    }

    internal static string TotpCode(byte[] secret, long step, int digits = 6)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 15;
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (value % (digits == 8 ? 100000000 : 1000000)).ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    private string EncryptTotp(byte[] secret)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[secret.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(SHA256.HashData(_jwtSecretBytes), 16);
        aes.Encrypt(nonce, secret, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private byte[] DecryptTotp(string encoded)
    {
        var bytes = Convert.FromBase64String(encoded);
        var plain = new byte[bytes.Length - 28];
        using var aes = new AesGcm(SHA256.HashData(_jwtSecretBytes), 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
        return plain;
    }

    private static string EncodeBase32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new System.Text.StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { bits -= 5; result.Append(alphabet[(buffer >> bits) & 31]); }
        }
        if (bits > 0) result.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }
}
