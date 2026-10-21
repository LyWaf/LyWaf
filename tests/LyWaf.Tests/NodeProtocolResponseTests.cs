using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeProtocolResponseTests
{
    private static DefaultHttpContext Context(bool authenticated)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "test-trace" };
        if (authenticated)
            NodeProtocolResponse.BindAuthenticatedMessage(context, new NodeMessage(2, "cluster", "source", "target",
                "message", "operation", "runtime.sync", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1),
                JsonSerializer.SerializeToElement(new { })));
        return context;
    }

    [Fact]
    public void AddsMetadataWithoutReplacingBusinessStatusOrShape()
    {
        var result = Assert.IsAssignableFrom<IValueHttpResult>(NodeProtocolResponse.Success(Context(true),
            new { status = "Applied", sequence = 12, digest = "digest" }));
        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal("Applied", body.GetProperty("status").GetString());
        Assert.Equal(12, body.GetProperty("sequence").GetInt32());
        Assert.Equal("message", body.GetProperty("messageId").GetString());
        Assert.Equal("operation", body.GetProperty("operationId").GetString());
        Assert.True(body.GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData("Gap")]
    [InlineData("SnapshotRequired")]
    [InlineData("Conflict")]
    public void NonAppliedSyncOutcomeIsNotReportedAsSuccess(string status)
    {
        var result = (IValueHttpResult)NodeProtocolResponse.Success(Context(true), new { status });
        Assert.False(JsonSerializer.SerializeToElement(result.Value).GetProperty("success").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ErrorOnlyIncludesAuthenticatedCorrelation(bool authenticated)
    {
        var result = (IValueHttpResult)NodeProtocolResponse.Error(Context(authenticated), "node_outcome_unknown", 409);
        var body = Assert.IsType<NodeErrorResponse>(result.Value);
        Assert.Equal(authenticated ? "message" : null, body.MessageId);
        Assert.Equal("Unknown", body.Status);
        Assert.False(body.Retryable);
        Assert.Equal("node.errors.node_outcome_unknown", body.MessageKey);
    }
}
