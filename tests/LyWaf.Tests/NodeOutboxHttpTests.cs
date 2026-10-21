using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeOutboxHttpTests
{
    [Fact]
    public async Task ResultEndpointReturnsDurableAckAndRejectsMissingConflictAndUnauthorizedQueries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "outbox-http-" + Guid.NewGuid().ToString("N"));
        const string key = "http-outbox-key-at-least-32-bytes-long";
        var config = new NodeCommunicationOptions
        {
            Enabled = true, RequireHttps = false, ClusterId = "cluster", NodeId = "edge", Role = "Node", StateDirectory = directory,
            Peers = new() { ["control"] = new() { Role = "Controller", Keys = new() { ["key"] = key }, AllowedOperations = ["configuration.result"] } }
        };
        var monitor = new StaticOptionsMonitor<NodeCommunicationOptions>(config);
        var inbox = new NodeCommandInbox(monitor);
        var now = DateTimeOffset.UtcNow;
        var original = new NodeMessage(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "deployment",
            "configuration.commit", now, now.AddMinutes(5), JsonSerializer.SerializeToElement(new DistributionCommandRequest("deployment"), NodeMessageAuthentication.JsonOptions));
        var ack = new NodeAcknowledgement(true, "edge", "deployment", "commit");
        await inbox.ExecuteAsync(original, () => Task.FromResult(ack), default);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(monitor);
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton(inbox);
        // Completed and missing inbox lookups must not touch configuration lifecycle dependencies.
        builder.Services.AddSingleton(new DistributionNodeService(new StaticOptionsMonitor<DistributionOptions>(new()),
            null!, null!, new ConfigurationBuilder().Build()));
        await using var app = builder.Build();
        app.MapNodeCommunication(7030);
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            using var accepted = await Query(original);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal(ack, await accepted.Content.ReadFromJsonAsync<NodeAcknowledgement>());
            using var missing = await Query(original with { MessageId = Guid.NewGuid().ToString("D") });
            Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
            using var conflict = await Query(original with { Type = "configuration.rollback" });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var inboxDirectory = Path.Combine(directory, "inbox");
            var preservedDirectory = Path.Combine(directory, "inbox-preserved");
            Directory.Move(inboxDirectory, preservedDirectory);
            try
            {
                await File.WriteAllTextAsync(inboxDirectory, "storage-fault-secret");
                using var unavailable = await Query(original);
                Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
                var text = await unavailable.Content.ReadAsStringAsync();
                using var error = JsonDocument.Parse(text);
                Assert.Equal("node_outcome_unknown", error.RootElement.GetProperty("errorCode").GetString());
                Assert.Equal("Unknown", error.RootElement.GetProperty("status").GetString());
                Assert.False(error.RootElement.GetProperty("retryable").GetBoolean());
                Assert.DoesNotContain(directory, text);
                Assert.DoesNotContain("storage-fault-secret", text);
            }
            finally
            {
                File.Delete(inboxDirectory);
                Directory.Move(preservedDirectory, inboxDirectory);
            }
            using var afterRecovery = await Query(original);
            Assert.Equal(ack, await afterRecovery.Content.ReadFromJsonAsync<NodeAcknowledgement>());
            foreach (var code in new[] { NodeConfigurationErrors.Invalid, NodeConfigurationErrors.Conflict })
            {
                var rejectedCommand = original with { MessageId = Guid.NewGuid().ToString("D") };
                await inbox.ExecuteAsync(rejectedCommand, () => Task.FromResult(new NodeAcknowledgement(false,
                    "edge", "deployment", "commit", Error: "private-error-text", ErrorCode: code, MessageKey: "node.errors." + code)), default);
                using var businessReply = await Query(rejectedCommand);
                Assert.Equal(NodeConfigurationErrors.HttpStatus(code), (int)businessReply.StatusCode);
                var text = await businessReply.Content.ReadAsStringAsync();
                using var body = JsonDocument.Parse(text);
                Assert.False(body.RootElement.GetProperty("ack").GetBoolean());
                Assert.False(body.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(code, body.RootElement.GetProperty("errorCode").GetString());
                Assert.Equal("edge", body.RootElement.GetProperty("nodeId").GetString());
                Assert.DoesNotContain("private-error-text", text);
            }
            config.Peers["control"].AllowedOperations.Clear();
            using var forbidden = await Query(original);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal(3, Directory.GetFiles(Path.Combine(directory, "inbox"), "*.json").Length);

            async Task<HttpResponseMessage> Query(NodeMessage command)
            {
                var envelope = original with { MessageId = Guid.NewGuid().ToString("D"), Type = "configuration.result",
                    Payload = JsonSerializer.SerializeToElement(command, NodeMessageAuthentication.JsonOptions) };
                var body = JsonSerializer.SerializeToUtf8Bytes(envelope, NodeMessageAuthentication.JsonOptions);
                using var request = new HttpRequestMessage(HttpMethod.Post, NodeMessageAuthentication.Prefix + envelope.Type)
                { Content = new ByteArrayContent(body) };
                request.Headers.Host = "localhost:7030";
                NodeMessageAuthentication.Sign(request, envelope, "key", key, body);
                return await http.SendAsync(request);
            }
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
