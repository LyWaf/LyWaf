using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;

namespace LyWaf.Services.NodeCommunication;

public sealed class NodeMessageAuthentication(IOptionsMonitor<NodeCommunicationOptions> options, NodeIdentityService? identity = null)
{
    public const string Prefix = "/api/v2/node-agent/";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] HeaderNames = ["X-LyWaf-Cluster", "X-LyWaf-Sender", "X-LyWaf-Receiver",
        "X-LyWaf-KeyId", "X-LyWaf-Timestamp", "X-LyWaf-Nonce", "X-LyWaf-Signature"];

    public async Task<(NodeMessage? Message, NodeProtocolError? Error)> ReadAsync(HttpContext context,
        string operation, CancellationToken token)
    {
        static (NodeMessage?, NodeProtocolError?) Reject(string code, int status = 401) => (null, new(code, status));
        NodeCommunicationOptions config;
        try { config = options.CurrentValue; }
        catch (OptionsValidationException) { return Reject("node_identity_unavailable", 503); }
        if (!config.Enabled) return Reject("node_protocol_disabled", 403);
        if (identity is not null)
        {
            try { identity.Capture(); }
            catch (OptionsValidationException) { return Reject("node_identity_unavailable", 503); }
            catch (InvalidOperationException) { return Reject("node_identity_unavailable", 503); }
        }
        if (config.RequireHttps && !context.Request.IsHttps) return Reject("node_https_required", 403);
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (context.Request.Method != "POST" || context.Request.Path.Value != Prefix + operation || context.Request.QueryString.HasValue
            || (!string.IsNullOrEmpty(rawTarget) && !string.Equals(rawTarget, Prefix + operation, StringComparison.Ordinal)))
            return Reject("node_invalid_path", 400);
        if (HeaderNames.Any(name => context.Request.Headers[name].Count != 1)) return Reject("node_invalid_headers");
        var headers = context.Request.Headers;
        string cluster = headers[HeaderNames[0]].ToString(), sender = headers[HeaderNames[1]].ToString(),
            receiver = headers[HeaderNames[2]].ToString(), keyId = headers[HeaderNames[3]].ToString(),
            timestamp = headers[HeaderNames[4]].ToString(), nonce = headers[HeaderNames[5]].ToString(),
            signature = headers[HeaderNames[6]].ToString();
        if (!ValidId(cluster) || !ValidId(sender) || !ValidId(receiver) || !ValidId(keyId)
            || cluster != config.ClusterId || receiver != config.NodeId) return Reject("node_identity_mismatch", 403);
        if (!config.Peers.TryGetValue(sender, out var peer) || !peer.Enabled || !peer.Keys.TryGetValue(keyId, out var secret)
            || string.IsNullOrEmpty(secret) || Encoding.UTF8.GetByteCount(secret) < 32) return Reject("node_unknown_identity");
        var now = DateTimeOffset.UtcNow;
        var skew = Math.Clamp(config.AllowedClockSkewSeconds, 5, 300);
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || Math.Abs((decimal)now.ToUnixTimeSeconds() - seconds) > skew
            || nonce.Length != 32 || !nonce.All(char.IsAsciiHexDigit) || signature.Length != 64 || !signature.All(char.IsAsciiHexDigit))
            return Reject("node_invalid_signature_metadata");
        var limit = Math.Clamp(config.MaxMessageBytes, 1024, 16 * 1024 * 1024);
        if (context.Request.ContentLength > limit) return Reject("node_message_too_large", 413);
        using var buffer = new MemoryStream();
        var bytes = new byte[16384];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, limit - buffer.Length + 1)), token);
            if (read == 0) break;
            if (buffer.Length + read > limit) return Reject("node_message_too_large", 413);
            await buffer.WriteAsync(bytes.AsMemory(0, read), token);
        }
        var body = buffer.ToArray();
        var expected = Compute(secret, operation, cluster, sender, receiver, keyId, timestamp, nonce, body);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(signature)))
            return Reject("node_invalid_signature");
        if (!NodeAdmissionPolicy.Allows(peer, operation)) return Reject("node_admission_denied", 403);
        if ((operation is "configuration.prepare" or "configuration.commit" or "configuration.rollback")
            && !NodeAdmissionPolicy.HasSingleConfigurationWriter(config))
            return Reject("node_operation_forbidden", 403);
        if (!peer.AllowedOperations.Contains(operation, StringComparer.Ordinal)
            || (operation.StartsWith("configuration.", StringComparison.Ordinal) && (peer.Role != "Controller" || config.Role != "Node")))
            return Reject("node_operation_forbidden", 403);
        NodeMessage? message;
        try { message = JsonSerializer.Deserialize<NodeMessage>(body, JsonOptions); }
        catch (JsonException) { return Reject("node_invalid_message", 400); }
        if (message is null || message.ProtocolVersion != 2 || message.ClusterId != cluster || message.SenderNodeId != sender
            || message.ReceiverNodeId != receiver || message.Type != operation || !Guid.TryParseExact(message.MessageId, "D", out _)
            || string.IsNullOrWhiteSpace(message.CorrelationId) || message.CorrelationId.Length > 128
            || message.ExpiresAt <= now || message.CreatedAt > now.AddSeconds(skew) || message.CreatedAt >= message.ExpiresAt
            || message.ExpiresAt - message.CreatedAt > TimeSpan.FromMinutes(30)) return Reject("node_invalid_message", 400);
        var claim = await PersistentReplayGuard.ClaimAsync(Path.Combine(config.StateDirectory, "replay-v2"),
            JsonSerializer.Serialize(new[] { cluster, sender, keyId, nonce }), now.ToUnixTimeSeconds(), token);
        if (claim == ReplayClaim.Replay) return Reject("node_replay");
        if (claim != ReplayClaim.Accepted) return Reject("node_replay_store_unavailable", 503);
        return (message, null);
    }

    public static void Sign(HttpRequestMessage request, NodeMessage message, string keyId, string secret, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var values = new[] { message.ClusterId, message.SenderNodeId, message.ReceiverNodeId, keyId, timestamp, nonce,
            Compute(secret, message.Type, message.ClusterId, message.SenderNodeId, message.ReceiverNodeId, keyId, timestamp, nonce, body) };
        for (var i = 0; i < HeaderNames.Length; i++) request.Headers.Add(HeaderNames[i], values[i]);
    }

    internal static bool ValidId(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    internal static string Compute(string secret, string operation, string cluster, string sender, string receiver,
        string keyId, string timestamp, string nonce, byte[] body)
    {
        var canonical = string.Join('\n', "2", "POST", Prefix + operation, cluster, sender, receiver, keyId, timestamp, nonce,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));
    }
}
