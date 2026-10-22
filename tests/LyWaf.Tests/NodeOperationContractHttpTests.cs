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

public sealed class NodeOperationContractHttpTests
{
    public static IEnumerable<object[]> AuthenticationCases() => NodeProtocolOperations.All
        .Concat(LyWaf.Plugins.Sys.Analysis.AnalysisNodeRequests.Capabilities.Select(item => item.LegacyOperation!)).SelectMany(operation =>
        new[] { "signature", "permission", "storage" }.Select(fault => new object[] { operation, fault }));

    [Theory]
    [MemberData(nameof(AuthenticationCases))]
    public async Task ProductionRoutesReturnCompleteAuthenticationContract(string operation, string fault)
    {
        await using var host = await NodeContractHttpHost.Start();
        Assert.Equal(14, NodeProtocolOperations.All.Count);
        if (fault == "permission") host.Protocol.Peers["sender"].AllowedOperations.Clear();
        if (fault == "storage") File.WriteAllText(Path.Combine(host.Directory, "replay-v2"), "not a directory");
        using var response = await host.Send(operation, new { }, fault == "signature");
        var expected = fault switch
        {
            "signature" => (401, "node_invalid_signature"),
            "permission" => (403, "node_operation_forbidden"),
            _ => (503, "node_replay_store_unavailable")
        };
        Assert.Equal(expected.Item1, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertError(body.RootElement, expected.Item2, expected.Item1 == 503);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("messageId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("operationId").ValueKind);
    }

    internal static void AssertError(JsonElement body, string code, bool retryable)
    {
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(code, body.GetProperty("errorCode").GetString());
        Assert.Equal("node.errors." + code, body.GetProperty("messageKey").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal("Rejected", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Object, body.GetProperty("messageArgs").ValueKind);
        Assert.Equal(retryable, body.GetProperty("retryable").GetBoolean());
    }
}

// All requests traverse a loopback socket, production routing, HMAC and persistent nonce admission.
internal sealed class NodeContractHttpHost : IAsyncDisposable
{
    internal const string Secret = "contract-http-secret-at-least-32-bytes";
    internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "lywaf-contract-" + Guid.NewGuid().ToString("N"));
    internal NodeCommunicationOptions Protocol { get; }
    internal RuntimeStateOptions Runtime { get; }
    internal WebApplication App { get; private set; } = null!;
    internal HttpClient Client { get; private set; } = null!;

    private NodeContractHttpHost()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Protocol = new()
        {
            Enabled = true, RequireHttps = false, ClusterId = "contract", NodeId = "receiver", StateDirectory = Directory,
            Peers = new() { ["sender"] = new() { Role = "Controller", Keys = new() { ["key"] = Secret },
                AllowedOperations = NodeProtocolOperations.All.Concat(LyWaf.Plugins.Sys.Analysis.AnalysisNodeRequests.Capabilities.Select(item => item.LegacyOperation!)).ToList() } }
        };
        Runtime = new() { Enabled = true, NodeId = "receiver", StateDirectory = Path.Combine(Directory, "runtime"),
            SharedSecret = Secret, MaxStateEntries = 1 };
        Runtime.Peers["sender"] = new() { Endpoint = "http://127.0.0.1:1", Receive = new()
            { AllowedDomains = ["ip.blocked"], AllowUngroupedKeys = true } };
    }

    internal static async Task<NodeContractHttpHost> Start(bool legacy = false,
        Action<IServiceCollection, NodeContractHttpHost>? configure = null)
    {
        var host = new NodeContractHttpHost();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IOptionsMonitor<NodeCommunicationOptions>>(new StaticOptionsMonitor<NodeCommunicationOptions>(host.Protocol));
        builder.Services.AddSingleton<IOptionsMonitor<RuntimeStateOptions>>(new StaticOptionsMonitor<RuntimeStateOptions>(host.Runtime));
        builder.Services.AddSingleton<NodeMessageAuthentication>();
        builder.Services.AddSingleton<IRuntimeStateStore, RuntimeStateStore>();
        builder.Services.AddSingleton<RuntimeStateAuthentication>();
        builder.Services.AddSingleton<RuntimeStateReplicator>();
        configure?.Invoke(builder.Services, host);
        LyWaf.Plugins.Sys.Analysis.AnalysisNodeRequests.Register(builder.Services);
        host.App = builder.Build();
        host.App.MapNodeCommunication(7030);
        if (legacy) host.App.MapLyWafRuntimeStateApi(7030);
        await host.App.StartAsync();
        host.Client = new() { BaseAddress = new(host.App.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(10) };
        return host;
    }

    internal Task<HttpResponseMessage> Send(string operation, object payload, bool badSignature = false) =>
        Send(new NodeMessage(2, "contract", "sender", "receiver", Guid.NewGuid().ToString("D"), "contract-operation",
            operation, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            JsonSerializer.SerializeToElement(payload, NodeMessageAuthentication.JsonOptions)), badSignature);

    internal async Task<HttpResponseMessage> Send(NodeMessage message, bool badSignature = false)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, NodeMessageAuthentication.Prefix + message.Type)
            { Content = new ByteArrayContent(body) };
        request.Headers.Host = "localhost:7030";
        request.Content.Headers.ContentType = new("application/json");
        NodeMessageAuthentication.Sign(request, message, "key", badSignature ? new string('x', 40) : Secret, body);
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null) { await App.StopAsync(); await App.DisposeAsync(); }
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
    }
}
