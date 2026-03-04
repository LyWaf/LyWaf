using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NLog;

namespace LyWaf.Services.Auth;

/// <summary>
/// 认证结果
/// </summary>
public class AuthResult
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string? Username { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? Message { get; set; }
    /// <summary>暴力破解锁定剩余秒数（0=未锁定）</summary>
    public int RetryAfterSeconds { get; set; }
}

/// <summary>
/// 认证配置（.lywaf.auth.json 文件结构）
/// </summary>
public class AuthConfig
{
    public string Username { get; set; } = "LyWaf";
    /// <summary>SHA256(password) hex，用于登录验证</summary>
    public string PasswordHash { get; set; } = "";
    public string JwtSecret { get; set; } = "";
    public int TokenExpiryHours { get; set; } = 24;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// 登录失败追踪（内存中，按用户名）
/// </summary>
internal class LoginAttemptInfo
{
    public int FailCount { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime LastFailTime { get; set; }
}

/// <summary>
/// 认证服务接口
/// </summary>
public interface IAuthService
{
    /// <summary>初始化认证配置（加载或创建 .lywaf.auth.json）</summary>
    void Initialize();

    /// <summary>
    /// 验证登录哈希，返回 JWT token。
    /// passwordHash = SHA256(SHA256(password) + timestamp)
    /// </summary>
    AuthResult Login(string username, string passwordHash, long timestamp);

    /// <summary>验证 JWT token，返回是否有效</summary>
    bool ValidateToken(string token);

    /// <summary>刷新 token</summary>
    AuthResult RefreshToken(string token);

    /// <summary>修改密码（需认证，明文传入）</summary>
    bool ChangePassword(string currentPassword, string newPassword);

    /// <summary>从 token 中提取用户名</summary>
    string? GetUsername(string token);

    /// <summary>获取服务器当前 Unix 时间戳（秒）</summary>
    long GetServerTimestamp();

    /// <summary>
    /// 直接验证用户名密码（用于 HTTP Basic Auth / curl 快速鉴权）。
    /// 同样受暴力破解防护限制。
    /// </summary>
    AuthResult ValidateCredentials(string username, string password);

    /// <summary>
    /// 重置密码（CLI 命令行用）。生成新随机密码，打印到控制台。
    /// </summary>
    string ResetPassword();
}

/// <summary>
/// JWT 认证服务实现
/// </summary>
public class AuthService : IAuthService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private static readonly string AuthFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".lywaf.auth.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private AuthConfig _config = new();
    private byte[] _jwtSecretBytes = [];
    private DateTime _configLoadedAt = DateTime.MinValue;
    private const int ConfigRefreshSeconds = 5;

    /// <summary>暴力破解防护：按用户名追踪连续失败次数</summary>
    private readonly ConcurrentDictionary<string, LoginAttemptInfo> _loginAttempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>连续失败次数上限</summary>
    private const int MaxFailedAttempts = 5;

    /// <summary>锁定冷却时间（秒）</summary>
    private const int LockoutSeconds = 60;

    /// <summary>时间戳允许误差（秒）</summary>
    private const int TimestampToleranceSeconds = 60;

    // =============== 初始化 ===============

    public void Initialize()
    {
        if (File.Exists(AuthFilePath))
        {
            try
            {
                var json = File.ReadAllText(AuthFilePath, Encoding.UTF8);
                _config = JsonSerializer.Deserialize<AuthConfig>(json, JsonOptions) ?? new AuthConfig();
                _jwtSecretBytes = Convert.FromBase64String(_config.JwtSecret);
                _configLoadedAt = DateTime.UtcNow;
                _logger.Info("已加载认证配置 ({File})", AuthFilePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "加载认证配置失败，将重新生成");
                CreateDefaultConfig();
            }
        }
        else
        {
            CreateDefaultConfig();
        }
    }

    private void CreateDefaultConfig()
    {
        // 生成随机密码（16字符）
        var passwordChars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var passwordBytes = RandomNumberGenerator.GetBytes(16);
        var password = new string(passwordBytes.Select(b => passwordChars[b % passwordChars.Length]).ToArray());

        // 生成 JWT 密钥（64字节）
        _jwtSecretBytes = RandomNumberGenerator.GetBytes(64);

        _config = new AuthConfig
        {
            Username = "LyWaf",
            PasswordHash = ComputeSha256(password),
            JwtSecret = Convert.ToBase64String(_jwtSecretBytes),
            TokenExpiryHours = 24,
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = null
        };

        SaveConfig();

        // 打印默认密码到控制台和日志
        var banner = $"""

            ╔══════════════════════════════════════╗
            ║     LyWaf 控制台默认登录信息         ║
            ╠══════════════════════════════════════╣
            ║  用户名: LyWaf                       ║
            ║  密  码: {password,-29}║
            ╠══════════════════════════════════════╣
            ║  请登录后尽快修改密码！               ║
            ╚══════════════════════════════════════╝
            """;
        Console.WriteLine(banner);
        _logger.Warn("控制台初始密码已生成，用户名: LyWaf，密码: {Password}", password);
    }

