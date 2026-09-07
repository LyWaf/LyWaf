using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace LyWaf.Tests;

public sealed class DistributionAuthenticationTests
{
    [Fact]
    public async Task SignedRequestIsAcceptedOnceAndReplayIsRejected()
    {
        var options = new DistributionOptions { Enabled = true, Role = "Node", SharedSecret = "test-secret" };
        var auth = new DistributionAuthentication(new StaticOptionsMonitor<DistributionOptions>(options));
        var payload = new DistributionCommandRequest("deployment");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var signed = new HttpRequestMessage(HttpMethod.Post, "http://node/");
        DistributionAuthentication.Sign(signed, "commit", options.SharedSecret, bytes);
        var context = CreateContext(bytes, signed);

        var (value, error) = await auth.ReadAndVerifyAsync<DistributionCommandRequest>(context, "commit", default);
        Assert.NotNull(value);
        Assert.Null(error);

        var replay = CreateContext(bytes, signed);
        var (_, replayError) = await auth.ReadAndVerifyAsync<DistributionCommandRequest>(replay, "commit", default);
        Assert.Equal("nonce 已使用", replayError);
    }

    private static DefaultHttpContext CreateContext(byte[] body, HttpRequestMessage signed)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        foreach (var header in signed.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
        return context;
    }
}

public sealed class DistributionNodeServiceTests
{
    [Fact]
    public async Task PrepareCommitAndRollbackPreserveTwoPhaseBoundary()
    {
        var oldPath = SharedData.ConfigFilePath;
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, "Feature:\n  Value: old\n");
        SharedData.ConfigFilePath = configPath;
        try
        {
            var root = new ConfigurationBuilder().AddYamlFile(configPath, optional: false, reloadOnChange: false).Build();
            var versions = new ConfigVersionService(new HttpContextAccessor());
            var service = new DistributionNodeService(
                new StaticOptionsMonitor<DistributionOptions>(new()
                {
                    Enabled = true, Role = "Node", NodeId = "node-a",
                    StateDirectory = Path.Combine(directory, "state")
                }),
                versions, new ConfigurationLifecycleService(), root);
            var content = "Feature:\n  Value: new\n";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

            var prepared = await service.PrepareAsync(new("deploy-1", "source-1", hash, content), default);
            Assert.True(prepared.Ack);
            Assert.Contains("old", await File.ReadAllTextAsync(configPath));

            var committed = await service.CommitAsync("deploy-1", default);
            Assert.True(committed.Ack);
            Assert.Contains("new", await File.ReadAllTextAsync(configPath));
            var repeatedCommit = await service.CommitAsync("deploy-1", default);
            Assert.Equal(committed.ConfigVersionId, repeatedCommit.ConfigVersionId);

            var rolledBack = await service.RollbackAsync("deploy-1", default);
            Assert.True(rolledBack.Ack);
            Assert.Contains("old", await File.ReadAllTextAsync(configPath));
            Assert.True((await service.RollbackAsync("deploy-1", default)).Ack);
        }
        finally
        {
            SharedData.ConfigFilePath = oldPath;
            Directory.Delete(directory, true);
        }
    }
}

public sealed class DistributionNodeClientTests
{
    [Fact]
    public async Task RejectsNonSuccessfulHttpResponse()
    {
        var options = new DistributionOptions
        {
            Enabled = true,
            Role = "Controller",
            SharedSecret = "test-secret",
            Nodes = { ["node-a"] = new() { Endpoint = "http://node-a" } }
        };
        var handler = new StubHttpHandler(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = JsonContent.Create(new NodeAcknowledgement(false, "node-a", "deployment", "prepare"))
        });
        var client = new DistributionNodeClient(new StaticOptionsMonitor<DistributionOptions>(options),
            new StubHttpClientFactory(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PrepareAsync("node-a",
            new PrepareConfigurationRequest("deployment", "version", "hash", "content"), default));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}

public sealed class DistributionControllerServiceTests
{
    [Fact]
    public async Task CanaryDeploymentPausesAndResumesByBatch()
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            var created = await controller.CreateAsync(new(versionId, CanaryCount: 1, BatchSize: 1, PauseAfterEachBatch: true), default);
            var first = await WaitForAsync(controller, created.Id, x => x.State == "Paused" && x.NextIndex == 1);
            Assert.Equal("Ack", first.Targets["node-1"].State);

            await controller.ResumeAsync(created.Id, default);
            await WaitForAsync(controller, created.Id, x => x.State == "Paused" && x.NextIndex == 2);
            await controller.ResumeAsync(created.Id, default);
            var completed = await WaitForAsync(controller, created.Id, x => x.State == "Completed");

