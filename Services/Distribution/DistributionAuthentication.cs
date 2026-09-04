using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Distribution;

/// <summary>节点分发请求的 HMAC 验证器，校验请求体摘要、时间窗并拒绝 nonce 重放。</summary>
public sealed class DistributionAuthentication(IOptionsMonitor<DistributionOptions> options)
{
    private readonly ConcurrentDictionary<string, long> _nonces = new(StringComparer.Ordinal);

    public async Task<(T? Value, string? Error)> ReadAndVerifyAsync<T>(
        HttpContext context, string operation, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (!current.Enabled || !current.Role.Equals("Node", StringComparison.OrdinalIgnoreCase))
            return (default, "当前实例不是分发目标节点");
        if (string.IsNullOrWhiteSpace(current.SharedSecret)) return (default, "节点未配置 SharedSecret");
        if (!long.TryParse(context.Request.Headers["X-LyWaf-Timestamp"], out var timestamp))
            return (default, "缺少有效时间戳");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > Math.Clamp(current.AllowedClockSkewSeconds, 5, 300))
            return (default, "请求时间戳超出允许范围");
        var nonce = context.Request.Headers["X-LyWaf-Nonce"].ToString();
        var signature = context.Request.Headers["X-LyWaf-Signature"].ToString();
        if (nonce.Length is < 16 or > 128 || signature.Length != 64) return (default, "签名头无效");
        if (context.Request.ContentLength > current.MaxConfigBytes)
            return (default, "请求体超过节点限制");
        using var memory = new MemoryStream();
        await context.Request.Body.CopyToAsync(memory, cancellationToken);
        if (memory.Length > current.MaxConfigBytes) return (default, "请求体超过节点限制");
        var body = memory.ToArray();
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = $"{operation}\n{timestamp}\n{nonce}\n{bodyHash}";
        var expected = ComputeSignature(current.SharedSecret, canonical);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToLowerInvariant())))
            return (default, "签名无效");
        foreach (var stale in _nonces.Where(x => now - x.Value > current.AllowedClockSkewSeconds * 2))
            _nonces.TryRemove(stale.Key, out _);
        if (!_nonces.TryAdd(nonce, timestamp)) return (default, "nonce 已使用");
        try { return (JsonSerializer.Deserialize<T>(body, JsonOptions), null); }
        catch (JsonException ex) { return (default, $"JSON 无效: {ex.Message}"); }
    }

    public static void Sign(HttpRequestMessage request, string operation, string secret, ReadOnlySpan<byte> body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var signature = ComputeSignature(secret, $"{operation}\n{timestamp}\n{nonce}\n{bodyHash}");
        request.Headers.Add("X-LyWaf-Timestamp", timestamp);
        request.Headers.Add("X-LyWaf-Nonce", nonce);
        request.Headers.Add("X-LyWaf-Signature", signature);
    }

    private static string ComputeSignature(string secret, string canonical)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
