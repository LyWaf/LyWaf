using System.Text.Json;
using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace LyWaf.Tests;

public sealed class ManagementApiContractTests
{
    [Theory]
    [InlineData("/api/v1/plugins/web-tamper/proxy/resources", "/api/plugins/web-tamper/proxy/resources", true)]
    [InlineData("/api/status", "/api/status", false)]
    public void VersionedPathIsRewrittenWithoutChangingLegacyPath(string input, string expected, bool changed)
    {
        Assert.Equal(changed, ManagementApiContract.TryRewriteVersionedPath(input, out var rewritten));
        Assert.Equal(expected, rewritten.Value);
    }

    [Fact]
    public void ErrorEnvelopeKeepsLegacyMessageAndAddsStableDetails()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "request-42" };
        var envelope = ManagementApiContract.CreateError(context, "invalid_request", "字段无效", new { field = "name" });

        Assert.False(envelope.Success);
        Assert.Equal("字段无效", envelope.Message);
        Assert.Equal("invalid_request", envelope.Error.Code);
        Assert.Equal("request-42", envelope.Error.TraceId);
    }

    [Fact]
    public void OpenApiPublishesVersionedPathsAndJsonSchemaReferences()
    {
        var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/widgets/{id}"), 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        var source = new TestEndpointDataSource(builder.Build());

        var json = JsonSerializer.Serialize(ManagementApiContract.BuildOpenApiDocument(source));
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/widgets/{id}", out _));
        Assert.Equal("#/components/schemas/ApiErrorEnvelope",
            document.RootElement.GetProperty("paths").GetProperty("/api/v1/widgets/{id}").GetProperty("get")
                .GetProperty("responses").GetProperty("400").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
    }

    private sealed class TestEndpointDataSource(params Endpoint[] endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }
}
