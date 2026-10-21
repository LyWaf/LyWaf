using System.Text.Json;
using LyWaf.Services.Configuration;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LyWaf.Tests;

public sealed class DistributionOutboxConfirmationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "confirmation-" + Guid.NewGuid().ToString("N"));
    private readonly Client client = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DistributionControllerService Controller() => new(
        new StaticOptionsMonitor<DistributionOptions>(new() { Enabled = true, Role = "Controller", StateDirectory = directory }),
        new ConfigVersionService(new HttpContextAccessor()), client,
        communication: new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        { Enabled = true, Role = "Controller", ClusterId = "cluster", NodeId = "controller" }));

    private async Task Seed(string stage = "commit", string state = "Unknown", string deploymentState = "Paused")
    {
        Directory.CreateDirectory(directory);
        var deployment = new ConfigurationDeployment("deployment", "version", "hash", DateTimeOffset.UtcNow,
            deploymentState, 1, 1, true, 0, ["node", "next"], new Dictionary<string, DistributionTargetState>
            {
                ["node"] = new("node", state, DateTimeOffset.UtcNow, Error: "lost", PendingStage: stage),
                ["next"] = new("next", "Pending", DateTimeOffset.UtcNow)
            });
        await File.WriteAllTextAsync(Path.Combine(directory, "deployment-deployment.json"), JsonSerializer.Serialize(deployment, Json));
        var now = DateTimeOffset.UtcNow;
        client.Record = new(new NodeMessage(2, "cluster", "controller", "node", "message", "deployment",
            "configuration." + stage, now, now.AddMinutes(1), JsonSerializer.SerializeToElement(new { deploymentId = "deployment" })),
            "https://node", 1, "Completed", new(true, "node", "deployment", stage, "local"));
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("commit")]
    [InlineData("rollback")]
    public async Task ConfirmedRejectionSurvivesRestartWithoutAdvancingDeployment(string stage)
    {
        await Seed(stage);
        client.Record = client.Record! with
        {
            State = "Rejected",
            Result = new(false, "node", "deployment", stage, Error: "remote-private-text",
                ErrorCode: NodeConfigurationErrors.Conflict, MessageKey: "untrusted-key")
        };
        Assert.Equal(1, await Controller().RecoverCompletedOutboxAsync(default));
        var result = Assert.Single(await Controller().ListAsync(default));
        Assert.Equal(stage == "rollback" ? "RollbackNack" : "Nack", result.Targets["node"].State);
        Assert.Equal(NodeConfigurationErrors.Conflict, result.Targets["node"].Error);
        Assert.Null(result.Targets["node"].PendingStage);
        Assert.Equal("Paused", result.State);
        Assert.Equal(0, result.NextIndex);
        Assert.Equal(0, client.Commands);
        Assert.Equal(0, await Controller().RecoverCompletedOutboxAsync(default));
    }

    [Theory]
    [InlineData("prepare", "Prepared", "Paused")]
    [InlineData("commit", "Ack", "Paused")]
    [InlineData("rollback", "RolledBack", "RollbackFailed")]
    [InlineData("commit", "Ack", "Failed")]
    public async Task RecoversOnlyTargetAndPersistsWithoutProgress(string stage, string expected, string deploymentState)
    {
        await Seed(stage, deploymentState: deploymentState);
        var controller = Controller();
        Assert.Equal(1, await controller.RecoverCompletedOutboxAsync(default));
        Assert.Equal(0, await controller.RecoverCompletedOutboxAsync(default));
        var result = Assert.Single(await Controller().ListAsync(default));
        Assert.Equal(expected, result.Targets["node"].State);
        Assert.Null(result.Targets["node"].Error);
        Assert.Null(result.Targets["node"].PendingStage);
        Assert.Equal("local", result.Targets["node"].ConfigVersionId);
        Assert.Equal(deploymentState, result.State);
        Assert.Equal(0, result.NextIndex);
        Assert.Equal("Pending", result.Targets["next"].State);
        Assert.Equal(0, client.Commands);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("deployment")]
    [InlineData("stage")]
    [InlineData("payload")]
    [InlineData("cluster")]
    [InlineData("sender")]
    [InlineData("receiver")]
    [InlineData("correlation")]
    [InlineData("type")]
    [InlineData("nack")]
    [InlineData("pending")]
    [InlineData("unsent")]
    public async Task RejectsUnrelatedOrUnconfirmedRecords(string mismatch)
    {
        await Seed();
        var r = client.Record!;
        client.Record = mismatch switch
        {
            "node" => r with { Result = r.Result! with { NodeId = "NODE" } },
            "deployment" => r with { Result = r.Result! with { DeploymentId = "DEPLOYMENT" } },
            "stage" => r with { Result = r.Result! with { Stage = "prepare" } },
            "payload" => r with { Message = r.Message with { Payload = JsonSerializer.SerializeToElement(new { deploymentId = "other" }) } },
            "cluster" => r with { Message = r.Message with { ClusterId = "other" } },
            "sender" => r with { Message = r.Message with { SenderNodeId = "other" } },
            "receiver" => r with { Message = r.Message with { ReceiverNodeId = "other" } },
            "correlation" => r with { Message = r.Message with { CorrelationId = "other" } },
            "type" => r with { Message = r.Message with { Type = "configuration.prepare" } },
            "nack" => r with { Result = r.Result! with { Ack = false } },
            "pending" => r with { State = "Unknown" },
            _ => r with { Attempts = 0 }
        };
        Assert.Equal(0, await Controller().RecoverCompletedOutboxAsync(default));
        Assert.Equal("Unknown", Assert.Single(await Controller().ListAsync(default)).Targets["node"].State);
    }

    [Theory]
    [InlineData("Ack", "prepare")]
    [InlineData("RolledBack", "prepare")]
    [InlineData("Unknown", "commit")]
    [InlineData("Unknown", null)]
    public async Task OldPrepareCannotOverwriteLaterOrUnspecifiedStage(string state, string? pendingStage)
    {
        await Seed("prepare", state);
        var path = Path.Combine(directory, "deployment-deployment.json");
        var deployment = JsonSerializer.Deserialize<ConfigurationDeployment>(await File.ReadAllTextAsync(path), Json)!;
        var targets = deployment.Targets.ToDictionary(x => x.Key, x => x.Value);
        targets["node"] = targets["node"] with { PendingStage = pendingStage };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(deployment with { Targets = targets }, Json));
        Assert.Equal(0, await Controller().RecoverCompletedOutboxAsync(default));
    }

    [Fact]
    public async Task SkipsActiveRollbackWithoutOverwritingItsSnapshot()
    {
        await Seed();
        var controller = Controller();
        client.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var rollback = controller.RollbackAsync("deployment", default);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.Equal(0, await controller.RecoverCompletedOutboxAsync(default)); }
        finally { client.Block.SetResult(); }
        await rollback;
        Assert.Equal("RolledBack", Assert.Single(await controller.ListAsync(default)).Targets["node"].State);
    }

    [Fact]
    public void BackgroundIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddLyWafDistribution(new ConfigurationBuilder().Build());
        Assert.Contains(services, x => x.ServiceType == typeof(IHostedService)
            && x.ImplementationType == typeof(DistributionOutboxConfirmationService));
    }

    [Fact]
    public async Task ConcurrentRecoveryIsIdempotent()
    {
        await Seed();
        var controller = Controller();
        var counts = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => controller.RecoverCompletedOutboxAsync(default)));
        Assert.Equal(1, counts.Sum());
        Assert.Equal("Ack", Assert.Single(await Controller().ListAsync(default)).Targets["node"].State);
    }

    [Fact]
    public async Task BackgroundRecoversWithoutPageRequests()
    {
        await Seed();
        using var worker = new DistributionOutboxConfirmationService(Controller(), NullLogger<DistributionOutboxConfirmationService>.Instance);
        await worker.StartAsync(default);
        try
        {
            for (var i = 0; i < 100; i++)
            {
                if (Assert.Single(await Controller().ListAsync(default)).Targets["node"].State == "Ack") return;
                await Task.Delay(20);
            }
            Assert.Fail("Background did not project completed acknowledgement");
        }
        finally { await worker.StopAsync(default); }
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private sealed class Client : IDistributionNodeClient
    {
        public NodeOutboxRecord? Record;
        public int Commands;
        public TaskCompletionSource? Block;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<NodeOutboxRecord> ReadOutbox() => Record is null ? [] : [Record];
        public Task<NodeAcknowledgement> PrepareAsync(string nodeId, PrepareConfigurationRequest request, CancellationToken token) => throw new InvalidOperationException();
        public async Task<NodeAcknowledgement> CommandAsync(string nodeId, string operation, DistributionCommandRequest request, CancellationToken token)
        {
            Commands++;
            Entered.TrySetResult();
            if (Block is not null) await Block.Task.WaitAsync(token);
            return new(true, nodeId, request.DeploymentId, operation);
        }
        public Task<DistributionNodeStatus> GetStatusAsync(string nodeId, CancellationToken token) => throw new InvalidOperationException();
        public Task<NodeDashboardSnapshot> GetDashboardAsync(string nodeId, CancellationToken token) => throw new InvalidOperationException();
    }
}
