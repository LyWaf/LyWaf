using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using LyWaf.Services.AuditLog;
using LyWaf.Services.Auth;
using LyWaf.Services.Distribution;
using LyWaf.Services.ManagementApi;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSetupHttpTests
{
    [Fact]
    public async Task CredentialIsWriteOnlyCannotBeReplayedAndRevocationRefreshesPermission()
    {
        await using var host = await Host.StartAsync();
        using var created = await host.SendAsync(HttpMethod.Post, "/api/node-setup/peers", new { name = "edge" }, "one-time-credential");
        created.EnsureSuccessStatusCode();
        Assert.Contains("no-store", created.Headers.CacheControl!.ToString());
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var credential = body.RootElement.GetProperty("credential").GetString()!;
        var key = NodeSetupStore.DecodeCredential(credential);
        using var replay = await host.SendAsync(HttpMethod.Post, "/api/node-setup/peers", new { name = "edge" }, "one-time-credential");
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.DoesNotContain(credential, await replay.Content.ReadAsStringAsync());
        Assert.Single(host.Store.Peers());
        using var read = await host.SendAsync(HttpMethod.Get, "/api/node-setup", auditor: true);
        read.EnsureSuccessStatusCode();
        var json = await read.Content.ReadAsStringAsync();
        Assert.DoesNotContain(credential, json);
        Assert.DoesNotContain(key.Secret, json);
        Assert.All(host.Audit.Actions, action => Assert.DoesNotContain(key.Secret, action));
        using var denied = await host.SendAsync(HttpMethod.Post, "/api/node-setup/peers", new { name = "forbidden" }, auditor: true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Single(host.Store.Peers());
        Assert.True(host.App.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue.Peers[key.NodeId].Enabled);
        using var disabled = await host.SendAsync(HttpMethod.Put, "/api/node-setup/peers/" + key.NodeId, new { enabled = false });
        disabled.EnsureSuccessStatusCode();
        Assert.False(host.App.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue.Peers[key.NodeId].Enabled);
        Assert.False(host.App.Services.GetRequiredService<IOptionsMonitor<DistributionOptions>>().CurrentValue.Nodes[key.NodeId].Enabled);
    }

    [Fact]
    public async Task InvalidCredentialIsRejectedWithoutEchoingItOrChangingRole()
    {
        await using var host = await Host.StartAsync();
        using var response = await host.SendAsync(HttpMethod.Put, "/api/node-setup", new
            { mode = "Node", controllerUrl = "https://controller.example/", credential = "secret-invalid-credential" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("secret-invalid-credential", await response.Content.ReadAsStringAsync());
        Assert.Equal("Controller", host.Store.Status().Mode);
    }

    [Fact]
    public async Task PendingRoleChangeIsExplicitAndDoesNotApplyThroughAnUnrelatedPeerRefresh()
    {
        await using var host = await Host.StartAsync();
        using var changed = await host.SendAsync(HttpMethod.Put, "/api/node-setup", new { mode = "Standalone" });
        changed.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("settings").GetProperty("restartRequired").GetBoolean());
        Assert.Equal("Controller", host.App.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue.Mode);
        using var refused = await host.SendAsync(HttpMethod.Post, "/api/node-setup/peers", new { name = "new" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(host.Store.Peers());
    }

    [Fact]
    public async Task SharedKeyAndDeletionRequireAdminRefreshLiveOptionsAndNeverExposeKeyInStatus()
    {
        await using var host = await Host.StartAsync();
        var issued = await host.Store.CreateCredentialAsync("edge", default);
        var original = NodeSetupStore.DecodeCredential(issued.Credential);
        var secret = new string('c', 64);
        using var denied = await host.SendAsync(HttpMethod.Put, "/api/node-setup/shared-key", new { key = secret }, auditor: true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var saved = await host.SendAsync(HttpMethod.Put, "/api/node-setup/shared-key", new { key = secret });
        saved.EnsureSuccessStatusCode();
        Assert.DoesNotContain(secret, await saved.Content.ReadAsStringAsync());
        var config = host.App.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue;
        Assert.Equal(secret, config.Peers[original.NodeId].Keys[config.Peers[original.NodeId].ActiveKeyId]);
        Assert.DoesNotContain(original.Secret, config.Peers[original.NodeId].Keys.Values);
        using var exported = await host.SendAsync(HttpMethod.Post, $"/api/node-setup/peers/{original.NodeId}/credential", idempotencyKey: "export-once");
        exported.EnsureSuccessStatusCode();
        Assert.Contains("no-store", exported.Headers.CacheControl!.ToString());
        using var response = JsonDocument.Parse(await exported.Content.ReadAsStringAsync());
        Assert.Equal(secret, NodeSetupStore.DecodeCredential(response.RootElement.GetProperty("credential").GetString()!).Secret);
        using var replay = await host.SendAsync(HttpMethod.Post, $"/api/node-setup/peers/{original.NodeId}/credential", idempotencyKey: "export-once");
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using var auditorDelete = await host.SendAsync(HttpMethod.Delete, $"/api/node-setup/peers/{original.NodeId}", auditor: true);
        Assert.Equal(HttpStatusCode.Forbidden, auditorDelete.StatusCode);
        using var deleted = await host.SendAsync(HttpMethod.Delete, $"/api/node-setup/peers/{original.NodeId}");
        deleted.EnsureSuccessStatusCode();
        Assert.Empty(host.Store.Peers());
        Assert.DoesNotContain(original.NodeId, host.App.Services.GetRequiredService<IOptionsMonitor<NodeCommunicationOptions>>().CurrentValue.Peers.Keys);
        using var missing = await host.SendAsync(HttpMethod.Post, $"/api/node-setup/peers/{original.NodeId}/credential");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var status = await host.SendAsync(HttpMethod.Get, "/api/node-setup", auditor: true);
        Assert.DoesNotContain(secret, await status.Content.ReadAsStringAsync());
        Assert.All(host.Audit.Actions, action => Assert.DoesNotContain(secret, action));
    }

    [Fact]
    public async Task SharedKeyDirectJoinPersistsItsMethodAndIdentityWithoutManualCredential()
    {
        await using var host = await Host.StartAsync();
        var shared = new string('d', 64);
        using var configured = await host.SendAsync(HttpMethod.Put, "/api/node-setup/shared-key", new { key = shared });
        configured.EnsureSuccessStatusCode();
        var clientRoot = Path.Combine(host.Root, "joining-client");
        var node = new NodeSetupStore(clientRoot);
        var endpoint = host.App.Urls.Single() + "/";
        var status = await node.ConfigureAsync("Node", endpoint, null, default,
            authenticationMode: "SharedKey", sharedKey: shared, nodeName: "Shared node");
        Assert.Equal("SharedKey", status.AuthenticationMode);
        Assert.True(status.RestartRequired);
        Assert.Single(host.Store.Peers());
        var id = status.NodeId;
        var reloaded = new NodeSetupStore(clientRoot);
        Assert.Equal("SharedKey", reloaded.Status().AuthenticationMode);
        Assert.Equal(id, reloaded.Status().NodeId);
        Assert.Equal(shared, reloaded.Active!.Node!.Credential.Secret);
        Assert.NotEmpty(reloaded.Active.Node.Credential.ReleasePublicKeyPem);
        var unchanged = await reloaded.ConfigureAsync("Node", endpoint, null, default, authenticationMode: "SharedKey");
        Assert.False(unchanged.RestartRequired);
        await reloaded.ConfigureAsync("Node", endpoint, null, default, authenticationMode: "SharedKey", sharedKey: shared);
        Assert.Single(host.Store.Peers());
        Assert.Equal(id, reloaded.Status().NodeId);
        await Assert.ThrowsAsync<HttpRequestException>(() => reloaded.ConfigureAsync("Node", endpoint, null, default,
            authenticationMode: "SharedKey", sharedKey: new string('e', 64)));
        Assert.Equal(id, reloaded.Status().NodeId);
        var rotated = new string('e', 64);
        await host.Store.SetSharedCommunicationKeyAsync(rotated, default);
        await reloaded.ConfigureAsync("Node", endpoint, null, default, authenticationMode: "SharedKey", sharedKey: rotated);
        Assert.Equal(id, reloaded.Status().NodeId);
        Assert.Single(host.Store.Peers());
        await host.Store.DeletePeerAsync(id!, default);
        await Assert.ThrowsAsync<HttpRequestException>(() => reloaded.ConfigureAsync("Node", endpoint, null, default,
            authenticationMode: "SharedKey", sharedKey: rotated));
        Assert.Empty(host.Store.Peers());
    }

    [Fact]
    public async Task EnrollmentRequiresFreshSignedRequestAndRejectsReplayAndDisabledIdentity()
    {
        await using var host = await Host.StartAsync();
        var secret = new string('f', 64);
        await host.Store.SetSharedCommunicationKeyAsync(secret, default);
        var request = NodeSharedKeyEnrollment.Create(secret, "node-auto", "Auto node");
        using var bad = await host.SendAsync(HttpMethod.Post, NodeSharedKeyEnrollment.Path, request with { Name = "Tampered" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        using var accepted = await host.SendAsync(HttpMethod.Post, NodeSharedKeyEnrollment.Path, request);
        accepted.EnsureSuccessStatusCode();
        Assert.Contains("no-store", accepted.Headers.CacheControl!.ToString());
        var reply = await accepted.Content.ReadFromJsonAsync<NodeSharedKeyReply>();
        Assert.Equal(request.Nonce, reply!.Nonce);
        Assert.Equal(NodeSharedKeyEnrollment.Reply(secret, request, reply.Credential).Signature, reply.Signature);
        using var replay = await host.SendAsync(HttpMethod.Post, NodeSharedKeyEnrollment.Path, request);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        await host.Store.SetPeerEnabledAsync(request.NodeId, false, default);
        using var disabled = await host.SendAsync(HttpMethod.Post, NodeSharedKeyEnrollment.Path,
            NodeSharedKeyEnrollment.Create(secret, request.NodeId, request.Name));
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        Assert.DoesNotContain(secret, string.Join('\n', host.Audit.Actions));
    }

    [Fact]
    public async Task FailedFirstJoinReusesItsPersistentIdentityOnRetry()
    {
        await using var host = await Host.StartAsync();
        var secret = new string('b', 64);
        await host.Store.SetSharedCommunicationKeyAsync(secret, default);
        var root = Path.Combine(host.Root, "retry-client");
        var client = new NodeSetupStore(root);
        var endpoint = host.App.Urls.Single() + "/";
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ConfigureAsync("Node", endpoint, null, default,
            authenticationMode: "SharedKey", sharedKey: new string('a', 64)));
        Assert.Equal("Standalone", client.Status().Mode);
        var stableId = await File.ReadAllTextAsync(Path.Combine(root, "shared-node-id"));
        Assert.Empty(host.Store.Peers());
        client = new NodeSetupStore(root);
        var joined = await client.ConfigureAsync("Node", endpoint, null, default, authenticationMode: "SharedKey", sharedKey: secret);
        Assert.Equal(stableId, joined.NodeId);
        Assert.Single(host.Store.Peers());
    }

    private sealed class Audit : IAuditLogService
    {
        public List<string> Actions { get; } = [];
        public void Log(string username, string action, string ip) => Actions.Add(action);
        public (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50) => ([], 0);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "lywaf-setup-http-" + Guid.NewGuid().ToString("N"));
        public string Root => root;
        public WebApplication App { get; private set; } = null!;
        public NodeSetupStore Store { get; private set; } = null!;
        public Audit Audit { get; } = new();
        private HttpClient client = null!;
        public static async Task<Host> StartAsync()
        {
            var host = new Host();
            await new NodeSetupStore(host.root).ConfigureAsync("Controller", null, null, default);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            host.Store = builder.Configuration.AddNodeSetup(builder.Services, host.root);
            builder.Services.AddOptions<NodeCommunicationOptions>().Bind(builder.Configuration.GetSection("NodeCommunication"));
            builder.Services.AddOptions<DistributionOptions>().Bind(builder.Configuration.GetSection("Distribution"));
            builder.Services.AddSingleton<IAuditLogService>(host.Audit);
            builder.Services.AddSingleton<ManagementIdempotency>();
            host.App = builder.Build();
            host.App.UseRouting();
            host.App.Use(async (context, next) =>
            {
                context.Items["Username"] = "fixture";
                var grants = ManagementPermissions.ForRole(context.Request.Headers["X-Test-Role"] == "auditor" ? "auditor" : "admin");
                if (!ManagementAuthorization.IsAllowed(context, grants)) { context.Response.StatusCode = 403; return; }
                await next();
            });
            host.App.UseManagementIdempotency(port);
            host.App.MapNodeSetup(port);
            await host.App.StartAsync();
            host.client = new() { BaseAddress = new(host.App.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            return host;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? idempotencyKey = null, bool auditor = false)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
            if (auditor) request.Headers.Add("X-Test-Role", "auditor");
            return await client.SendAsync(request);
        }
        public async ValueTask DisposeAsync()
        {
            client?.Dispose();
            if (App is not null) { await App.StopAsync(); await App.DisposeAsync(); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
