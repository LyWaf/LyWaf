using System.Net;
using System.Text;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeRemoteErrorTests
{
    [Fact]
    public async Task PreservesStatusCodeRetryAndSafeMetadataButNotRemoteMessage()
    {
        using var response = Response("""{"errorCode":"node_inbox_busy","traceId":"trace:123","messageKey":"attacker.key","message":"secret content"}""");
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(3));
        var error = await NodeRemoteError.ReadAsync(response, default);
        Assert.IsType<HttpRequestException>(error);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("node_inbox_busy", error.Data["ErrorCode"]);
        Assert.Equal("node.errors.node_inbox_busy", error.Data["MessageKey"]);
        Assert.Equal("trace:123", error.Data["RemoteTraceId"]);
        Assert.Equal(TimeSpan.FromSeconds(3), error.Data["RetryAfter"]);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("attacker", error.ToString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"errorCode\":123}")]
    [InlineData("{\"errorCode\":\"one\",\"errorCode\":\"two\"}")]
    [InlineData("{\"errorCode\":\"invalid\\nlog entry\"}")]
    public async Task InvalidMetadataDoesNotReplaceHttpFailure(string body)
    {
        using var response = Response(body);
        var error = await NodeRemoteError.ReadAsync(response, default);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("Node request rejected", error.Message);
        Assert.False(error.Data.Contains("ErrorCode"));
    }

    [Fact]
    public async Task UnknownLengthBodyReadsAtMostLimitPlusOne()
    {
        using var stream = new CountingStream(new byte[NodeRemoteError.MaximumBodyBytes * 10]);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(stream) };
        var error = await NodeRemoteError.ReadAsync(response, default);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(NodeRemoteError.MaximumBodyBytes + 1, stream.BytesRead);
        Assert.False(error.Data.Contains("ErrorCode"));
    }

    [Fact]
    public async Task KnownOversizeBodyIsNotRead()
    {
        using var stream = new CountingStream(new byte[NodeRemoteError.MaximumBodyBytes * 2]);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = NodeRemoteError.MaximumBodyBytes * 2;
        var error = await NodeRemoteError.ReadAsync(response, default);
        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.ServiceUnavailable)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CancellationIsNotConvertedToAnHttpRejection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new CountingStream(new byte[10]);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StreamContent(stream) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NodeRemoteError.ReadAsync(response, cancellation.Token));
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task BrokenOptionalBodyPreservesKnownHttpStatus()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StreamContent(new BrokenStream()) };
        var error = await NodeRemoteError.ReadAsync(response, default);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("Node request rejected", error.Message);
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("untrusted body content"));
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = base.Read(buffer.Span);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
    }
}
