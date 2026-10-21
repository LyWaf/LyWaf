using System.Net;
using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class CcReleaseAuthorizationTests
{
    [Fact]
    public void OrdinaryReplicatingNodeCannotAdvanceUnsignedLocalGeneration()
    {
        using var store = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(new()
        { Enabled = true, NodeId = "ordinary", StateDirectory = Temp() }));
        Assert.Throws<InvalidOperationException>(() => store.AdvanceCcPolicyGeneration("v2:policy:192.0.2.1"));
        Assert.Equal(CcPolicyGeneration.Initial, store.GetCcPolicyGeneration("v2:policy:192.0.2.1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryPeerCanRelayButCannotForgeReleaseThroughActualRuntimeEndpoints(bool v2)
    {
        const string secret = "ordinary-peer-transport-key-32-bytes";
        var runtime = CcReleaseTestKeys.Configure(new RuntimeStateOptions
        {
            Enabled = true, NodeId = "receiver", StateDirectory = Temp(),
            Peers = new() { ["ordinary"] = new() { Secret = secret,
                Receive = new() { AllowedDomains = [CcPolicyGeneration.Domain], AllowUngroupedKeys = true,
                    TrustedForwardedSources = ["sender"] } } }
        });
        runtime.CcReleasePrivateKeyPem = "";
        var communication = new NodeCommunicationOptions
        {
            Enabled = true, RequireHttps = false, ClusterId = "cluster", NodeId = "receiver", StateDirectory = Temp(),
            Peers = new() { ["ordinary"] = new() { Keys = new() { ["key"] = secret }, AllowedOperations = ["runtime.merge"] } }
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(s => s.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new StaticOptionsMonitor<RuntimeStateOptions>(runtime));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new StaticOptionsMonitor<NodeCommunicationOptions>(communication));
        builder.Services.AddSingleton<IRuntimeStateStore, RuntimeStateStore>();
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<RuntimeStateAuthentication>();
        builder.Services.AddSingleton<RuntimeStateReplicator>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        app.MapNodeCommunication(7030);
        app.MapLyWafRuntimeStateApi(7030);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(10) };
            using var authority = new RuntimeStateStore(new StaticOptionsMonitor<RuntimeStateOptions>(
                CcReleaseTestKeys.Configure(new() { NodeId = "sender", StateDirectory = Temp() })));
            authority.AdvanceCcPolicyGeneration("v2:policy:192.0.2.1");
            var valid = Assert.Single(authority.ExportMutations());
            var receive = runtime.Peers["ordinary"].Receive;
            runtime.Peers["ordinary"].Receive = new();
            using (var denied = await client.SendAsync(Request(valid)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                using var error = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
                Assert.Equal("runtime_domain_forbidden", error.RootElement.GetProperty("errorCode").GetString());
            }
            runtime.Peers["ordinary"].Receive = receive;
            receive.TrustedForwardedSources.Clear();
            using (var denied = await client.SendAsync(Request(valid)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                using var error = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
                Assert.Equal("runtime_source_forbidden", error.RootElement.GetProperty("errorCode").GetString());
            }
            Assert.Empty(app.Services.GetRequiredService<IRuntimeStateStore>().GetSnapshots(CcPolicyGeneration.Domain));
            receive.TrustedForwardedSources.Add("sender");
            var forged = new[]
            {
                valid with { CcReleaseProof = null },
                valid with { Sequence = long.MaxValue },
                valid with { Key = "v2:another-policy:192.0.2.1" },
                valid with { Value = JsonSerializer.SerializeToElement(new CcPolicyGeneration(999, Guid.NewGuid().ToString("N"))) },
                valid with { CcReleaseProof = valid.CcReleaseProof! with { Issuer = "ordinary" }, NodeId = "ordinary" },
                valid with { CcReleaseProof = valid.CcReleaseProof! with { TrustDomain = "other-cluster" } }
            };
            foreach (var mutation in forged)
            {
                using var response = await client.SendAsync(Request(mutation));
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Empty(app.Services.GetRequiredService<IRuntimeStateStore>().GetSnapshots(CcPolicyGeneration.Domain));
            }
            using var accepted = await client.SendAsync(Request(valid));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Single(app.Services.GetRequiredService<IRuntimeStateStore>().GetSnapshots(CcPolicyGeneration.Domain));

            HttpRequestMessage Request(RuntimeStateMutation mutation)
            {
                var now = DateTimeOffset.UtcNow;
                var envelope = new NodeMessage(2, "cluster", "ordinary", "receiver", Guid.NewGuid().ToString("D"),
                    "release-test", "runtime.merge", now, now.AddMinutes(1),
                    JsonSerializer.SerializeToElement(new[] { mutation }, NodeMessageAuthentication.JsonOptions));
                var body = v2 ? JsonSerializer.SerializeToUtf8Bytes(envelope, NodeMessageAuthentication.JsonOptions)
                    : JsonSerializer.SerializeToUtf8Bytes(mutation, NodeMessageAuthentication.JsonOptions);
                var request = new HttpRequestMessage(HttpMethod.Post, v2 ? NodeMessageAuthentication.Prefix + "runtime.merge" : "/api/runtime-state/agent/merge")
                { Content = new ByteArrayContent(body) };
                request.Headers.Host = "localhost:7030";
                request.Content.Headers.ContentType = new("application/json");
                if (v2) NodeMessageAuthentication.Sign(request, envelope, "key", secret, body);
                else RuntimeStateAuthentication.Sign(request, "ordinary", secret, body);
                return request;
            }
        }
        finally { await app.StopAsync(); }
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "cc-release-auth-" + Guid.NewGuid().ToString("N"));
}