            Assert.Equal(3, completed.Targets.Values.Count(x => x.State == "Ack"));
            Assert.Equal(3, client.Prepared.Count);
            Assert.Equal(3, client.Committed.Count);
        });
    }

    [Fact]
    public async Task PrepareNackStopsBatchBeforeAnyCommit()
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            client.PrepareNackNode = "node-2";
            var created = await controller.CreateAsync(new(versionId, CanaryCount: 2, BatchSize: 2, PauseAfterEachBatch: false), default);
            var failed = await WaitForAsync(controller, created.Id, x => x.State == "Failed");
            Assert.Equal("Nack", failed.Targets["node-2"].State);
            Assert.Empty(client.Committed);
        });
    }

    [Fact]
    public async Task CommitNackRollsBackAlreadyCommittedNodes()
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            client.CommitNackNode = "node-2";
            var created = await controller.CreateAsync(new(versionId, NodeIds: ["node-1", "node-2"],
                CanaryCount: 2, BatchSize: 2, PauseAfterEachBatch: false), default);
            await WaitForAsync(controller, created.Id, x => x.State == "Failed");
            Assert.Contains("node-1", client.RolledBack);
        });
    }

    private static async Task WithControllerAsync(Func<DistributionControllerService, FakeNodeClient, string, Task> test)
    {
        var oldPath = SharedData.ConfigFilePath;
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-controller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, "Feature:\n  Value: old\n");
        SharedData.ConfigFilePath = configPath;
        try
        {
            var versions = new ConfigVersionService(new HttpContextAccessor());
            var version = await versions.WriteMainAsync(configPath, "Feature:\n  Value: distributed\n", "test");
            var opts = new DistributionOptions
            {
                Enabled = true, Role = "Controller", StateDirectory = Path.Combine(directory, "state"),
                Nodes = new()
                {
                    ["node-1"] = new() { Endpoint = "http://node-1" },
                    ["node-2"] = new() { Endpoint = "http://node-2" },
                    ["node-3"] = new() { Endpoint = "http://node-3" }
                }
            };
            var client = new FakeNodeClient();
            var controller = new DistributionControllerService(
                new StaticOptionsMonitor<DistributionOptions>(opts), versions, client);
            await test(controller, client, version.Id);
        }
        finally
        {
            SharedData.ConfigFilePath = oldPath;
            Directory.Delete(directory, true);
        }
    }

    private static async Task<ConfigurationDeployment> WaitForAsync(
        DistributionControllerService controller, string id, Func<ConfigurationDeployment, bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            var deployment = Assert.Single(await controller.ListAsync(default), x => x.Id == id);
            if (condition(deployment)) return deployment;
            await Task.Delay(20);
        }
        throw new TimeoutException("分发状态未按预期变化");
    }

    private sealed class FakeNodeClient : IDistributionNodeClient
    {
        public List<string> Prepared { get; } = [];
        public List<string> Committed { get; } = [];
        public List<string> RolledBack { get; } = [];
        public string? PrepareNackNode { get; set; }
        public string? CommitNackNode { get; set; }

        public Task<NodeAcknowledgement> PrepareAsync(string nodeId, PrepareConfigurationRequest request, CancellationToken cancellationToken)
        {
            Prepared.Add(nodeId);
            return Task.FromResult(new NodeAcknowledgement(nodeId != PrepareNackNode, nodeId, request.DeploymentId,
                "prepare", Error: nodeId == PrepareNackNode ? "rejected" : null));
        }

        public Task<NodeAcknowledgement> CommandAsync(string nodeId, string operation, DistributionCommandRequest request, CancellationToken cancellationToken)
        {
            if (operation == "commit") Committed.Add(nodeId);
            if (operation == "rollback") RolledBack.Add(nodeId);
            var ack = operation != "commit" || nodeId != CommitNackNode;
            return Task.FromResult(new NodeAcknowledgement(ack, nodeId, request.DeploymentId, operation,
                ConfigVersionId: ack ? "local-version" : null, Error: ack ? null : "commit failed"));
        }

        public Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(new DistributionNodeStatus(nodeId, "Node", "1.0", null, null, "Idle", DateTimeOffset.UtcNow));

        public Task<NodeDashboardSnapshot> GetDashboardAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(new NodeDashboardSnapshot(nodeId, "1.0", null, "Idle", DateTimeOffset.UtcNow,
                10, 0, new NodeSecuritySnapshot(0, 0, 0, 0, 0, 0, 0, 0), [], []));
    }
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
