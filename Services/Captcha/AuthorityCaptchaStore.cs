using System.Text;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Options;

namespace LyWaf.Services.Captcha;

internal sealed record CaptchaIssueRequest(string Token, CaptchaService.CaptchaEntry Entry, DateTimeOffset IssuedAt, int LifetimeMilliseconds);
internal sealed record CaptchaTakeRequest(string Token);
internal sealed record CaptchaAuthorityReply(string RequestId, long Sequence, CaptchaService.CaptchaEntry? Entry = null);

internal sealed class AuthorityCaptchaStore(IOptions<CaptchaStoreOptions> options,
    IOptionsMonitor<NodeCommunicationOptions> communication, FileCaptchaAuthority files, NodeSocketConnections? sockets = null) : ISharedCaptchaStore, ICcCaptchaConsumption, IHostedService, IDisposable
{
    public const string IssueOperation = "captcha.store.issue";
    public const string TakeOperation = "captcha.store.take";
    private readonly CaptchaStoreOptions _options = options.Value;
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private NodeCommunicationOptions Configuration()
    {
        NodeCommunicationOptions config;
        try { config = communication.CurrentValue; }
        catch (OptionsValidationException) { throw new CaptchaStoreUnavailableException(); }
        if (!config.Enabled || config.ClusterId != _options.ClusterId) throw new CaptchaStoreUnavailableException();
        return config;
    }
    public async Task PutAsync(string token, CaptchaService.CaptchaEntry entry, CancellationToken tokenCancellation, int lifetimeMilliseconds = 300000)
    {
        var config = Configuration();
        var issuedAt = DateTimeOffset.UtcNow;
        if (config.NodeId == _options.AuthorityNodeId) await files.PutAsync(token, entry, issuedAt, lifetimeMilliseconds, tokenCancellation);
        else await SendAsync(config, IssueOperation, new CaptchaIssueRequest(token, entry, issuedAt, lifetimeMilliseconds), tokenCancellation);
    }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Configuration().NodeId == _options.AuthorityNodeId) await files.OpenAsync(cancellationToken);
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task<CaptchaService.CaptchaEntry?> TakeAsync(string token, CancellationToken tokenCancellation)
    {
        if (!FileCaptchaAuthority.ValidToken(token)) return null;
        var config = Configuration();
        return config.NodeId == _options.AuthorityNodeId ? await files.TakeAsync(token, tokenCancellation, config.NodeId)
            : (await SendAsync(config, TakeOperation, new CaptchaTakeRequest(token), tokenCancellation)).Entry;
    }
    private async Task<CaptchaAuthorityReply> SendAsync(NodeCommunicationOptions config, string operation, object payload, CancellationToken caller)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(caller);
        timeout.CancelAfter(_options.TimeoutMilliseconds);
        try
        {
            if (!config.Peers.TryGetValue(_options.AuthorityNodeId, out var peer) || !NodeAdmissionPolicy.Allows(peer, operation)
                || !peer.AllowedOperations.Contains(operation, StringComparer.Ordinal)
                || !peer.Keys.TryGetValue(peer.ActiveKeyId, out var secret) || Encoding.UTF8.GetByteCount(secret) < 32
                || !Uri.TryCreate(_options.AuthorityEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")
                || config.RequireHttps && endpoint.Scheme != "https" || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0
                || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0) throw new CaptchaStoreUnavailableException();
            var now = DateTimeOffset.UtcNow;
            var message = new NodeMessage(2, config.ClusterId, config.NodeId, _options.AuthorityNodeId, Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("N"), operation, now, now.AddSeconds(10), JsonSerializer.SerializeToElement(payload, NodeMessageAuthentication.JsonOptions));
            var body = JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, NodeMessageAuthentication.Prefix + operation))
                { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            NodeMessageAuthentication.Sign(request, message, peer.ActiveKeyId, secret, body);
            using var response = await NodeControlTransport.SendAsync(config, sockets, _options.AuthorityNodeId, _http, request, timeout.Token);
            if (!response.IsSuccessStatusCode) throw await NodeRemoteError.ReadAsync(response, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[4096]; int length;
            while ((length = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            {
                if (buffer.Length + length > 16384) throw new CaptchaStoreUnavailableException();
                buffer.Write(bytes, 0, length);
            }
            var reply = JsonSerializer.Deserialize<CaptchaAuthorityReply>(buffer.ToArray(), NodeMessageAuthentication.JsonOptions);
            if (reply is null || reply.RequestId != message.MessageId || reply.Sequence < 0) throw new CaptchaStoreUnavailableException();
            return reply;
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException)
        { throw new CaptchaStoreUnavailableException(); }
    }
    public async Task<IResult> HandleAsync(NodeMessage message, HttpContext context)
    {
        if (Configuration().NodeId != _options.AuthorityNodeId) return NodeProtocolResponse.Error(context, "captcha_authority_unavailable", 503);
        try
        {
            CaptchaService.CaptchaEntry? entry = null;
            if (message.Type == IssueOperation)
            {
                var issue = message.Payload.Deserialize<CaptchaIssueRequest>(NodeMessageAuthentication.JsonOptions);
                if (issue is null) return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
                await files.PutAsync(issue.Token, issue.Entry, issue.IssuedAt, issue.LifetimeMilliseconds, context.RequestAborted);
            }
            else
            {
                var take = message.Payload.Deserialize<CaptchaTakeRequest>(NodeMessageAuthentication.JsonOptions);
                if (take is null || !FileCaptchaAuthority.ValidToken(take.Token)) return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
                entry = await files.TakeAsync(take.Token, context.RequestAborted, message.SenderNodeId);
            }
            return NodeProtocolResponse.Success(context, new CaptchaAuthorityReply(message.MessageId, files.Sequence, entry));
        }
        catch (ArgumentException) { return NodeProtocolResponse.Error(context, "node_invalid_payload", 400); }
        catch (CaptchaStoreUnavailableException) { return NodeProtocolResponse.Error(context, "captcha_authority_unavailable", 503); }
    }
    public void Dispose() => _http.Dispose();

    public Task<bool> WasConsumedAsync(string challengeDigest, string nodeId, string subject, CancellationToken token) =>
        Configuration().NodeId == _options.AuthorityNodeId ? files.WasConsumedAsync(challengeDigest, nodeId, subject, token)
            : Task.FromResult(false);
}
