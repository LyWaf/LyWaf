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
    public string? Role { get; set; }
    public IReadOnlyCollection<string> Permissions { get; set; } = [];
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
    public string Role { get; set; } = ManagementRoles.Admin;
    public bool Disabled { get; set; }
    public int SessionVersion { get; set; } = 1;
    public List<ManagementUser> Users { get; set; } = [];
    public List<ManagementLoginRecord> LoginHistory { get; set; } = [];
    public List<ManagementRoleDefinition> Roles { get; set; } = [];
}

public sealed class ManagementUser
{
    public string? TotpSecret { get; set; }
    public string? PendingTotpSecret { get; set; }
    public DateTime? PendingTotpExpiresAt { get; set; }
    public long LastTotpStep { get; set; } = -1;
    public List<string> RecoveryHashes { get; set; } = [];
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = ManagementRoles.Auditor;
    public bool Disabled { get; set; }
    public int SessionVersion { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public sealed record ManagementLoginRecord(string Username, DateTime OccurredAt, bool Success, string ClientIp);

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
    AuthResult Login(string username, string passwordHash, long timestamp, string? secondFactor = null);
    TotpEnrollment BeginTotp(string username, string password);
    string[] ConfirmTotp(string username, string password, string code);
    bool DisableTotp(string username, string password, string code);
    bool IsTotpEnabled(string username);
    string CreateStepUp(string username, string password, string? code, string sessionToken);
    bool ValidateStepUp(string sessionToken, string? proof);

    /// <summary>验证 JWT token，返回是否有效</summary>
    bool ValidateToken(string token);

    /// <summary>刷新 token</summary>
    AuthResult RefreshToken(string token);

    /// <summary>修改密码（需认证，明文传入）</summary>
    bool ChangePassword(string username, string currentPassword, string newPassword);

    /// <summary>从 token 中提取用户名</summary>
    string? GetUsername(string token);

    /// <summary>读取并验证令牌中的管理身份。</summary>
    ManagementIdentity? GetIdentity(string token);

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

    /// <summary>获取当前用户名</summary>
    string GetCurrentUsername();

    /// <summary>获取上次登录时间</summary>
    DateTime? GetLastLoginAt();
    IReadOnlyList<ManagementUser> GetUsers();
    IReadOnlyList<ManagementLoginRecord> GetLoginHistory(int limit = 100);
    void RecordLoginAttempt(string username, bool success, string clientIp);
    ManagementUser CreateUser(string username, string password, string role);
    bool SetUserDisabled(string id, bool disabled);
    bool SetUserRole(string id, string role);
    string? ResetUserPassword(string id);
    bool RevokeUserSessions(string id);
    IReadOnlyList<ManagementRoleDefinition> GetRoles();
    void SaveRole(string name, string[] permissions);
    bool DeleteRole(string name);
}

public sealed record ManagementIdentity(string Username, string Role, IReadOnlySet<string> Permissions);

/// <summary>
/// JWT 认证服务实现
/// </summary>
public partial class AuthService : IAuthService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly string AuthFilePath;
    private string? _persistedConfig;

    public AuthService() : this(Path.Combine(Directory.GetCurrentDirectory(), ".lywaf.auth.json")) { }

