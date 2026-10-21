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

public sealed class DistributionAuthenticationTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "lywaf-distribution-auth-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_stateDirectory)) Directory.Delete(_stateDirectory, true); }
    [Fact]
    public async Task ChunkedRequestStopsReadingAtConfiguredLimit()
    {
        var options = new DistributionOptions { Enabled = true, Role = "Node", SharedSecret = "test-secret", MaxConfigBytes = 1024 };
        var auth = new DistributionAuthentication(new StaticOptionsMonitor<DistributionOptions>(options));
        var bytes = new byte[100000];
        using var signed = new HttpRequestMessage(HttpMethod.Post, "http://node/");
        DistributionAuthentication.Sign(signed, "prepare", options.SharedSecret, bytes);
        var context = CreateContext(bytes, signed);
        context.Request.ContentLength = null;
        var (_, error) = await auth.ReadAndVerifyAsync<DistributionCommandRequest>(context, "prepare", default);
        Assert.Equal("请求体超过节点限制", error);
        Assert.True(context.Request.Body.Position <= 16384);
    }

    [Fact]
    public async Task SignedRequestIsAcceptedOnceAndReplayIsRejected()
    {
        var options = new DistributionOptions { Enabled = true, Role = "Node", SharedSecret = "test-secret", StateDirectory = _stateDirectory };
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
        var restarted = new DistributionAuthentication(new StaticOptionsMonitor<DistributionOptions>(options));
        var (_, replayError) = await restarted.ReadAndVerifyAsync<DistributionCommandRequest>(replay, "commit", default);
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
        await File.WriteAllTextAsync(configPath, "SpeedLimit:\n  Value: old\n");
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
            var content = "SpeedLimit:\n  Value: new\n";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

            var prepared = await service.PrepareAsync(new("deploy-1", "source-1", hash, content), default);
            Assert.True(prepared.Ack);
            Assert.NotNull(await service.ReconcileCommandAsync("configuration.prepare", "deploy-1", new("deploy-1", "source-1", hash, content), default));
            Assert.Null(await service.ReconcileCommandAsync("configuration.prepare", "deploy-1", new("deploy-1", "source-1", hash, "other"), default));
            Assert.Null(await service.ReconcileCommandAsync("configuration.commit", "deploy-1", null, default));
            Assert.Contains("old", await File.ReadAllTextAsync(configPath));

            Assert.True((await service.PrepareAsync(new("deploy-1", "source-1", hash, content), default)).Ack);
            Assert.False((await service.PrepareAsync(new("deploy-1", "source-1", hash, content + "# changed"), default)).Ack);
            var other = "SpeedLimit:\n  Value: other\n";
            var otherHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(other)));
            Assert.False((await service.PrepareAsync(new("deploy-1", "source-1", otherHash, other), default)).Ack);
            Assert.False((await service.PrepareAsync(new("deploy-other", "source-2", otherHash, other), default)).Ack);

            var statePath = Path.Combine(directory, "state", "node-state.json");
            var preparedState = await File.ReadAllTextAsync(statePath);
            var committed = await service.CommitAsync("deploy-1", default);
            Assert.True(committed.Ack);
            // Simulate a crash after version/config persistence but before the Applied state rename.
            await File.WriteAllTextAsync(statePath, preparedState);
            service = new DistributionNodeService(new StaticOptionsMonitor<DistributionOptions>(new()
            {
                Enabled = true, Role = "Node", NodeId = "node-a", StateDirectory = Path.Combine(directory, "state")
            }), versions, new ConfigurationLifecycleService(), root);
            Assert.Equal("Prepared", service.GetStatus().State);
            await File.WriteAllTextAsync(configPath, "SpeedLimit:\n  Value: locally-edited\n");
            Assert.Null(await service.ReconcileCommandAsync("configuration.commit", "deploy-1", null, default));
            Assert.Equal("Prepared", service.GetStatus().State);
            Assert.Contains("locally-edited", await File.ReadAllTextAsync(configPath));
            await File.WriteAllTextAsync(configPath, content);
            var recovered = await service.ReconcileCommandAsync("configuration.commit", "deploy-1", null, default);
            Assert.Equal(committed.ConfigVersionId, recovered?.ConfigVersionId);
            Assert.Equal("Applied", service.GetStatus().State);
            Assert.NotNull(await service.ReconcileCommandAsync("configuration.commit", "deploy-1", null, default));
            Assert.Null(await service.ReconcileCommandAsync("configuration.commit", "another-task", null, default));
            Assert.Contains("new", await File.ReadAllTextAsync(configPath));
            var repeatedCommit = await service.CommitAsync("deploy-1", default);
            Assert.Equal(committed.ConfigVersionId, repeatedCommit.ConfigVersionId);

            var rolledBack = await service.RollbackAsync("deploy-1", default);
            Assert.True(rolledBack.Ack);
            Assert.NotNull(await service.ReconcileCommandAsync("configuration.rollback", "deploy-1", null, default));
            Assert.Null(await service.ReconcileCommandAsync("configuration.commit", "deploy-1", null, default));
            Assert.Contains("old", await File.ReadAllTextAsync(configPath));
            Assert.True((await service.RollbackAsync("deploy-1", default)).Ack);
            Assert.False((await service.CommitAsync("deploy-1", default)).Ack);
            Assert.False((await service.PrepareAsync(new("deploy-1", "source-1", hash, content), default)).Ack);

            Assert.True((await service.PrepareAsync(new("deploy-2", "source-2", hash, content), default)).Ack);
            await File.WriteAllTextAsync(configPath, "SpeedLimit:\n  Value: edited-locally\n");
            var conflict = await service.CommitAsync("deploy-2", default);
            Assert.False(conflict.Ack);
            Assert.Contains("基准", conflict.Error);
            Assert.Contains("edited-locally", await File.ReadAllTextAsync(configPath));
            Assert.True((await service.RollbackAsync("deploy-2", default)).Ack);
            Assert.Contains("edited-locally", await File.ReadAllTextAsync(configPath));
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
    [Theory]
    [InlineData("Running", "Paused")]
    [InlineData("Pausing", "Paused")]
    [InlineData("RollingBack", "RollbackFailed")]
    [InlineData("Completed", "Completed")]
    public async Task RestartReadsLegacyBomAndPreservesUncertainTargets(string storedState, string expectedState)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var deployment = new ConfigurationDeployment("deployment", "version", "hash", DateTimeOffset.UtcNow,
                storedState, 1, 1, false, 0, ["node-1"],
                new Dictionary<string, DistributionTargetState> { ["node-1"] = new("node-1", "Unknown", DateTimeOffset.UtcNow) });
            await File.WriteAllTextAsync(Path.Combine(directory, "deployment-deployment.json"),
                JsonSerializer.Serialize(deployment, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8);
            var controller = new DistributionControllerService(new StaticOptionsMonitor<DistributionOptions>(new()
                { Enabled = true, Role = "Controller", StateDirectory = directory }),
                new ConfigVersionService(new HttpContextAccessor()), new FakeNodeClient());
            var recovered = Assert.Single(await controller.ListAsync(default));
            Assert.Equal(expectedState, recovered.State);
            Assert.Equal("Unknown", recovered.Targets["node-1"].State);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostCommitResponseIsReconciledOrPausedWithoutRollback(bool canConfirm)
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            client.CommitResponseLost = true;
            client.ConfirmApplied = canConfirm;
            var created = await controller.CreateAsync(new(versionId, NodeIds: ["node-1"],
                CanaryCount: 1, PauseAfterEachBatch: false), default);
            var result = await WaitForAsync(controller, created.Id, state => state.State == (canConfirm ? "Completed" : "Paused"));
            Assert.Equal(canConfirm ? "Ack" : "Unknown", result.Targets["node-1"].State);
            Assert.Empty(client.RolledBack);
            Assert.Single(client.Committed);
        });
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeDoesNotResendConfirmedNodesInPartiallyCommittedBatch(bool rejectPrepare)
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            client.CommitResponseLostNode = "node-2";
            var created = await controller.CreateAsync(new(versionId, NodeIds: ["node-1", "node-2"],
                CanaryCount: 2, PauseAfterEachBatch: false), default);
            var paused = await WaitForAsync(controller, created.Id, state => state.State == "Paused");
            Assert.Equal("Ack", paused.Targets["node-1"].State);
            Assert.Equal("Unknown", paused.Targets["node-2"].State);

            client.CommitResponseLostNode = null;
            client.PrepareNackNode = rejectPrepare ? "node-2" : null;
            await controller.ResumeAsync(created.Id, default);
            var completed = await WaitForAsync(controller, created.Id, state => state.State == (rejectPrepare ? "Failed" : "Completed"));
            Assert.Equal(new[] { "node-1", "node-2", "node-2" }, client.Prepared);
            if (rejectPrepare)
            {
                Assert.Equal(new[] { "node-1", "node-2" }, client.Committed);
                Assert.Equal(new[] { "node-1" }, client.RolledBack);
                Assert.Equal("RolledBack", completed.Targets["node-1"].State);
                Assert.Equal("Nack", completed.Targets["node-2"].State);
            }
            else
            {
                Assert.All(completed.Targets.Values, target => Assert.Equal("Ack", target.State));
                Assert.Equal(new[] { "node-1", "node-2", "node-2" }, client.Committed);
                Assert.Empty(client.RolledBack);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRollbackIncludesUnknownAndPreparedNodes(bool failFirstRollback)
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            client.CommitResponseLost = true;
            var created = await controller.CreateAsync(new(versionId, NodeIds: ["node-1", "node-2"],
                CanaryCount: 2, PauseAfterEachBatch: false), default);
            var paused = await WaitForAsync(controller, created.Id, state => state.State == "Paused");
            Assert.Equal("Unknown", paused.Targets["node-1"].State);
            Assert.Equal("Prepared", paused.Targets["node-2"].State);

            client.RollbackThrowNode = failFirstRollback ? "node-1" : null;
            var rolledBack = await controller.RollbackAsync(created.Id, default);
            Assert.Equal(failFirstRollback ? "RollbackFailed" : "RolledBack", rolledBack.State);
            Assert.Equal(new[] { "node-1", "node-2" }, client.RolledBack);
            Assert.Equal("RolledBack", rolledBack.Targets["node-2"].State);
            if (failFirstRollback)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ResumeAsync(created.Id, default));
                client.RollbackThrowNode = null;
                rolledBack = await controller.RollbackAsync(created.Id, default);
                Assert.Equal("RolledBack", rolledBack.State);
                Assert.Equal(new[] { "node-1", "node-2", "node-1" }, client.RolledBack);
            }
            Assert.All(rolledBack.Targets.Values, target => Assert.Equal("RolledBack", target.State));
            Assert.Single(client.Committed);
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
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ResumeAsync(created.Id, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.PauseAsync(created.Id, default));
            Assert.Equal("Failed", Assert.Single(await controller.ListAsync(default)).State);
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

    [Fact]
    public async Task ProductionQueueExposesUnifiedTaskId()
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            var created = await controller.CreateAsync(new(versionId, NodeIds: ["node-1"], PauseAfterEachBatch: false), default);
            var completed = await WaitForAsync(controller, created.Id, x => x.State == "Completed");
            Assert.NotNull(completed.TaskId);
            Assert.Contains("node-1", client.Committed);
        }, true);
    }

    [Fact]
    public async Task EveryDispatchHasAnUnknownTargetWithPendingStage()
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            var stages = new List<string>();
            client.BeforeOperation = async stage =>
            {
                var deployment = Assert.Single(await controller.ListAsync(default));
                var target = deployment.Targets["node-1"];
                Assert.Equal("Unknown", target.State);
                Assert.Equal(stage, target.PendingStage);
                stages.Add(stage);
            };
            var created = await controller.CreateAsync(new(versionId, NodeIds: ["node-1"], PauseAfterEachBatch: false), default);
            await WaitForAsync(controller, created.Id, state => state.State == "Completed");
            await controller.RollbackAsync(created.Id, default);
            Assert.Equal(new[] { "prepare", "commit", "rollback" }, stages);
        });
    }

    [Fact]
    public async Task ConcurrentRollbackAndResumeCannotOverlapManualRollback()
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            var created = await controller.CreateAsync(new(versionId, NodeIds: ["node-1"], PauseAfterEachBatch: false), default);
            await WaitForAsync(controller, created.Id, state => state.State == "Completed");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.BeforeRollback = async () => { started.SetResult(); await release.Task; };
            var first = controller.RollbackAsync(created.Id, default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RollbackAsync(created.Id, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ResumeAsync(created.Id, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => controller.PauseAsync(created.Id, default));
            }
            finally { release.SetResult(); }
            Assert.Equal("RolledBack", (await first).State);
            Assert.Single(client.RolledBack);
        });
    }

    [Fact]
    public async Task RollbackFailureStillAttemptsOtherCommittedNodes()
    {
        await WithControllerAsync(async (controller, client, versionId) =>
        {
            client.CommitNackNode = "node-3";
            client.RollbackThrowNode = "node-1";
            var created = await controller.CreateAsync(new(versionId, CanaryCount: 3, BatchSize: 3, PauseAfterEachBatch: false), default);
            var failed = await WaitForAsync(controller, created.Id, item => item.State == "RollbackFailed");
            Assert.Contains("node-2", client.RolledBack);
            Assert.Equal("Unknown", failed.Targets["node-1"].State);
            Assert.Equal("rollback", failed.Targets["node-1"].PendingStage);
            Assert.Equal("RolledBack", failed.Targets["node-2"].State);
        });
    }

    private static async Task WithControllerAsync(Func<DistributionControllerService, FakeNodeClient, string, Task> test, bool useJobs = false)
    {
        var oldPath = SharedData.ConfigFilePath;
        var directory = Path.Combine(Path.GetTempPath(), "lywaf-controller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, "SpeedLimit:\n  Value: old\n");
        SharedData.ConfigFilePath = configPath;
        using var jobs = new LyWaf.Services.ManagementTasks.ManagementJobService();
        if (useJobs) await jobs.StartAsync(default);
        try
        {
            var versions = new ConfigVersionService(new HttpContextAccessor());
            var version = await versions.WriteMainAsync(configPath, "SpeedLimit:\n  Value: distributed\n", "test");
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
                new StaticOptionsMonitor<DistributionOptions>(opts), versions, client, useJobs ? jobs : null);
            await test(controller, client, version.Id);
        }
        finally
        {
            if (useJobs) await jobs.StopAsync(default);
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
        public string? RollbackThrowNode { get; set; }
        public bool CommitResponseLost { get; set; }
        public string? CommitResponseLostNode { get; set; }
        public bool ConfirmApplied { get; set; }
        public Func<Task>? BeforeRollback { get; set; }
        public Func<string, Task>? BeforeOperation { get; set; }
        private string? _lastDeployment;

        public async Task<NodeAcknowledgement> PrepareAsync(string nodeId, PrepareConfigurationRequest request, CancellationToken cancellationToken)
        {
            if (BeforeOperation is not null) await BeforeOperation("prepare");
            Prepared.Add(nodeId);
            return new NodeAcknowledgement(nodeId != PrepareNackNode, nodeId, request.DeploymentId,
                "prepare", Error: nodeId == PrepareNackNode ? "rejected" : null);
        }

        public async Task<NodeAcknowledgement> CommandAsync(string nodeId, string operation, DistributionCommandRequest request, CancellationToken cancellationToken)
        {
            if (BeforeOperation is not null) await BeforeOperation(operation);
            if (operation == "rollback" && BeforeRollback is not null) await BeforeRollback();
            if (operation == "commit") Committed.Add(nodeId);
            if (operation == "commit" && (CommitResponseLost || nodeId == CommitResponseLostNode))
            {
                _lastDeployment = request.DeploymentId;
                throw new HttpRequestException("response lost after applying");
            }
            if (operation == "rollback") RolledBack.Add(nodeId);
            if (operation == "rollback" && nodeId == RollbackThrowNode) throw new HttpRequestException("unreachable node");
            var ack = operation != "commit" || nodeId != CommitNackNode;
            return new NodeAcknowledgement(ack, nodeId, request.DeploymentId, operation,
                ConfigVersionId: ack ? "local-version" : null, Error: ack ? null : "commit failed");
        }

        public Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(new DistributionNodeStatus(nodeId, "Node", "1.0", ConfirmApplied ? _lastDeployment : null,
                ConfirmApplied ? "local-version" : null, ConfirmApplied ? "Applied" : "Idle", DateTimeOffset.UtcNow));

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
