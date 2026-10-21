using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.Statistic;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.RuntimeState;

public sealed class CcCaptchaReleaseClient : IDisposable
{
    private readonly IOptionsMonitor<RuntimeStateOptions> _runtime;
    private readonly IOptionsMonitor<NodeCommunicationOptions> _communication;
    private readonly IRuntimeStateStore _store;
    private readonly IStatisticService _statistics;
    private readonly HttpClient _http;
    private readonly NodeSocketConnections? _sockets;

    public CcCaptchaReleaseClient(IOptionsMonitor<RuntimeStateOptions> runtime,
        IOptionsMonitor<NodeCommunicationOptions> communication, IRuntimeStateStore store, IStatisticService statistics,
        NodeSocketConnections? sockets = null)
        : this(runtime, communication, store, statistics, new SocketsHttpHandler { AllowAutoRedirect = false }) { _sockets = sockets; }

    internal CcCaptchaReleaseClient(IOptionsMonitor<RuntimeStateOptions> runtime,
        IOptionsMonitor<NodeCommunicationOptions> communication, IRuntimeStateStore store, IStatisticService statistics, HttpMessageHandler transport)
    {
        _runtime = runtime; _communication = communication; _store = store; _statistics = statistics;
        _http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public CcCaptchaTarget? ResolveTarget(HttpContext context, string ip, string subject)
    {
        foreach (var rule in _statistics.GetAdvancedCcRules().Where(r => r.Enabled && r.Action == CcAction.Captcha))
        {
            var scope = CcRuleStateKey.ResolveScope(rule, CcRuleStateKey.Scope(context), _store.GetStatus().NodeId);
            var key = CcRuleStateKey.Create(rule, ip, scope);
            var generation = _store.GetCcPolicyGeneration(key);
            if (generation.StateKey(key) == subject) return new(rule.Id, scope, ip, key, generation);
        }
        return null;
    }

    public async Task<CcCaptchaReleaseConfirmation> ReleaseAsync(CcCaptchaTarget target, string token, CancellationToken cancellationToken)
    {
        var options = _runtime.CurrentValue;
        var config = _communication.CurrentValue;
        var localRule = options.Enabled && string.IsNullOrWhiteSpace(options.CcReleasePrivateKeyPem)
            && config.Mode == "Node" && target.Scope == "node:" + config.NodeId
            ? _statistics.GetAdvancedCcRules().FirstOrDefault(rule => rule.Id == target.RuleId && rule.Scope == CcScope.Node) : null;
        var request = new CcCaptchaReleaseRequest(target, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), DateTimeOffset.UtcNow, localRule);
        var releaseId = CcCaptchaReleaseIssuer.ReleaseId(options.NodeId, request);
        if (!options.Enabled || !string.IsNullOrWhiteSpace(options.CcReleasePrivateKeyPem))
        {
            var fence = _store.IssueCcCaptchaRelease(target.PolicyKey, target.Expected, releaseId);
            return new(fence.NodeId, fence.CcReleaseProof is not null, true);
        }
        var authority = options.CcCaptchaAuthorityNodeId;
        if (!config.Enabled || config.NodeId != options.NodeId || authority == config.NodeId
            || !options.Peers.TryGetValue(authority, out var runtimePeer) || !runtimePeer.Enabled || runtimePeer.ProtocolVersion != 2
            || !config.Peers.TryGetValue(authority, out var peer) || !peer.Enabled
            || !peer.Keys.TryGetValue(peer.ActiveKeyId, out var secret) || Encoding.UTF8.GetByteCount(secret) < 32
            || !options.CcReleaseAuthorities.ContainsKey(authority)
            || !Uri.TryCreate(runtimePeer.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || (config.RequireHttps && uri.Scheme != "https") || uri.AbsolutePath != "/" || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidOperationException("CC captcha release authority is not configured.");
        var now = DateTimeOffset.UtcNow;
        var envelope = new NodeMessage(2, config.ClusterId, config.NodeId, authority, new Guid(Convert.FromHexString(releaseId)).ToString("D"),
            request.ChallengeId, CcCaptchaReleaseIssuer.Operation, now, now.AddMinutes(2),
            JsonSerializer.SerializeToElement(request, NodeMessageAuthentication.JsonOptions));
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, NodeMessageAuthentication.JsonOptions);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(uri, NodeMessageAuthentication.Prefix + envelope.Type))
                { Content = new ByteArrayContent(body) };
                message.Content.Headers.ContentType = new("application/json");
                NodeMessageAuthentication.Sign(message, envelope, peer.ActiveKeyId, secret, body);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await NodeControlTransport.SendAsync(config, _sockets, authority, _http, message, timeout.Token);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt == 0) continue;
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"CC release rejected: {(int)response.StatusCode}");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var bytes = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(bytes, timeout.Token)) != 0)
                {
                    if (buffer.Length + read > 64 * 1024) throw new InvalidOperationException("CC release response exceeds limit.");
                    buffer.Write(bytes, 0, read);
                }
                var result = JsonSerializer.Deserialize<CcCaptchaReleaseResponse>(buffer.ToArray(), NodeMessageAuthentication.JsonOptions);
                var fence = result?.Fence;
                var expected = new CcPolicyGeneration(checked(target.Expected.Version + 1), releaseId);
                if (result is not { IssuerApplied: true, AllNodesConfirmed: false } || fence is null
                    || fence.Domain != CcPolicyGeneration.Domain || fence.Key != target.PolicyKey || fence.NodeId != authority
                    || fence.Value?.Deserialize<CcPolicyGeneration>(NodeMessageAuthentication.JsonOptions) != expected)
                    throw new InvalidOperationException("CC release response binding mismatch.");
                _store.Merge(fence); // Verifies issuer signature and durably persists the fence before reporting local success.
                if (_store.GetCcPolicyGeneration(target.PolicyKey) != expected)
                    throw new CcCaptchaGenerationConflictException();
                return new(authority, true, true);
            }
            catch (Exception ex) when (attempt == 0 && !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException or TaskCanceledException) { }
        }
        throw new InvalidOperationException("CC release outcome is unknown.");
    }

    public void Dispose() => _http.Dispose();
}
