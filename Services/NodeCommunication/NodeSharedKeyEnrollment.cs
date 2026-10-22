using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeSharedKeyRequest(int Version, string NodeId, string Name, long Timestamp, string Nonce, string Signature);
public sealed record NodeSharedKeyReply(string Credential, string Nonce, string Signature);

internal static class NodeSharedKeyEnrollment
{
    internal const string Path = NodeMessageAuthentication.Prefix + "enroll-shared-key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static NodeSharedKeyRequest Create(string secret, string nodeId, string name)
    {
        var request = new NodeSharedKeyRequest(1, nodeId, name, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), "");
        return request with { Signature = RequestSignature(secret, request) };
    }

    internal static void Validate(NodeSharedKeyRequest request, string secret)
    {
        if (request.Version != 1 || !NodeMessageAuthentication.ValidId(request.NodeId)
            || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || request.Name.Any(char.IsControl)
            || request.Nonce is not { Length: 32 } || !request.Nonce.All(char.IsAsciiHexDigit)
            || Math.Abs((decimal)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - request.Timestamp) > 60
            || !Equal(RequestSignature(secret, request), request.Signature))
            throw new UnauthorizedAccessException();
    }

    private static string RequestSignature(string secret, NodeSharedKeyRequest request)
        => Mac(secret, "request\nPOST\n" + Path + "\n" + JsonSerializer.Serialize(request with { Signature = "" }, Json));

    internal static NodeSharedKeyReply Reply(string secret, NodeSharedKeyRequest request, string credential)
        => new(credential, request.Nonce, Mac(secret, "response\n" + request.Signature + "\n" + credential));

    private static string Mac(string secret, string canonical)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical)));

    private static bool Equal(string expected, string? actual)
        => actual is { Length: 64 } && actual.All(char.IsAsciiHexDigit)
            && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));

    internal static async Task<NodeSetupCredential> JoinAsync(string endpoint, string secret, string nodeId, string name, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        token = deadline.Token;
        if (name.Length > 128 || name.Any(char.IsControl)) throw new ArgumentException("Invalid node name");
        var request = Create(secret, nodeId, name);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(endpoint), Path)) { Content = JsonContent.Create(request) };
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Controller enrollment rejected", null, response.StatusCode);
        var bytes = new byte[8193];
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count > 8192) throw new HttpRequestException("Invalid enrollment response");
        NodeSharedKeyReply? reply;
        try { reply = JsonSerializer.Deserialize<NodeSharedKeyReply>(bytes.AsSpan(0, count), Json); }
        catch (JsonException) { throw new HttpRequestException("Invalid enrollment response"); }
        if (reply is null || reply.Credential is not { Length: > 0 and <= 4096 } || reply.Nonce != request.Nonce
            || !Equal(Reply(secret, request, reply.Credential).Signature, reply.Signature))
            throw new HttpRequestException("Invalid enrollment response signature");
        NodeSetupCredential credential;
        try { credential = NodeSetupStore.DecodeCredential(reply.Credential); }
        catch (ArgumentException) { throw new HttpRequestException("Invalid enrollment credential"); }
        if (credential.NodeId != nodeId || credential.Secret != secret) throw new HttpRequestException("Enrollment identity mismatch");
        return credential;
    }
}
