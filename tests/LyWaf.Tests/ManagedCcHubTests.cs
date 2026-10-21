using System.Net;
using System.Net.Sockets;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed partial class ManagedRuntimeHubTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerEndorsedEvidenceAggregatesCountsAndTriggersCcWithoutNodePeerKeys(bool managedMembership)
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-managed-cc-" + Guid.NewGuid().ToString("N"));
        var hosts = new List<WebApplication>();
        try
        {
            var setup = new NodeSetupStore(Path.Combine(root, "controller", "setup"));
            await setup.ConfigureAsync("Controller", null, null, default);
            var inviteA = await setup.CreateCredentialAsync("a", default);
            var inviteB = await setup.CreateCredentialAsync("b", default);
            var credentialA = NodeSetupStore.DecodeCredential(inviteA.Credential);
            var credentialB = NodeSetupStore.DecodeCredential(inviteB.Credential);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var url = $"http://127.0.0.1:{port}/";
            await new NodeSetupStore(Path.Combine(root, "a", "setup")).ConfigureAsync("Node", url, inviteA.Credential, default);
            await new NodeSetupStore(Path.Combine(root, "b", "setup")).ConfigureAsync("Node", url, inviteB.Credential, default);
            var expected = new[] { credentialA.ControllerNodeId, credentialA.NodeId, credentialB.NodeId };
            var rule = new AdvancedCcRule { Id = "hub-cc", Period = 5, Threshold = 100, ActionSeconds = 30,
                Action = CcAction.Block, RequireCompleteCoverage = true,
                UseManagedMembership = managedMembership, ExpectedNodes = managedMembership ? [] : expected };
            var controller = Build(Path.Combine(root, "controller"), port, rule);
            var a = Build(Path.Combine(root, "a"), 0, rule);
            var b = Build(Path.Combine(root, "b"), 0, rule);
            hosts.AddRange([controller, a, b]);
            foreach (var host in hosts) await host.StartAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var config = controller.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
            await Until(() => a.Services.GetRequiredService<NodeHubMembership>().IsFresh(config.ClusterId, config.NodeId)
                && b.Services.GetRequiredService<NodeHubMembership>().IsFresh(config.ClusterId, config.NodeId), deadline.Token);
            var stores = hosts.Select(host => host.Services.GetRequiredService<RuntimeStateStore>()).ToArray();
            var cohorts = hosts.Select(host => host.Services.GetRequiredService<CcCohortState>()).ToArray();
            foreach (var cohort in cohorts)
                Assert.Equal(expected.Order(StringComparer.Ordinal), cohort.ExpectedNodes(rule));
            var decisions = hosts.Select(host => host.Services.GetRequiredService<CcDecisionService>()).ToArray();
            foreach (var cohort in cohorts) cohort.ObserveRules([rule]);
            var policies = decisions.Select(service => service.Register(rule, "host:shared.example", "192.0.2.7")).ToArray();
            var window = CcDecisionService.Window(DateTimeOffset.UtcNow, rule.Period).AddSeconds(rule.Period);
            await Task.Delay(window - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(20), deadline.Token);
            var amounts = new[] { 30, 40, 35 };
            for (var i = 0; i < stores.Length; i++)
                stores[i].IncrementWindow("cc.access", policies[i].StateKey, window, TimeSpan.FromSeconds(rule.Period), amounts[i]);
            var query = new CcExecutionQuery(policies[1], CcDecisionService.DecisionId(policies[1], window), window);
            var client = a.Services.GetRequiredService<CcPeerQueryClient>();
            var nodeOptions = a.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
            Assert.Single(nodeOptions.Peers);
            Assert.False(nodeOptions.Peers.ContainsKey(credentialB.NodeId));
            var proof = await client.QueryAsync(credentialB.NodeId, [query], false, deadline.Token);
            Assert.Equal(config.NodeId, Assert.Single(proof.Replies).ViaController);
            Assert.Null(proof.Replies[0].ReceivedAt);
            Assert.Null(proof.Replies[0].ProcessingMilliseconds);
            Assert.Equal(35, proof.Replies[0].Evidence.LocalCount);
            var direct = await client.QueryAsync(config.NodeId, [query], false, deadline.Token);
            Assert.NotNull(Assert.Single(direct.Replies).ReceivedAt);
            Assert.NotNull(direct.Replies[0].ProcessingMilliseconds);
            if (managedMembership)
            {
                using var monitor = new CcCohortMonitor(cohorts[1], client,
                    a.Services.GetRequiredService<IStatisticService>(), stores[1]);
                await monitor.StartAsync(deadline.Token);
                try
                {
                    await Until(() => cohorts[1].Read(policies[1], rule) is { Coverage.Status: "Complete", Count: 105 }, deadline.Token);
                }
                finally { await monitor.StopAsync(default); }
            }
            await client.ProbeAsync(config.NodeId, query, cohorts[1], deadline.Token);
            await client.ProbeAsync(credentialB.NodeId, query, cohorts[1], deadline.Token);
            var snapshot = cohorts[1].Read(policies[1], rule);
            Assert.Equal("Complete", snapshot.Coverage.Status);
            Assert.Equal(105, snapshot.Count);
            var decision = decisions[1].Evaluate(policies[1], rule, window, 40).Decision;
            Assert.NotNull(decision);
            Assert.Equal(105, decision.ObservedCount);
            for (var i = 0; i < hosts.Count; i++)
            {
                if (i == 1) continue;
                var localQuery = new CcExecutionQuery(policies[i], decision.DecisionId, window);
                var localClient = hosts[i].Services.GetRequiredService<CcPeerQueryClient>();
                var localId = stores[i].GetStatus().NodeId;
                await Task.WhenAll(expected.Where(node => node != localId)
                    .Select(node => localClient.ProbeAsync(node, localQuery, cohorts[i], deadline.Token)));
                var applied = decisions[i].Evaluate(policies[i], rule, window, amounts[i]).Decision;
                Assert.NotNull(applied);
                Assert.Equal(decision.DecisionId, applied.DecisionId);
                Assert.Equal(105, applied.ObservedCount);
                Assert.Equal("Applied", decisions[i].InspectExecution(policies[i], decision.DecisionId).Status);
            }

            // Current controller admission, not the historical membership list, decides whether a new proof may be issued.
            config.Peers[credentialB.NodeId].AdmissionState = "Revoked";
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ProbeAsync(credentialB.NodeId, query, cohorts[1], deadline.Token));
            Assert.Contains(credentialB.NodeId, cohorts[1].Read(policies[1], rule).Coverage.MissingNodes);
            config.Peers[credentialB.NodeId].AdmissionState = "Active";
            await client.ProbeAsync(credentialB.NodeId, query, cohorts[1], deadline.Token);
            var runtime = controller.Services.GetRequiredService<IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
            runtime.Peers[credentialA.NodeId].Send.AllowedDomains.Clear();
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ProbeAsync(credentialB.NodeId, query, cohorts[1], deadline.Token));
            Assert.Contains(credentialB.NodeId, cohorts[1].Read(policies[1], rule).Coverage.MissingNodes);
            a.Services.GetRequiredService<NodeHubMembership>().Invalidate();
            Assert.Equal("Partial", cohorts[1].Read(policies[1], rule).Coverage.Status);
            if (managedMembership)
            {
                Assert.Empty(cohorts[1].ExpectedNodes(rule));
                Assert.Equal(0, cohorts[1].Read(policies[1], rule).Count);
                Assert.False(cohorts[1].Execution(policies[1], rule, decisions[1].InspectExecution(policies[1], decision.DecisionId)).AllNodesConfirmed);
            }
        }
        finally
        {
            foreach (var host in hosts.AsEnumerable().Reverse()) { await host.StopAsync(); await host.DisposeAsync(); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
