using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyWaf.Services.Auth;

public sealed record ManagementTokenInfo(
    string Id,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    bool Revoked);

public sealed record CreatedManagementToken(ManagementTokenInfo Token, string Secret);

public interface IManagementTokenService
{
    IReadOnlyList<ManagementTokenInfo> List();
    CreatedManagementToken Create(string name, IEnumerable<string> scopes, DateTime? expiresAt);
    bool Revoke(string id);
    ManagementTokenInfo? Authenticate(string secret);
}

/// <summary>
/// 管理 API Token 存储。明文只在创建时返回一次，磁盘仅保存 SHA-256 摘要。
/// </summary>
public sealed class ManagementTokenService : IManagementTokenService
{
    private sealed class TokenRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SecretHash { get; set; } = "";
        public List<string> Scopes { get; set; } = [];
        public DateTime CreatedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public bool Revoked { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly object _gate = new();
    private readonly string _path;
    private List<TokenRecord> _tokens;

    public ManagementTokenService(string? path = null)
    {
        _path = path ?? Path.Combine(Directory.GetCurrentDirectory(), ".lywaf.management-tokens.json");
        _tokens = Load();
    }

    public IReadOnlyList<ManagementTokenInfo> List()
    {
        lock (_gate)
            return _tokens.Select(ToInfo).OrderByDescending(x => x.CreatedAt).ToArray();
    }

    public CreatedManagementToken Create(string name, IEnumerable<string> scopes, DateTime? expiresAt)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100)
            throw new ArgumentException("Token 名称长度必须为 1 到 100 个字符", nameof(name));
        if (expiresAt is not null && expiresAt <= DateTime.UtcNow)
            throw new ArgumentException("过期时间必须晚于当前时间", nameof(expiresAt));

        var normalizedScopes = scopes.Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x is "read" or "write" or "admin")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedScopes.Count == 0)
            normalizedScopes.Add("read");

        var id = Guid.NewGuid().ToString("N");
        var secret = $"lywaf_pat_{id[..8]}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}";
        var record = new TokenRecord
        {
            Id = id,
            Name = name,
            SecretHash = Hash(secret),
            Scopes = normalizedScopes,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt?.ToUniversalTime()
        };
        lock (_gate)
        {
            _tokens.Add(record);
            Save();
        }
        return new CreatedManagementToken(ToInfo(record), secret);
    }

    public bool Revoke(string id)
    {
        lock (_gate)
        {
            var token = _tokens.FirstOrDefault(x => x.Id == id);
            if (token is null || token.Revoked) return false;
            token.Revoked = true;
            Save();
            return true;
        }
    }

    public ManagementTokenInfo? Authenticate(string secret)
    {
        if (!secret.StartsWith("lywaf_pat_", StringComparison.Ordinal) || secret.Length > 160)
            return null;
        var hash = Hash(secret);
        lock (_gate)
        {
            var candidateHash = Convert.FromHexString(hash);
            var token = _tokens.FirstOrDefault(x => IsMatch(x, candidateHash));
            if (token is null) return null;
            var now = DateTime.UtcNow;
            if (token.LastUsedAt is null || now - token.LastUsedAt >= TimeSpan.FromMinutes(1))
            {
                token.LastUsedAt = now;
                Save();
            }
            return ToInfo(token);
        }
    }

    private static bool IsMatch(TokenRecord token, byte[] candidateHash)
    {
        if (token.Revoked || token.ExpiresAt is not null && token.ExpiresAt <= DateTime.UtcNow)
            return false;
        try
        {
            var storedHash = Convert.FromHexString(token.SecretHash);
            return storedHash.Length == candidateHash.Length &&
                   CryptographicOperations.FixedTimeEquals(storedHash, candidateHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private List<TokenRecord> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<TokenRecord>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_tokens, JsonOptions), Encoding.UTF8);
        File.Move(temporary, _path, true);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ManagementTokenInfo ToInfo(TokenRecord token) =>
        new(token.Id, token.Name, token.Scopes, token.CreatedAt, token.ExpiresAt, token.LastUsedAt, token.Revoked);
}
