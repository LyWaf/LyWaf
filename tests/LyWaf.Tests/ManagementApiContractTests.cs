using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using LyWaf.Services.ManagementApi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using LyWaf.Services.Auth;
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

    [Fact]
    public void OpenApiUsesModelsPermissionsStatusAndNormalizedRouteParameters()
    {
        var get = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/widgets/{id:int}"), 0);
        get.Metadata.Add(new HttpMethodMetadata(["GET"]));
        var post = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/widgets/{id:int}"), 0);
        post.Metadata.Add(new HttpMethodMetadata(["POST"]));
        post.Metadata.Add(new ManagementPermissionRequirement(ManagementPermissions.ManageSystem));
        post.Metadata.Add(new AcceptsMetadata(["application/json"], typeof(Widget)));
        post.Metadata.Add(new ProducesResponseTypeMetadata(202, typeof(Widget), ["application/json"]));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ManagementApiContract.BuildOpenApiDocument(new TestEndpointDataSource(get.Build(), post.Build()))));
        var path = document.RootElement.GetProperty("paths").GetProperty("/api/v1/widgets/{id}");
        Assert.True(path.TryGetProperty("get", out _));
        var operation = path.GetProperty("post");
        Assert.Equal("system.manage", operation.GetProperty("x-permissions")[0].GetString());
        var parameter = operation.GetProperty("parameters")[0];
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.Equal("integer", parameter.GetProperty("schema").GetProperty("type").GetString());
        var responses = operation.GetProperty("responses");
        Assert.False(responses.TryGetProperty("200", out _));
        Assert.Equal("string", responses.GetProperty("202").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
        Assert.True(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
        var requestSchema = operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("name", requestSchema.GetProperty("required")[0].GetString());
        Assert.Equal(200, requestSchema.GetProperty("properties").GetProperty("name").GetProperty("maxLength").GetInt32());
        Assert.True(responses.TryGetProperty("409", out _));
    }

    [Fact]
    public void PagedResponsePublishesQueryBoundsAndTotalModel()
    {
        var endpoint = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/api/widgets"), 0);
        endpoint.Metadata.Add(new HttpMethodMetadata(["GET"]));
        endpoint.Metadata.Add(new ProducesResponseTypeMetadata(200, typeof(ManagementPage<Widget>), ["application/json"]));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ManagementApiContract.BuildOpenApiDocument(new TestEndpointDataSource(endpoint.Build()))));
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/v1/widgets").GetProperty("get");
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        Assert.Equal(7, parameters.Count);
        Assert.Equal(100, parameters["pageSize"].GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.Equal(200, parameters["search"].GetProperty("schema").GetProperty("maxLength").GetInt32());
        Assert.All(parameters.Values, p => Assert.Equal("query", p.GetProperty("in").GetString()));
        var properties = operation.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("properties");
        Assert.Equal("integer", properties.GetProperty("total").GetProperty("type").GetString());
        Assert.Equal("array", properties.GetProperty("data").GetProperty("type").GetString());
    }

    [Fact]
    public void NonListResponseDoesNotAdvertisePagination()
    {
        Assert.Empty(ManagementOpenApiModels.ListParameters([
            new ProducesResponseTypeMetadata(200, typeof(Widget), ["application/json"])]));
    }

    private sealed record Widget([property: Required, StringLength(200)] string Name);

    private sealed record WireTypes(TimeSpan Duration, DateOnly Day, string? Note);
    private sealed record CollectionBounds([property: MaxLength(10)] Dictionary<string, string> Headers,
        [property: MaxLength(20)] List<string> Entries);

    [Fact]
    public void OpenApiDistinguishesDictionaryAndArrayBounds()
    {
        var response = JsonSerializer.SerializeToElement(ManagementOpenApiModels.Response(
            new ProducesResponseTypeMetadata(200, typeof(CollectionBounds), ["application/json"])));
        var properties = response.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("properties");
        Assert.Equal(10, properties.GetProperty("headers").GetProperty("maxProperties").GetInt32());
        Assert.False(properties.GetProperty("headers").TryGetProperty("maxItems", out _));
        Assert.Equal(20, properties.GetProperty("entries").GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public void OpenApiMatchesSerializerForTimeSpanAndNullableReferences()
    {
        var response = JsonSerializer.SerializeToElement(ManagementOpenApiModels.Response(
            new ProducesResponseTypeMetadata(200, typeof(WireTypes), ["application/json"])));
        var properties = response.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("duration").GetProperty("type").GetString());
        Assert.Equal("date", properties.GetProperty("day").GetProperty("format").GetString());
        Assert.Contains(properties.GetProperty("note").GetProperty("anyOf").EnumerateArray(),
            variant => variant.GetProperty("type").GetString() == "null");
    }

    private sealed class TestEndpointDataSource(params Endpoint[] endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }
}