    internal AuthService(string path) => AuthFilePath = Path.GetFullPath(path);
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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void Initialize()
    {
        if (File.Exists(AuthFilePath))
        {
            try
            {
                var json = File.ReadAllText(AuthFilePath, Encoding.UTF8);
                _config = JsonSerializer.Deserialize<AuthConfig>(json, JsonOptions) ?? new AuthConfig();
                _jwtSecretBytes = Convert.FromBase64String(_config.JwtSecret);
                if (_jwtSecretBytes.Length < 32) throw new InvalidDataException("认证签名密钥无效");
                _persistedConfig = json;
                EnsureUserStore();
                _configLoadedAt = DateTime.UtcNow;
                _logger.Info("已加载认证配置 ({File})", AuthFilePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "加载认证配置失败，保留原文件");
                throw;
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
            LastLoginAt = null,
            Role = ManagementRoles.Admin,
            SessionVersion = 1,
            Users =
            [
                new ManagementUser
                {
                    Username = "LyWaf",
                    PasswordHash = ComputeSha256(password),
                    Role = ManagementRoles.Admin
                }
            ]
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
        _logger.Warn("控制台初始密码已生成，请从启动控制台获取并及时修改");
    }

    private void SaveConfig()
    {
        var temporaryPath = AuthFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            File.WriteAllText(temporaryPath, json, Encoding.UTF8);
            File.Move(temporaryPath, AuthFilePath, true);
            _persistedConfig = json;
            _configLoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存认证配置失败");
            if (_persistedConfig is not null)
                _config = JsonSerializer.Deserialize<AuthConfig>(_persistedConfig, JsonOptions)!;
            throw;
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private void EnsureUserStore()
    {
        if (_config.Users.Count > 0) return;
        _config.Users.Add(new ManagementUser
        {
            Username = _config.Username,
            PasswordHash = _config.PasswordHash,
            Role = string.IsNullOrWhiteSpace(_config.Role) ? ManagementRoles.Admin : _config.Role,
            Disabled = _config.Disabled,
            SessionVersion = Math.Max(1, _config.SessionVersion),
            CreatedAt = _config.CreatedAt,
            LastLoginAt = _config.LastLoginAt
        });
        SaveConfig();
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
                var secret = Convert.FromBase64String(newConfig.JwtSecret);
                if (secret.Length < 32) throw new InvalidOperationException("JWT 密钥长度不足");
                _config = newConfig;
                _jwtSecretBytes = secret;
                _persistedConfig = json;
                EnsureUserStore();
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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public long GetServerTimestamp()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    // =============== 登录 ===============

    /// <summary>
    /// 验证登录。前端发送 passwordHash = SHA256(SHA256(password) + timestamp.toString())
    /// 服务端用存储的 PasswordHash + timestamp 计算期望值进行比对。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public AuthResult Login(string username, string passwordHash, long timestamp, string? secondFactor = null)
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
                Message = LyWaf.Localization.TextLocalizer.Format("api.auth.locked", lockoutSeconds),
                RetryAfterSeconds = lockoutSeconds
            };
        }

        // 2. 验证时间戳（防协议重放）
        var serverTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timeDiff = Math.Abs((decimal)serverTime - timestamp);
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
        var user = _config.Users.FirstOrDefault(item =>
            string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        if (user is null)
        {
            RecordLoginFailure(username);
            _logger.Warn("登录失败：用户名不存在 ({Username})", username);
            return new AuthResult { Success = false, Message = "用户名或密码错误" };
        }

        if (user.Disabled)
        {
            _logger.Warn("登录失败：用户已禁用 ({Username})", username);
            return new AuthResult { Success = false, Message = "用户已禁用" };
        }

        // 4. 验证密码哈希：SHA256(storedPasswordHash + timestamp)
        var expectedHash = ComputeSha256(user.PasswordHash + timestamp.ToString());
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
                    Message = LyWaf.Localization.TextLocalizer.Format("api.auth.locked", newLockout),
                    RetryAfterSeconds = newLockout
                };
            }

            return new AuthResult { Success = false, Message = "用户名或密码错误" };
        }

        if (user.TotpSecret is not null && !VerifySecondFactor(user, secondFactor))
        {
            RecordLoginFailure(username);
            return new AuthResult { Success = false, Message = "请输入有效的动态码或恢复码" };
        }

        // 5. 登录成功
        ResetLoginAttempts(username);

        user.LastLoginAt = DateTime.UtcNow;
        _config.LastLoginAt = user.LastLoginAt;
        SaveConfig();

        var expiresAt = DateTime.UtcNow.AddHours(_config.TokenExpiryHours);
        var token = GenerateToken(user.Username, user.Role, user.SessionVersion, expiresAt);

        _logger.Info("用户 {Username} 登录成功", user.Username);

