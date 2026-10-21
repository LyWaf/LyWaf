using System.Net;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeHttpIntegrationTests
{
    [Fact]
    public async Task ActualRuntimeEndpointAuthenticatesRejectsReplayAndHonorsDisableSwitch()
    {
        const string key = "http-integration-key-at-least-32-bytes";
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-http-" + Guid.NewGuid().ToString("N"));
        var runtime = new RuntimeStateOptions { Enabled = true, NodeId = "receiver", StateDirectory = directory };
        runtime.Peers["sender"] = new() { Receive = new() { AllowedDomains = ["cc.access"], AllowUngroupedKeys = true } };
        var protocol = new NodeCommunicationOptions
        {
            Enabled = true, RequireHttps = false, ClusterId = "cluster", NodeId = "receiver", StateDirectory = directory,
            Peers = new() { ["sender"] = new() { Keys = new() { ["key"] = key }, AllowedOperations = ["runtime.merge"] } }
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new StaticOptionsMonitor<RuntimeStateOptions>(runtime));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new StaticOptionsMonitor<NodeCommunicationOptions>(protocol));
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<NodeIdentityService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<NodeIdentityService>());
        // Deliberately omit metadata dependencies: activation failures must produce the protocol error, not HTTP 500.
        builder.Services.AddNodeHandshakeServices();
        builder.Services.AddSingleton<IRuntimeStateStore, RuntimeStateStore>();
        // Invalid/unauthorized queries must be rejected before consulting the rule provider.
        builder.Services.AddSingleton<CcDecisionService>(services => new(
            null!, services.GetRequiredService<IRuntimeStateStore>(), new CcBanLog()));
        builder.Services.AddSingleton<CcExecutionQueryService>();
        await using var app = builder.Build();
        // Host matching is independent of the ephemeral listening port.
        app.MapNodeCommunication(7030);
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
            var now = DateTimeOffset.UtcNow;
            var mutation = new RuntimeStateMutation("cc.access", "rule|window", "sender", 1, now, now.AddMinutes(1), false,
                null, new Dictionary<string, long> { [JsonSerializer.Serialize(new[] { "sender", new string('a', 32) })] = 40 });
            var message = new NodeMessage(2, "cluster", "sender", "receiver", Guid.NewGuid().ToString("D"), "runtime-state",
                "runtime.merge", now, now.AddMinutes(5), JsonSerializer.SerializeToElement(mutation, NodeMessageAuthentication.JsonOptions));
            using var first = Request(message);
            var headers = first.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray());
            using var accepted = await client.SendAsync(first);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            using var replay = Request(message);
            replay.Headers.Clear();
            foreach (var header in headers) replay.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var rejected = await client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            using var replayError = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal("node_replay", replayError.RootElement.GetProperty("errorCode").GetString());
            AssertErrorContract(replayError.RootElement, "node_replay");
            var store = app.Services.GetRequiredService<IRuntimeStateStore>();
            Assert.Equal(40, Assert.Single(store.GetSnapshots()).CounterValue);

            using var batchRequest = Request(message with
            {
                MessageId = Guid.NewGuid().ToString("D"),
                Payload = JsonSerializer.SerializeToElement(new[] { mutation, mutation }, NodeMessageAuthentication.JsonOptions)
            });
            using var batchResponse = await client.SendAsync(batchRequest);
            Assert.Equal(HttpStatusCode.OK, batchResponse.StatusCode);
            Assert.Equal(40, Assert.Single(store.GetSnapshots()).CounterValue);
            runtime.MaxStateEntries = 1;
            try
            {
                using var fullRequest = Request(message with
                {
                    MessageId = Guid.NewGuid().ToString("D"),
                    Payload = JsonSerializer.SerializeToElement(mutation with { Key = "another-rule|window" }, NodeMessageAuthentication.JsonOptions)
                });
                using var full = await client.SendAsync(fullRequest);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
                var text = await full.Content.ReadAsStringAsync();
                using var body = JsonDocument.Parse(text);
                AssertErrorContract(body.RootElement, "node_runtime_store_unavailable");
                Assert.True(body.RootElement.GetProperty("retryable").GetBoolean());
                Assert.DoesNotContain(directory, text);
                Assert.Equal(40, Assert.Single(store.GetSnapshots()).CounterValue);
            }
            finally { runtime.MaxStateEntries = 100_000; }

            var handshake = message with { Type = "node.handshake", MessageId = Guid.NewGuid().ToString("D"), Payload = JsonSerializer.SerializeToElement(new { }) };
            using (var deniedRequest = Request(handshake))
            using (var denied = await client.SendAsync(deniedRequest))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            protocol.Peers["sender"].AllowedOperations = ["runtime.merge", "node.handshake"];
            using (var handshakeRequest = Request(handshake))
            using (var response = await client.SendAsync(handshakeRequest))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertErrorContract(body.RootElement, "node_handshake_unavailable");
            }

            runtime.Enabled = false;
            using var disabledRequest = Request(message with { MessageId = Guid.NewGuid().ToString("D") });
            using var disabled = await client.SendAsync(disabledRequest);
            Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
            using var disabledError = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync());
            AssertErrorContract(disabledError.RootElement, "node_runtime_disabled_or_misconfigured");
            Assert.Equal(40, Assert.Single(store.GetSnapshots()).CounterValue);

            runtime.Enabled = true;
            foreach (var admission in new[] { "PendingApproval", "Draining", "Revoked" })
            {
                protocol.Peers["sender"].AdmissionState = admission;
                using var deniedRequest = Request(message with { MessageId = Guid.NewGuid().ToString("D") });
                using var deniedResponse = await client.SendAsync(deniedRequest);
                Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
                using var errorBody = JsonDocument.Parse(await deniedResponse.Content.ReadAsStringAsync());
                AssertErrorContract(errorBody.RootElement, "node_admission_denied");
                Assert.Equal(40, Assert.Single(store.GetSnapshots()).CounterValue);
            }
            protocol.Peers["sender"].AdmissionState = "Active";
            var executionQuery = message with
            {
                Type = CcExecutionQueryService.Operation, MessageId = Guid.NewGuid().ToString("D"),
                Payload = JsonSerializer.SerializeToElement(new { })
            };
            using (var queryRequest = Request(executionQuery))
            using (var response = await client.SendAsync(queryRequest))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            protocol.Peers["sender"].AllowedOperations.Add(CcExecutionQueryService.Operation);
            using (var queryRequest = Request(executionQuery with { MessageId = Guid.NewGuid().ToString("D") }))
            using (var response = await client.SendAsync(queryRequest))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertErrorContract(body.RootElement, "node_invalid_payload");
            }
            foreach (var language in new[] { "en-US", "zh-CN" })
            {
                using var invalidRequest = Request(message with
                {
                    MessageId = Guid.NewGuid().ToString("D"),
                    Payload = JsonSerializer.SerializeToElement("not a mutation")
                });
                invalidRequest.Headers.AcceptLanguage.ParseAdd(language);
                using var invalidResponse = await client.SendAsync(invalidRequest);
                Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
                using var invalidError = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
                AssertErrorContract(invalidError.RootElement, "node_invalid_payload");
            }

            protocol.Peers["sender"].AllowedOperations.Add(PluginNodeMessageService.Operation);
            using (var absentPluginRequest = Request(message with
            {
                Type = PluginNodeMessageService.Operation, MessageId = Guid.NewGuid().ToString("D")
            }))
            using (var absentPluginResponse = await client.SendAsync(absentPluginRequest))
            {
                Assert.Equal(HttpStatusCode.NotFound, absentPluginResponse.StatusCode);
                using var body = JsonDocument.Parse(await absentPluginResponse.Content.ReadAsStringAsync());
                AssertErrorContract(body.RootElement, "node_plugin_missing");
            }

            protocol.NodeId = "changed-without-restart";
            using (var changedIdentityRequest = Request(handshake with { ReceiverNodeId = protocol.NodeId }))
            using (var response = await client.SendAsync(changedIdentityRequest))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertErrorContract(body.RootElement, "node_identity_unavailable");
            }

            HttpRequestMessage Request(NodeMessage envelope)
            {
                var body = JsonSerializer.SerializeToUtf8Bytes(envelope, NodeMessageAuthentication.JsonOptions);
                var request = new HttpRequestMessage(HttpMethod.Post, NodeMessageAuthentication.Prefix + envelope.Type)
                { Content = new ByteArrayContent(body) };
                request.Headers.Host = "localhost:7030";
                request.Content.Headers.ContentType = new("application/json");
                NodeMessageAuthentication.Sign(request, envelope, "key", key, body);
                return request;
            }
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AssertErrorContract(JsonElement response, string code)
    {
        Assert.False(response.GetProperty("success").GetBoolean());
        Assert.Equal(code, response.GetProperty("errorCode").GetString());
        Assert.Equal("node.errors." + code, response.GetProperty("messageKey").GetString());
        Assert.False(string.IsNullOrWhiteSpace(response.GetProperty("traceId").GetString()));
    }
}
