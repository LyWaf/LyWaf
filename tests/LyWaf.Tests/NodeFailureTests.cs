using System.Net;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeFailureTests
{
    [Fact]
    public void ClassifiesFailuresWithoutKeepingRawExceptionText()
    {
        Assert.Equal("node_timeout", NodeFailure.FromException(new TaskCanceledException("secret")).Code);
        Assert.Equal("node_unreachable", NodeFailure.FromException(new HttpRequestException("secret")).Code);
        Assert.Equal("node_http_rejected", NodeFailure.FromException(new HttpRequestException("secret", null, HttpStatusCode.Forbidden)).Code);
        Assert.Equal("node_invalid_response", NodeFailure.FromException(new InvalidDataException("secret")).Code);
        Assert.Equal("node_configuration_error", NodeFailure.FromException(new InvalidOperationException("secret")).Code);
    }

    [Fact]
    public void RejectsUnboundedOrMultilineMetadata()
    {
        var error = new HttpRequestException("secret");
        error.Data["ErrorCode"] = "node_error\nprivate details";
        error.Data["RemoteTraceId"] = new string('x', 129);
        Assert.Equal(new NodeFailure("node_unreachable", null), NodeFailure.FromException(error));
    }
}
