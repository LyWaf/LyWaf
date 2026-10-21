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

    private sealed class Audit : IAuditLogService
    {
        public List<string> Actions { get; } = [];
        public void Log(string username, string action, string ip) => Actions.Add(action);
        public (List<AuditLogEntry> Items, int Total) GetLogs(int offset = 0, int limit = 50) => ([], 0);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "lywaf-setup-http-" + Guid.NewGuid().ToString("N"));
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
