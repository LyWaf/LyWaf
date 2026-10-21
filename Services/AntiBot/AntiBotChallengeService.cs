using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Utils;
using Microsoft.Extensions.Options;
using LyWaf.Services.Captcha;

namespace LyWaf.Services.AntiBot;

public interface IAntiBotChallengeService
{
    string CreateChallenge(HttpContext context);
    Task<string> CreateChallengeAsync(HttpContext context)
        => Task.FromResult(CreateChallenge(context));
    bool VerifyProof(HttpContext context, string token, long counter, out string returnPath);
    Task<(bool Success, string ReturnPath)> VerifyProofAsync(HttpContext context, string token, long counter)
    {
        var success = VerifyProof(context, token, counter, out var path);
        return Task.FromResult((success, path));
    }
    string CreateSession(HttpContext context);
    bool ValidateSession(HttpContext context, string? token);
}

public sealed class AntiBotChallengeService(IOptionsMonitor<AntiBotOptions> options, IServiceProvider? services = null) : IAntiBotChallengeService
{
    private readonly ISharedCaptchaStore? _shared = services?.GetService<ISharedCaptchaStore>();
    private static readonly Dictionary<string, long> ConsumedProofs = new(StringComparer.Ordinal);
    private static readonly byte[] EphemeralSecret = RandomNumberGenerator.GetBytes(32);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string CreateChallenge(HttpContext context)
        => CreateChallengeAsync(context).GetAwaiter().GetResult();

    public async Task<string> CreateChallengeAsync(HttpContext context)
    {
        var current = options.CurrentValue;
        if (_shared is not null && Encoding.UTF8.GetByteCount(current.Secret) < 32)
            throw new CaptchaStoreUnavailableException();
        var token = Sign(new TokenPayload("challenge", DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(current.ChallengeTokenSeconds, 30, 600)).ToUnixTimeSeconds(),
            context.Request.Host.Value ?? "", ClientHash(context), UserAgentHash(context),
            SafeReturnPath(context.Request.PathBase + context.Request.Path + context.Request.QueryString),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16))), current);
        if (_shared is not null)
            await _shared.PutAsync(ProofKey(token), new CaptchaService.CaptchaEntry
                { Mode = "pow", Answer = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), ClientIp = ClientHash(context) },
                context.RequestAborted, Math.Clamp(current.ChallengeTokenSeconds, 30, 600) * 1000);
        return token;
    }

    public bool VerifyProof(HttpContext context, string token, long counter, out string returnPath)
    {
        var result = VerifyProofAsync(context, token, counter).GetAwaiter().GetResult();
        returnPath = result.ReturnPath;
        return result.Success;
    }

    private static string ProofKey(string token) => "p1." + Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(token))[..24]);

    public async Task<(bool Success, string ReturnPath)> VerifyProofAsync(HttpContext context, string token, long counter)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        if (token is not { Length: > 0 and <= 8192 } || !TryValidate(context, token, "challenge", out var payload)) return (false, "/");
        if (_shared is not null && Encoding.UTF8.GetByteCount(options.CurrentValue.Secret) < 32)
            throw new CaptchaStoreUnavailableException();
        var difficulty = Math.Clamp(options.CurrentValue.ProofLeadingZeroNibbles, 1, 6);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{token}:{counter}")));
        if (!hash.StartsWith(new string('0', difficulty), StringComparison.Ordinal)) return (false, "/");
        if (_shared is not null)
        {
            var entry = await _shared.TakeAsync(ProofKey(token), context.RequestAborted);
            context.RequestAborted.ThrowIfCancellationRequested();
            if (entry is null || entry.Mode != "pow" || entry.ClientIp != ClientHash(context)
                || entry.Answer != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))) return (false, "/");
        }
        else lock (ConsumedProofs)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var expired in ConsumedProofs.Where(p => p.Value < now).Select(p => p.Key).ToArray()) ConsumedProofs.Remove(expired);
            if (ConsumedProofs.Count >= 10000 || !ConsumedProofs.TryAdd(ProofKey(token), payload.ExpiresAt)) return (false, "/");
        }
        return (true, SafeReturnPath(payload.ReturnPath));
    }

    public string CreateSession(HttpContext context)
    {
        var current = options.CurrentValue;
        return Sign(new TokenPayload("session", DateTimeOffset.UtcNow.AddMinutes(
            Math.Clamp(current.SessionMinutes, 1, 1440)).ToUnixTimeSeconds(),
            context.Request.Host.Value ?? "", ClientHash(context), UserAgentHash(context), "/", ""), current);
    }

    public bool ValidateSession(HttpContext context, string? token) =>
        !string.IsNullOrWhiteSpace(token) && TryValidate(context, token, "session", out _);

    private bool TryValidate(HttpContext context, string token, string kind, out TokenPayload payload)
    {
        payload = default!;
        var parts = token.Split('.');
        if (parts.Length != 2) return false;
        try
        {
            var body = Base64UrlDecode(parts[0]);
            var actual = Base64UrlDecode(parts[1]);
            var expected = HMACSHA256.HashData(GetSecret(options.CurrentValue), body);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return false;
            payload = JsonSerializer.Deserialize<TokenPayload>(body, JsonOptions)!;
            return payload is not null && payload.Kind == kind && payload.ExpiresAt >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
                   payload.Host.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
                   payload.ClientHash == ClientHash(context) && payload.UserAgentHash == UserAgentHash(context);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return false; }
    }

    private static string Sign(TokenPayload payload, AntiBotOptions options)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return $"{Base64Url(body)}.{Base64Url(HMACSHA256.HashData(GetSecret(options), body))}";
    }

    private static byte[] GetSecret(AntiBotOptions options) => string.IsNullOrWhiteSpace(options.Secret)
        ? EphemeralSecret
        : SHA256.HashData(Encoding.UTF8.GetBytes(options.Secret));
    private static string ClientHash(HttpContext context) => Hash(RequestUtil.GetClientIp(context.Request));
    private static string UserAgentHash(HttpContext context) => Hash(context.Request.Headers.UserAgent.ToString());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    private static string SafeReturnPath(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') && !value.StartsWith("//") ? value : "/";
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    private sealed record TokenPayload(string Kind, long ExpiresAt, string Host, string ClientHash,
        string UserAgentHash, string ReturnPath, string Nonce);
}
