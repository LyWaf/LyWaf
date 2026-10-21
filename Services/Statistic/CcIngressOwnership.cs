using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Statistic;

public sealed class CcIngressOptions
{
    public bool Enabled { get; set; }
    public Dictionary<string, CcIngressPeer> Peers { get; set; } = new(StringComparer.Ordinal);
}
public sealed class CcIngressPeer
{
    public string Secret { get; set; } = "";
    public string DestinationAuthority { get; set; } = "";
    public string[] RemoteAddresses { get; set; } = [];
    public string[] RuleIds { get; set; } = [];
    public string[] Scopes { get; set; } = [];
}

/// <summary>Pairwise data-plane delegation. External headers alone can never suppress counting.</summary>
public sealed class CcIngressOwnership(IOptionsMonitor<CcIngressOptions> options, IOptionsMonitor<RuntimeStateOptions> runtime,
    TimeProvider? clock = null)
{
    public const string Header = "X-LyWaf-CC-Entry";
    private static readonly object ItemKey = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private sealed record Claim(string RuleId, string Scope, string StateKey, string RequestId);
    private sealed record Envelope(string Sender, string Receiver, string Method, string PathAndQuery, DateTimeOffset ExpiresAt, Claim[] Claims);
    private sealed class RequestState { public Dictionary<string, Claim> Claims = new(); public Envelope? Incoming; public bool Parsed; }

    public bool ShouldCount(HttpContext context, AdvancedCcRule rule, CcPolicyDescriptor policy)
    {
        var state = State(context);
        if (state.Claims.ContainsKey(policy.StateKey)) return false;
        if (!state.Parsed) { state.Parsed = true; state.Incoming = Verify(context); }
        var incoming = state.Incoming?.Claims.FirstOrDefault(c => c.RuleId == rule.Id && c.Scope == policy.ProtectionScope && c.StateKey == policy.StateKey);
        if (state.Claims.Count < 16) state.Claims[policy.StateKey] = incoming ?? new(rule.Id, policy.ProtectionScope, policy.StateKey, Guid.NewGuid().ToString("N"));
        return incoming is null;
    }

    public void Forward(HttpContext context, HttpRequestMessage request, Uri? destination = null)
    {
        request.Headers.Remove(Header);
        var config = options.CurrentValue;
        if (!config.Enabled || (destination ?? request.RequestUri) is not { IsAbsoluteUri: true } uri || uri.Scheme != "https") return;
        var destinations = config.Peers.Where(p => p.Value.DestinationAuthority == uri.GetLeftPart(UriPartial.Authority)).ToArray();
        if (destinations.Length != 1 || !ValidSecret(destinations[0].Value.Secret)) return;
        var (peerId, peer) = destinations[0];
        var claims = State(context).Claims.Values.Where(c => Granted(peer, c)).ToArray();
        if (claims.Length == 0) return;
        var envelope = new Envelope(runtime.CurrentValue.NodeId, peerId, request.Method.Method, uri.PathAndQuery,
            _clock.GetUtcNow().AddSeconds(10), claims);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var header = Convert.ToBase64String(body) + "." + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(peer.Secret), body));
        if (header.Length <= 8192) request.Headers.TryAddWithoutValidation(Header, header);
    }

    private Envelope? Verify(HttpContext context)
    {
        var config = options.CurrentValue;
        if (!config.Enabled || !context.Request.IsHttps || !context.Request.Headers.TryGetValue(Header, out var headers) || headers.Count != 1) return null;
        var value = headers[0];
        if (value is null || value.Length > 8192) return null;
        try
        {
            var parts = value.Split('.');
            if (parts.Length != 2 || parts[1].Length != 64 || !parts[1].All(char.IsAsciiHexDigit)) return null;
            var body = Convert.FromBase64String(parts[0]);
            var envelope = JsonSerializer.Deserialize<Envelope>(body);
            if (envelope is null || envelope.Receiver != runtime.CurrentValue.NodeId
                || !config.Peers.TryGetValue(envelope.Sender, out var peer) || !ValidSecret(peer.Secret)
                || context.Connection.RemoteIpAddress is not { } remote
                || !peer.RemoteAddresses.Any(ip => IPAddress.TryParse(ip, out var trusted) && trusted.MapToIPv6().Equals(remote.MapToIPv6()))
                || envelope.Method != context.Request.Method || envelope.PathAndQuery != context.Request.Path + context.Request.QueryString
                || envelope.ExpiresAt <= _clock.GetUtcNow() || envelope.ExpiresAt > _clock.GetUtcNow().AddSeconds(11)
                || envelope.Claims is not { Length: > 0 and <= 16 }
                || envelope.Claims.Any(c => c is null || !Granted(peer, c) || c.StateKey is not { Length: > 0 and <= 512 }
                    || !Guid.TryParseExact(c.RequestId, "N", out _))
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[1]), HMACSHA256.HashData(Encoding.UTF8.GetBytes(peer.Secret), body))) return null;
            return envelope;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { return null; }
    }

    private static bool Granted(CcIngressPeer peer, Claim claim) => peer.RuleIds.Contains(claim.RuleId, StringComparer.Ordinal)
        && peer.Scopes.Contains(claim.Scope, StringComparer.Ordinal);
    private static bool ValidSecret(string secret) => Encoding.UTF8.GetByteCount(secret) >= 32;
    private static RequestState State(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var state)) return (RequestState)state!;
        var created = new RequestState(); context.Items[ItemKey] = created; return created;
    }
}
