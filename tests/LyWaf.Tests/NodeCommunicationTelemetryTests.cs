using System.Net;
using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.Telemetry;
using LyWaf.Services.RuntimeState;
using LyWaf.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Yarp.ReverseProxy;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeCommunicationTelemetryTests
{
    [Fact]
    public void ReplicationAcknowledgementUsesMutationAgeAndExcludesFutureTimestamps()
    {
        var telemetry = new NodeCommunicationTelemetry();
        var empty = telemetry.Capture().ReplicationAcknowledgement!;
        Assert.Equal(0, empty.Samples);
        var mutation = DateTimeOffset.UtcNow.AddSeconds(-30);
        telemetry.ObserveReplicationAcknowledgement(mutation);
        var first = telemetry.Capture().ReplicationAcknowledgement!;
        Assert.Equal(1, first.Samples);
        Assert.InRange(first.TotalMilliseconds, 30000, (DateTimeOffset.UtcNow - mutation).TotalMilliseconds);
        Assert.Equal(first.TotalMilliseconds, first.MaxMilliseconds);
        telemetry.ObserveReplicationAcknowledgement(DateTimeOffset.UtcNow.AddHours(1));
        var skew = telemetry.Capture().ReplicationAcknowledgement!;
        Assert.Equal(first.Samples, skew.Samples);
        Assert.Equal(first.TotalMilliseconds, skew.TotalMilliseconds);
        Assert.Equal(first.MaxMilliseconds, skew.MaxMilliseconds);
        Assert.Equal(1, skew.ClockSkewSamples);
        Assert.Equal(0, first.ClockSkewSamples);
        Assert.Equal("mutation-change-to-transport-ack; not-business-execution-ack", skew.Semantics);
        telemetry.ObserveReplicationAcknowledgement(mutation);
        Assert.Equal(2, telemetry.Capture().ReplicationAcknowledgement!.Samples);
    }

    [Fact]
    public void ReplicationAcknowledgementSurvivesDashboardDiagnosticJsonContract()
    {
        var before = NodeCommunicationTelemetry.Shared.Capture().ReplicationAcknowledgement!.Samples;
        NodeCommunicationTelemetry.Shared.ObserveReplicationAcknowledgement(DateTimeOffset.UtcNow.AddSeconds(-1));
        var snapshot = new NodeDashboardDiagnostics().CaptureLocal();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var roundtrip = JsonSerializer.Deserialize<NodeLocalDiagnostics>(JsonSerializer.Serialize(snapshot, json), json)!;
        Assert.True(roundtrip.Communication.ReplicationAcknowledgement!.Samples > before);
        Assert.True(roundtrip.Communication.ReplicationAcknowledgement.TotalMilliseconds >= 1000);
        Assert.Equal(snapshot.Communication.ReplicationAcknowledgement, roundtrip.Communication.ReplicationAcknowledgement);
    }

    [Fact]
    public async Task RealHttpObservesBothDirectionsRejectionBytesAndLatencyWithoutPayloadLabels()
    {
        var before = NodeCommunicationTelemetry.Shared.Capture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(x => x.Listen(IPAddress.Loopback, 0));
        builder.Services.AddHostedService<NodeCommunicationHttpObserver>();
        builder.Services.AddTransient<IStartupFilter, NodeCommunicationObservationStartup>();
        await using var app = builder.Build();
        app.MapPost(NodeMessageAuthentication.Prefix + "node.status", () =>
            Results.Json(new { success = false, errorCode = "node_invalid_signature" }, statusCode: 401));
        await app.StartAsync();
        try
        {
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient();
            using var result = await http.PostAsync(url + NodeMessageAuthentication.Prefix + "node.status", new StringContent("SECRET-NOT-A-LABEL"));
            Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
            await result.Content.ReadAsStringAsync();
            var after = NodeCommunicationTelemetry.Shared.Capture();
            foreach (var direction in new[] { "inbound", "outbound" })
            {
                var sample = after.Series.Single(s => s.Operation == "node.status" && s.Direction == direction);
                var old = before.Series.FirstOrDefault(s => s.Operation == "node.status" && s.Direction == direction);
                Assert.True(sample.Requests > (old?.Requests ?? 0));
                Assert.True(sample.Rejected > (old?.Rejected ?? 0));
                Assert.True(sample.RequestBytes >= 18);
                Assert.Equal(sample.Requests, sample.DurationCounts.Sum());
            }
            Assert.True(after.Events["signature_rejected"] > before.Events.GetValueOrDefault("signature_rejected"));
            Assert.DoesNotContain("SECRET-NOT-A-LABEL", JsonSerializer.Serialize(after));
            Assert.DoesNotContain(url, JsonSerializer.Serialize(after));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task RealInboxReuseAndOutboxQueryEmitSemanticEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new NodeCommunicationOptions { StateDirectory = directory };
            var inbox = new NodeCommandInbox(new StaticOptionsMonitor<NodeCommunicationOptions>(options));
            var now = DateTimeOffset.UtcNow;
            var message = new NodeMessage(2, "cluster", "controller", "node", Guid.NewGuid().ToString("D"), "deployment",
                "configuration.commit", now, now.AddMinutes(1), JsonSerializer.SerializeToElement(new { }));
            var before = NodeCommunicationTelemetry.Shared.Capture();
            var executions = 0;
            Task<NodeAcknowledgement> Execute() { executions++; return Task.FromResult(new NodeAcknowledgement(true, "node", "deployment", "commit")); }
            await inbox.ExecuteAsync(message, Execute, default);
            await inbox.ExecuteAsync(message, Execute, default);
            Assert.Equal(1, executions);
            var outbox = new NodeCommandOutbox(directory);
            var calls = 0;
            await outbox.ExecuteAsync(message, "https://node", (_, query, _) =>
            {
                calls++;
                if (calls == 1) throw new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable);
                Assert.True(query);
                return Task.FromResult(new NodeAcknowledgement(true, "node", "deployment", "commit"));
            }, default);
            var after = NodeCommunicationTelemetry.Shared.Capture();
            Assert.True(after.Events["idempotency_hit"] > before.Events.GetValueOrDefault("idempotency_hit"));
            Assert.True(after.Events["retry_query"] > before.Events.GetValueOrDefault("retry_query"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void OperationAndEventCardinalityIsBoundedAndNoSecretsBecomeLabels()
    {
        var collector = new NodeCommunicationTelemetry();
        for (var i = 0; i < 10000; i++)
        { collector.Record("untrusted-" + i, false, 500, 3, null, null); collector.Event("secret-" + i); }
        var sample = collector.Capture();
        Assert.Equal("other", Assert.Single(sample.Series).Operation);
        Assert.Equal(10000, sample.Series[0].UnknownSizes);
        Assert.Empty(sample.Events);
    }

    [Fact]
    public async Task PendingUnknownUsesEndpointBoundRecordsWithoutExposingPayloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new NodeCommunicationOptions { Enabled = true, NodeId = "controller", ClusterId = "cluster", StateDirectory = directory };
            var outbox = new NodeCommandOutbox(directory);
            var now = DateTimeOffset.UtcNow;
            var message = new NodeMessage(2, "cluster", "controller", "node", Guid.NewGuid().ToString("D"), "deployment",
                "configuration.commit", now, now.AddMinutes(1), JsonSerializer.SerializeToElement(new { secret = "NEVER-EXPOSE" }));
            using var cancel = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.ExecuteAsync(message, "https://node", (_, _, _) =>
            { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }, cancel.Token));
            var diagnostics = new NodeDashboardDiagnostics(communication: new StaticOptionsMonitor<NodeCommunicationOptions>(config));
            var distribution = new DistributionOptions { Nodes = new() { ["node"] = new() { Endpoint = "https://node" } } };
            var result = (await diagnostics.CapturePeersAsync(distribution, now.AddSeconds(5), default))["node"];
            Assert.Equal(1, result.PendingCommands);
            Assert.Equal(1, result.UnknownCommands);
            Assert.True(result.OldestPendingMilliseconds >= 5000);
            Assert.DoesNotContain("NEVER-EXPOSE", JsonSerializer.Serialize(result));
            distribution.Nodes["node"].Endpoint = "https://new-node";
            Assert.Equal(0, (await diagnostics.CapturePeersAsync(distribution, now, default))["node"].PendingCommands);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DashboardReadsDurableHandshakeRevisionsWithoutInventingDeploymentAcknowledgement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "telemetry-registration-" + Guid.NewGuid().ToString("N"));
        var config = new NodeCommunicationOptions { Enabled = true, Role = "Controller", NodeId = "controller",
            ClusterId = "cluster", StateDirectory = directory };
        config.Peers["node"] = new() { Role = "Node", AllowedOperations = ["node.handshake"] };
        var monitor = new StaticOptionsMonitor<NodeCommunicationOptions>(config);
        var observer = new NodeRegistrationObserver(monitor);
        var now = DateTimeOffset.UtcNow;
        var boot = Guid.NewGuid().ToString("N");
        var handshake = new NodeHandshake("cluster", "node", boot, "Node", now, "1.0", [2],
            new("applied", new string('a', 64), "file-version"),
            new(NodeHandshakeMetadataService.ComputeDigest([]), []));
        try
        {
            var observation = observer.ObserveAuthenticated(new(2, "cluster", "node", "controller", Guid.NewGuid().ToString("D"),
                "handshake", "node.handshake", now, now.AddMinutes(1),
                JsonSerializer.SerializeToElement(handshake, NodeMessageAuthentication.JsonOptions)));
            var diagnostics = new NodeDashboardDiagnostics(communication: monitor, registrations: observer);
            var distribution = new DistributionOptions();
            distribution.Nodes["node"] = new() { Endpoint = "https://node" };
            var result = (await diagnostics.CapturePeersAsync(distribution, now, default))["node"];
            Assert.Equal(boot, result.BootId);
            Assert.Equal(observation.LastSeen, result.LastHandshakeAt);
            Assert.Equal("NeedsReconciliation", result.ReconciliationState);
            Assert.Equal("applied", result.ActualAppliedVersion);
            Assert.Equal("file-version", result.ActualFileVersion);
            Assert.Null(result.AcknowledgedAppliedVersion);
            Assert.Null(result.ExpectedSourceVersion);
            Assert.Null(result.Error);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task AgentUsesLeasedIdentityBootForSnapshotsAndEventIdsAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "telemetry-identity-" + Guid.NewGuid().ToString("N"));
        var config = new NodeCommunicationOptions { Enabled = true, NodeId = "node", ClusterId = "cluster", StateDirectory = directory };
        var monitor = new StaticOptionsMonitor<NodeCommunicationOptions>(config);
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddReverseProxy().LoadFromMemory([], []);
        await using var app = builder.Build();
        var provider = app.Services;
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(new()));
        var node = new DistributionNodeService(new StaticOptionsMonitor<DistributionOptions>(new()
        { NodeId = "node", StateDirectory = directory }), null!, null!, new ConfigurationBuilder().Build());
        var application = Guid.NewGuid().ToString("N");
        SharedData.RecordInterceptEvent("192.0.2.99", application, "", "", "rule", "waf");
        try
        {
            NodeDashboardSnapshot first;
            using (var identity = new NodeIdentityService(monitor))
            {
                await identity.StartAsync(default);
                var agent = new NodeDashboardAgent(node, store, provider.GetRequiredService<IProxyStateLookup>(), identity: identity);
                first = agent.Capture();
                Assert.Equal(identity.Capture().BootId, first.BootId);
                Assert.Equal("node_identity", first.BootSource);
            }
            using var restarted = new NodeIdentityService(monitor);
            await restarted.StartAsync(default);
            var second = new NodeDashboardAgent(node, store, provider.GetRequiredService<IProxyStateLookup>(), identity: restarted).Capture();
            Assert.NotEqual(first.BootId, second.BootId);
            var oldEvent = first.RecentEvents.Single(e => e.Application == application);
            var newEvent = second.RecentEvents.Single(e => e.Application == application);
            Assert.NotEqual(oldEvent.EventId, newEvent.EventId);
            Assert.Equal(second.BootId, newEvent.BootId);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
