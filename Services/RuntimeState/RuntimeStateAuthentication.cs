using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.RuntimeState;

/// <summary>校验运行状态节点复制请求的 HMAC、时间戳和一次性 nonce。</summary>
public sealed class RuntimeStateAuthentication(IOptionsMonitor<RuntimeStateOptions> options)
{
    private readonly ConcurrentDictionary<string, long> _nonces = new(StringComparer.Ordinal);
    private readonly object _nonceGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(RuntimeStateMutation? Value, string? Error)> ReadAndVerifyAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (!current.Enabled) return (null, "运行状态节点复制未启用");
        var sender = context.Request.Headers["X-LyWaf-Node"].ToString();
        if (sender.Length is < 1 or > 128) return (null, "节点标识无效");
        foreach (var header in new[] { "X-LyWaf-Node", "X-LyWaf-Timestamp", "X-LyWaf-Nonce", "X-LyWaf-Signature" })
            if (context.Request.Headers[header].Count != 1) return (null, "签名头必须提供且只能出现一次");
        var secret = current.Peers.TryGetValue(sender, out var peer) && !string.IsNullOrWhiteSpace(peer.Secret)
            ? peer.Secret : current.SharedSecret;
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(secret)) return (null, "节点或共享密钥未配置");
        if (!long.TryParse(context.Request.Headers["X-LyWaf-Timestamp"], out var timestamp)) return (null, "缺少有效时间戳");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skew = Math.Clamp(current.AllowedClockSkewSeconds, 5, 300);
        if (timestamp < now - skew || timestamp > now + skew) return (null, "请求时间戳超出允许范围");
        var nonce = context.Request.Headers["X-LyWaf-Nonce"].ToString();
        var signature = context.Request.Headers["X-LyWaf-Signature"].ToString();
        if (nonce.Length is < 16 or > 128 || signature.Length != 64) return (null, "签名头无效");
        var maxBytes = Math.Clamp(current.MaxValueBytes * 2L, 4096, 2 * 1024 * 1024);
        if (context.Request.ContentLength > maxBytes) return (null, "请求体超过限制");
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - memory.Length + 1)), cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > maxBytes) return (null, "请求体超过限制");
            await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var body = memory.ToArray();
        var expected = Compute(secret, sender, timestamp, nonce, body);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.ToLowerInvariant())))
            return (null, "签名无效");
        lock (_nonceGate)
        {
            foreach (var stale in _nonces.Where(x => now - x.Value > skew * 2)) _nonces.TryRemove(stale.Key, out _);
            if (_nonces.Count >= 10000) return (null, "签名校验容量已满，请稍后重试");
            if (!_nonces.TryAdd(sender + ":" + nonce, timestamp)) return (null, "nonce 已使用");
        }
        try { return (JsonSerializer.Deserialize<RuntimeStateMutation>(body, JsonOptions), null); }
        catch (JsonException ex) { return (null, $"JSON 无效: {ex.Message}"); }
    }

    public static void Sign(HttpRequestMessage request, string nodeId, string secret, ReadOnlySpan<byte> body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        request.Headers.Add("X-LyWaf-Node", nodeId);
        request.Headers.Add("X-LyWaf-Timestamp", timestamp.ToString());
        request.Headers.Add("X-LyWaf-Nonce", nonce);
        request.Headers.Add("X-LyWaf-Signature", Compute(secret, nodeId, timestamp, nonce, body));
    }

    private static string Compute(string secret, string nodeId, long timestamp, string nonce, ReadOnlySpan<byte> body)
    {
        var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"runtime-state\n{nodeId}\n{timestamp}\n{nonce}\n{hash}"))).ToLowerInvariant();
    }
}