        return new AuthResult
        {
            Success = true,
            Token = token,
            Username = user.Username,
            ExpiresAt = expiresAt,
            Role = user.Role,
            Permissions = ResolvePermissions(user.Role).ToArray()
        };
    }

    // =============== Basic Auth 直接鉴权（curl） ===============

    /// <summary>
    /// 直接验证用户名密码（HTTP Basic Auth），受暴力破解防护。
    /// 验证成功返回 Success=true（不生成 token，仅放行请求）。
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
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
                Message = LyWaf.Localization.TextLocalizer.Format("api.auth.locked", lockoutSeconds),
                RetryAfterSeconds = lockoutSeconds
            };
        }

        var user = _config.Users.FirstOrDefault(item =>
            string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        if (user is null || user.Disabled)
        {
            RecordLoginFailure(username);
            return new AuthResult { Success = false, Message = "用户名或密码错误" };
        }

        if (user.TotpSecret is not null || ComputeSha256(password) != user.PasswordHash)
        {
            RecordLoginFailure(username);
            return new AuthResult { Success = false, Message = "用户名或密码错误" };
        }

        ResetLoginAttempts(username);
        return new AuthResult
        {
            Success = true,
            Username = user.Username,
            Role = user.Role,
            Permissions = ResolvePermissions(user.Role).ToArray()
        };
    }

    // =============== Token 验证 ===============

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
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
            using var doc = JsonDocument.Parse(payload);

            if (doc.RootElement.TryGetProperty("exp", out var expEl))
            {
                var exp = DateTimeOffset.FromUnixTimeSeconds(expEl.GetInt64()).UtcDateTime;
                if (DateTime.UtcNow > exp) return false;
            }
            else
            {
                return false;
            }

            var sub = doc.RootElement.TryGetProperty("sub", out var subEl) ? subEl.GetString() : null;
            var version = doc.RootElement.TryGetProperty("ver", out var versionEl) ? versionEl.GetInt32() : 0;
            var user = _config.Users.FirstOrDefault(item =>
                string.Equals(item.Username, sub, StringComparison.OrdinalIgnoreCase));
            return user is { Disabled: false } && version == user.SessionVersion;
        }
        catch
        {
            return false;
        }
    }

    // =============== Token 刷新 ===============

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public AuthResult RefreshToken(string token)
    {
        ReloadConfigIfStale();

        if (!ValidateToken(token)) return new AuthResult { Success = false, Message = "令牌无效" };

        var username = GetUsername(token);
        if (username == null) return new AuthResult { Success = false, Message = "令牌无效" };

        var user = _config.Users.First(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        var expiresAt = DateTime.UtcNow.AddHours(_config.TokenExpiryHours);
        var newToken = GenerateToken(username, user.Role, user.SessionVersion, expiresAt);

        return new AuthResult
        {
            Success = true,
            Token = newToken,
            Username = username,
            ExpiresAt = expiresAt,
            Role = user.Role,
            Permissions = ResolvePermissions(user.Role).ToArray()
        };
    }

    // =============== 修改密码 ===============

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public bool ChangePassword(string username, string currentPassword, string newPassword)
    {
        ReloadConfigIfStale();

        var user = _config.Users.FirstOrDefault(item =>
            string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        if (user is null || ComputeSha256(currentPassword) != user.PasswordHash)
        {
            _logger.Warn("修改密码失败：当前密码错误");
            return false;
        }

        user.PasswordHash = ComputeSha256(newPassword);
        user.SessionVersion++;
        SaveConfig();
        _logger.Info("用户 {Username} 修改了密码", user.Username);
        return true;
    }

    // =============== 重置密码（CLI） ===============

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public string ResetPassword()
    {
        ReloadConfigIfStale();
        var admin = _config.Users.First(user => user.Role == ManagementRoles.Admin);
        return ResetPasswordCore(admin);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public IReadOnlyList<ManagementUser> GetUsers()
    {
        ReloadConfigIfStale();
        return _config.Users.OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .Select(user => new ManagementUser
            {
                Id = user.Id, Username = user.Username, Role = user.Role, Disabled = user.Disabled,
                CreatedAt = user.CreatedAt, LastLoginAt = user.LastLoginAt, SessionVersion = user.SessionVersion
            }).ToArray();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public IReadOnlyList<ManagementLoginRecord> GetLoginHistory(int limit = 100)
    {
        ReloadConfigIfStale();
        return _config.LoginHistory.OrderByDescending(item => item.OccurredAt)
            .Take(Math.Clamp(limit, 1, 500)).ToArray();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public void RecordLoginAttempt(string username, bool success, string clientIp)
    {
        ReloadConfigIfStale();
        AddLoginHistory(username, success, clientIp);
        SaveConfig();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public ManagementUser CreateUser(string username, string password, string role)
    {
        ReloadConfigIfStale();
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("用户名不能为空", nameof(username));
        if (password is not { Length: >= 8 and <= 1024 }) throw new ArgumentException("密码长度须为 8 至 1024 字符", nameof(password));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("角色不能为空", nameof(role));
        username = username.Trim();
        role = role.Trim().ToLowerInvariant();
        if (username.Length is < 3 or > 64) throw new ArgumentException("用户名长度必须为 3-64 个字符");
        if (password.Length < 8) throw new ArgumentException("密码长度至少 8 位");
        if (!RoleExists(role)) throw new ArgumentException("角色无效");
        if (_config.Users.Any(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("用户名已存在");
        var user = new ManagementUser { Username = username, PasswordHash = ComputeSha256(password), Role = role };
        _config.Users.Add(user);
        SaveConfig();
        return new ManagementUser { Id = user.Id, Username = user.Username, Role = user.Role,
            CreatedAt = user.CreatedAt, Disabled = user.Disabled, SessionVersion = user.SessionVersion };
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public bool SetUserDisabled(string id, bool disabled)
    {
        ReloadConfigIfStale();
        var user = _config.Users.FirstOrDefault(item => item.Id == id);
        if (user is null || disabled && IsLastEnabledAdmin(user)) return false;
        user.Disabled = disabled;
        if (disabled) user.SessionVersion++;
        SaveConfig();
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public bool SetUserRole(string id, string role)
    {
        ReloadConfigIfStale();
        role = role.Trim().ToLowerInvariant();
        if (!RoleExists(role)) return false;
        var user = _config.Users.FirstOrDefault(item => item.Id == id);
        if (user is null || role != ManagementRoles.Admin && IsLastEnabledAdmin(user)) return false;
        user.Role = role;
        user.SessionVersion++;
        SaveConfig();
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public string? ResetUserPassword(string id)
    {
        ReloadConfigIfStale();
        var user = _config.Users.FirstOrDefault(item => item.Id == id);
        return user is null ? null : ResetPasswordCore(user);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public bool RevokeUserSessions(string id)
    {
        ReloadConfigIfStale();
        var user = _config.Users.FirstOrDefault(item => item.Id == id);
        if (user is null) return false;
        user.SessionVersion++;
        SaveConfig();
        return true;
    }

    private bool IsLastEnabledAdmin(ManagementUser user) =>
        user.Role == ManagementRoles.Admin && !user.Disabled &&
        _config.Users.Count(item => item.Role == ManagementRoles.Admin && !item.Disabled) == 1;

    private string ResetPasswordCore(ManagementUser user)
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(16);
        var password = new string(bytes.Select(value => chars[value % chars.Length]).ToArray());
        user.PasswordHash = ComputeSha256(password);
        user.SessionVersion++;
        SaveConfig();
        _logger.Info("用户 {Username} 的密码已重置", user.Username);
        return password;
    }

    private void AddLoginHistory(string username, bool success, string clientIp)
    {
        _config.LoginHistory.Add(new(username, DateTime.UtcNow, success, clientIp));
        if (_config.LoginHistory.Count > 1000)
            _config.LoginHistory.RemoveRange(0, _config.LoginHistory.Count - 1000);
    }

    // =============== 配置信息查询 ===============

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public string GetCurrentUsername()
    {
        ReloadConfigIfStale();
        return _config.Username;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public DateTime? GetLastLoginAt()
    {
        ReloadConfigIfStale();
        return _config.LastLoginAt;
    }

    // =============== 从 Token 提取用户名 ===============

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public string? GetUsername(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            var payload = DecodeBase64Url(parts[1]);
            using var doc = JsonDocument.Parse(payload);

            return doc.RootElement.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
    public ManagementIdentity? GetIdentity(string token)
    {
        if (!ValidateToken(token)) return null;
        try
        {
            using var document = JsonDocument.Parse(DecodeBase64Url(token.Split('.')[1]));
            var payload = document.RootElement;
            var username = payload.GetProperty("sub").GetString();
            var role = payload.TryGetProperty("role", out var roleElement)
                ? roleElement.GetString() ?? ManagementRoles.Auditor
                : ManagementRoles.Auditor;
            return string.IsNullOrWhiteSpace(username)
                ? null
                : new ManagementIdentity(username, role, ResolvePermissions(role));
        }
        catch { return null; }
    }

    // =============== JWT 内部方法 ===============

    private string GenerateToken(string username, string role, int sessionVersion, DateTime expiresAt)
    {
        var header = EncodeBase64Url("""{"alg":"HS256","typ":"JWT"}""");
        var iat = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        var exp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds();
        var payloadJson = JsonSerializer.Serialize(new { sub = username, role, ver = sessionVersion, iat, exp });
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