    private void SaveConfig()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            File.WriteAllText(AuthFilePath, json, Encoding.UTF8);
            _configLoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存认证配置失败");
        }
    }

    /// <summary>
    /// 检查配置是否过期（超过 5 秒），过期则从文件重新加载
    /// </summary>
    private void ReloadConfigIfStale()
    {
        if ((DateTime.UtcNow - _configLoadedAt).TotalSeconds < ConfigRefreshSeconds)
            return;

        if (!File.Exists(AuthFilePath))
            return;

        try
        {
            var json = File.ReadAllText(AuthFilePath, Encoding.UTF8);
            var newConfig = JsonSerializer.Deserialize<AuthConfig>(json, JsonOptions);
            if (newConfig != null)
            {
                _config = newConfig;
                _jwtSecretBytes = Convert.FromBase64String(_config.JwtSecret);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "定时重新加载认证配置失败，继续使用旧配置");
        }

        _configLoadedAt = DateTime.UtcNow;
    }

    // =============== SHA256 工具 ===============

    /// <summary>
    /// 计算 SHA256 哈希（小写 hex）
    /// </summary>
    private static string ComputeSha256(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    // =============== 暴力破解防护 ===============

    private int GetLockoutRemainingSeconds(string username)
    {
        if (!_loginAttempts.TryGetValue(username, out var info)) return 0;
        if (info.LockedUntil == null) return 0;

        var remaining = (info.LockedUntil.Value - DateTime.UtcNow).TotalSeconds;
        if (remaining <= 0)
        {
            info.FailCount = 0;
            info.LockedUntil = null;
            return 0;
        }

        return (int)Math.Ceiling(remaining);
    }

    private void RecordLoginFailure(string username)
    {
        var info = _loginAttempts.GetOrAdd(username, _ => new LoginAttemptInfo());
        info.FailCount++;
        info.LastFailTime = DateTime.UtcNow;

        if (info.FailCount >= MaxFailedAttempts)
        {
            info.LockedUntil = DateTime.UtcNow.AddSeconds(LockoutSeconds);
            _logger.Warn("用户 {Username} 连续登录失败 {Count} 次，已锁定 {Seconds} 秒",
                username, info.FailCount, LockoutSeconds);
        }
    }

    private void ResetLoginAttempts(string username)
    {
        _loginAttempts.TryRemove(username, out _);
    }

    // =============== 服务器时间 ===============

    public long GetServerTimestamp()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    // =============== 登录 ===============

    /// <summary>
    /// 验证登录。前端发送 passwordHash = SHA256(SHA256(password) + timestamp.toString())
    /// 服务端用存储的 PasswordHash + timestamp 计算期望值进行比对。
    /// </summary>
    public AuthResult Login(string username, string passwordHash, long timestamp)
    {
        ReloadConfigIfStale();

        // 1. 检查暴力破解锁定
        var lockoutSeconds = GetLockoutRemainingSeconds(username);
        if (lockoutSeconds > 0)
        {
            _logger.Warn("用户 {Username} 登录被拒绝：冷却中，剩余 {Seconds} 秒", username, lockoutSeconds);
            return new AuthResult
            {
                Success = false,
                Message = $"登录失败次数过多，请 {lockoutSeconds} 秒后再试",
                RetryAfterSeconds = lockoutSeconds
            };
        }

        // 2. 验证时间戳（防协议重放）
        var serverTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timeDiff = Math.Abs(serverTime - timestamp);
        if (timeDiff > TimestampToleranceSeconds)
        {
            _logger.Warn("登录失败：时间戳偏差过大 ({Diff}s)，客户端={ClientTs}，服务端={ServerTs}",
                timeDiff, timestamp, serverTime);
            return new AuthResult
            {
                Success = false,
                Message = "请求已过期，请刷新页面后重试"
            };
        }

        // 3. 验证用户名
        if (!string.Equals(username, _config.Username, StringComparison.OrdinalIgnoreCase))
        {
            RecordLoginFailure(username);
            _logger.Warn("登录失败：用户名不存在 ({Username})", username);
            return new AuthResult { Success = false, Message = "用户名或密码错误" };
        }

        // 4. 验证密码哈希：SHA256(storedPasswordHash + timestamp)
        var expectedHash = ComputeSha256(_config.PasswordHash + timestamp.ToString());
        if (!string.Equals(expectedHash, passwordHash, StringComparison.OrdinalIgnoreCase))
        {
            RecordLoginFailure(username);
            _logger.Warn("登录失败：密码错误 ({Username})", username);

            var newLockout = GetLockoutRemainingSeconds(username);
            if (newLockout > 0)
            {
                return new AuthResult
                {
                    Success = false,
                    Message = $"登录失败次数过多，请 {newLockout} 秒后再试",
                    RetryAfterSeconds = newLockout
                };
            }

            return new AuthResult { Success = false, Message = "用户名或密码错误" };
        }

        // 5. 登录成功
        ResetLoginAttempts(username);

        _config.LastLoginAt = DateTime.UtcNow;
        SaveConfig();

        var expiresAt = DateTime.UtcNow.AddHours(_config.TokenExpiryHours);
        var token = GenerateToken(_config.Username, expiresAt);

        _logger.Info("用户 {Username} 登录成功", _config.Username);

        return new AuthResult
        {
            Success = true,
            Token = token,
            Username = _config.Username,
            ExpiresAt = expiresAt
        };
    }

    // =============== Basic Auth 直接鉴权（curl） ===============

    /// <summary>
    /// 直接验证用户名密码（HTTP Basic Auth），受暴力破解防护。
    /// 验证成功返回 Success=true（不生成 token，仅放行请求）。
    /// </summary>
    public AuthResult ValidateCredentials(string username, string password)
    {
        ReloadConfigIfStale();

        // 检查暴力破解锁定
        var lockoutSeconds = GetLockoutRemainingSeconds(username);
        if (lockoutSeconds > 0)
        {
            return new AuthResult
            {
                Success = false,
                Message = $"登录失败次数过多，请 {lockoutSeconds} 秒后再试",
                RetryAfterSeconds = lockoutSeconds
            };
        }

        if (!string.Equals(username, _config.Username, StringComparison.OrdinalIgnoreCase))
        {
            RecordLoginFailure(username);
            return new AuthResult { Success = false, Message = "用户名或密码错误" };
        }

        if (ComputeSha256(password) != _config.PasswordHash)
        {
            RecordLoginFailure(username);
            return new AuthResult { Success = false, Message = "用户名或密码错误" };
        }

        ResetLoginAttempts(username);
        return new AuthResult { Success = true, Username = _config.Username };
    }

    // =============== Token 验证 ===============

    public bool ValidateToken(string token)
    {
        ReloadConfigIfStale();

        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return false;

            var signatureInput = $"{parts[0]}.{parts[1]}";
            var expectedSig = ComputeHmacSha256(signatureInput);
            if (expectedSig != parts[2]) return false;

            var payload = DecodeBase64Url(parts[1]);
            var doc = JsonDocument.Parse(payload);

            if (doc.RootElement.TryGetProperty("exp", out var expEl))
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds(expEl.GetInt64()).UtcDateTime;
                if (DateTime.UtcNow > exp) return false;
            }
            else
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // =============== Token 刷新 ===============

    public AuthResult RefreshToken(string token)
    {
        ReloadConfigIfStale();

        if (!ValidateToken(token)) return new AuthResult { Success = false, Message = "令牌无效" };

        var username = GetUsername(token);
        if (username == null) return new AuthResult { Success = false, Message = "令牌无效" };

        var expiresAt = DateTime.UtcNow.AddHours(_config.TokenExpiryHours);
        var newToken = GenerateToken(username, expiresAt);

        return new AuthResult
        {
            Success = true,
            Token = newToken,
            Username = username,
            ExpiresAt = expiresAt
        };
    }

    // =============== 修改密码 ===============

    public bool ChangePassword(string currentPassword, string newPassword)
    {
        ReloadConfigIfStale();

        if (ComputeSha256(currentPassword) != _config.PasswordHash)
        {
            _logger.Warn("修改密码失败：当前密码错误");
            return false;
        }

        _config.PasswordHash = ComputeSha256(newPassword);
        SaveConfig();
        _logger.Info("用户 {Username} 修改了密码", _config.Username);
        return true;
    }

    // =============== 重置密码（CLI） ===============

    public string ResetPassword()
    {
        ReloadConfigIfStale();

        var passwordChars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var passwordBytes = RandomNumberGenerator.GetBytes(16);
        var newPassword = new string(passwordBytes.Select(b => passwordChars[b % passwordChars.Length]).ToArray());

        _config.PasswordHash = ComputeSha256(newPassword);
        SaveConfig();

        _logger.Info("用户 {Username} 的密码已通过命令行重置", _config.Username);
        return newPassword;
    }

    // =============== 从 Token 提取用户名 ===============

    public string? GetUsername(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            var payload = DecodeBase64Url(parts[1]);
            var doc = JsonDocument.Parse(payload);

            return doc.RootElement.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    // =============== JWT 内部方法 ===============

    private string GenerateToken(string username, DateTime expiresAt)
    {
        var header = EncodeBase64Url("""{"alg":"HS256","typ":"JWT"}""");
        var iat = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        var exp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        var payloadJson = $$$"""{"sub":"{{{username}}}","iat":{{{iat}}},"exp":{{{exp}}}}""";
        var payload = EncodeBase64Url(payloadJson);

        var signature = ComputeHmacSha256($"{header}.{payload}");
        return $"{header}.{payload}.{signature}";
    }

    private string ComputeHmacSha256(string input)
    {
        using var hmac = new HMACSHA256(_jwtSecretBytes);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Base64UrlEncode(hash);
    }

    private static string EncodeBase64Url(string input)
    {
        return Base64UrlEncode(Encoding.UTF8.GetBytes(input));
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string DecodeBase64Url(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
