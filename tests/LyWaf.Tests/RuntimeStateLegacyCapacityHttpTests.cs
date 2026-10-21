using System.Net;
using System.Text.Json;
using LyWaf.Services.RuntimeState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateLegacyCapacityHttpTests
{
    [Fact]
    public async Task SignedV1MergeReturnsRetryable503AtCapacityWithoutChangingExistingState()
    {
        await using var host = await NodeContractHttpHost.Start(legacy: true);
        var now = DateTimeOffset.UtcNow;
        var mutation = new RuntimeStateMutation("ip.blocked", "192.0.2.1", "sender", 1, now,
            now.AddMinutes(10), false, JsonSerializer.SerializeToElement("blocked"), null);
        using (var first = await Send(mutation))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        }
        var store = host.App.Services.GetRequiredService<IRuntimeStateStore>();
        var before = JsonSerializer.Serialize(store.GetSnapshots());
        using (var full = await Send(mutation with { Key = "192.0.2.2" }))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
            var text = await full.Content.ReadAsStringAsync();
            using var body = JsonDocument.Parse(text);
            NodeOperationContractHttpTests.AssertError(body.RootElement, "node_runtime_store_unavailable", true);
            Assert.DoesNotContain(host.Directory, text);
            Assert.Equal(before, JsonSerializer.Serialize(store.GetSnapshots()));
        }
        using var duplicate = await Send(mutation);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Single(store.GetSnapshots());

        async Task<HttpResponseMessage> Send(RuntimeStateMutation value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-state/agent/merge")
                { Content = new ByteArrayContent(bytes) };
            request.Headers.Host = "localhost:7030";
            request.Content.Headers.ContentType = new("application/json");
            RuntimeStateAuthentication.Sign(request, "sender", NodeContractHttpHost.Secret, bytes);
            return await host.Client.SendAsync(request);
        }
    }
}
