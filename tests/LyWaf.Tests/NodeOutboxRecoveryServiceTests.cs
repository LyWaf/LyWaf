using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeOutboxRecoveryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "outbox-worker-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private NodeCommunicationOptions Config => new()
    {
        Enabled = true, Role = "Controller", ClusterId = "cluster", NodeId = "control", StateDirectory = _directory,
        Peers = new() { ["edge"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('k', 32) } } }
    };
    private DistributionNodeClient Client(Transport transport, params string[] additionalPeers)
    {
        var protocol = Config;
        var distribution = new DistributionOptions
        {
            Nodes = new() { ["edge"] = new() { Endpoint = "https://edge", ProtocolVersion = 2 } }
        };
        foreach (var peer in additionalPeers)
        {
            distribution.Nodes[peer] = new() { Endpoint = "https://" + peer, ProtocolVersion = 2 };
            protocol.Peers[peer] = protocol.Peers["edge"];
        }
        return new(new StaticOptionsMonitor<DistributionOptions>(distribution), transport,
            new StaticOptionsMonitor<NodeCommunicationOptions>(protocol));
    }
    private NodeOutboxRecoveryService Worker(IDistributionNodeClient client, TestLogger? logger = null) =>
        new(client, new StaticOptionsMonitor<NodeCommunicationOptions>(Config), logger ?? new());

    [Fact]
    public void DistributionRegistrationIncludesRecoveryHostedService()
    {
        var services = new ServiceCollection();
        services.AddLyWafDistribution(new ConfigurationBuilder().Build());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(NodeOutboxRecoveryService));
    }

    [Fact]
    public async Task StartupAutomaticallyQueriesPersistedCommandAndCompletesWithoutCallerRecovery()
    {
        var record = Record();
        var path = Seed(record);
        using var transport = new Transport((message, _) => Task.FromResult(Ack(message)));
        using var worker = Worker(Client(transport));
        await worker.StartAsync(default);
        await Until(() => Read(path).State == "Completed");
        await worker.StopAsync(default);
        var query = Assert.Single(transport.Messages);
        Assert.Equal("configuration.result", query.Type);
        Assert.Equal(record.Message, query.Payload.Deserialize<NodeMessage>(Json)! with { Payload = record.Message.Payload });
        var saved = Read(path);
        Assert.Equal(2, saved.Attempts);
        Assert.Equal(record.Message.ExpiresAt, saved.Message.ExpiresAt);
        using var restarted = Worker(Client(transport));
        await restarted.RecoverPendingAsync(default);
        Assert.Single(transport.Messages);
    }

    [Fact]
    public async Task ShutdownCancelsInFlightAndDoesNotClaimAnotherRecord()
    {
        var first = Seed(Record());
        var second = Seed(Record());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new Transport(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException();
        });
        using var worker = Worker(Client(transport));
        await worker.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Task.IsCompleted);
        Assert.Single(transport.Messages);
        Assert.Equal(3, Read(first).Attempts + Read(second).Attempts);
        Assert.Equal("Unknown", Read(first).State);
        Assert.Equal("Unknown", Read(second).State);
        Assert.Null(Read(first).Result);
        Assert.Null(Read(second).Result);
    }

    [Fact]
    public async Task BackgroundFailuresExhaustOriginalBudgetAndSubsequentScansStaySilent()
    {
        var path = Seed(Record());
        using var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var logger = new TestLogger();
        using var worker = Worker(Client(transport), logger);
        await worker.StartAsync(default);
        await Until(() => Read(path).Attempts == 3);
        await worker.StopAsync(default);
        var warnings = logger.Warnings;
        using var restarted = Worker(Client(transport), logger);
        for (var i = 0; i < 5; i++) await restarted.RecoverPendingAsync(default);
        Assert.Equal(2, transport.Messages.Count);
        Assert.All(transport.Messages, message => Assert.Equal("configuration.result", message.Type));
        Assert.Equal(warnings, logger.Warnings);
    }

    [Fact]
    public async Task SlowPeerDoesNotBlockFastPeerAndSamePeerRemainsSerial()
    {
        var slow1 = Record();
        var slow2 = Record();
        var fast = Record() with { Message = Record().Message with { ReceiverNodeId = "fast" } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCalls = 0;
        var client = new StubClient { Records = [slow1, slow2, fast], Recover = async (id, token) =>
        {
            if (id == fast.Message.MessageId) { fastFinished.TrySetResult(); return new(true, "fast", "deployment", "commit"); }
            if (Interlocked.Increment(ref slowCalls) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return new(true, "edge", "deployment", "commit");
        } };
        using var worker = Worker(client);
        var scan = worker.RecoverPendingAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fastFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref slowCalls));
            Assert.False(scan.IsCompleted);
        }
        finally { release.TrySetResult(); await scan; }
        Assert.Equal(2, slowCalls);
        Assert.All(client.Records, record => Assert.Equal(1, record.Attempts));
    }

    [Fact]
    public async Task PeerConcurrencyIsBoundedAndShutdownCancelsAllWorkersWithoutClaimingMore()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cancelled = 0;
        var records = Enumerable.Range(0, 12).Select(i => Record() with
        { Message = Record().Message with { ReceiverNodeId = "peer-" + i } }).ToArray();
        var client = new StubClient { Records = records, Recover = async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == NodeOutboxRecoveryService.MaximumConcurrentPeers) entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            throw new InvalidOperationException();
        } };
        using var worker = Worker(client);
        await worker.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NodeOutboxRecoveryService.MaximumConcurrentPeers, Volatile.Read(ref calls));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NodeOutboxRecoveryService.MaximumConcurrentPeers, calls);
        Assert.Equal(calls, cancelled);
    }

    [Fact]
    public async Task LaterScanCanUseFreeSlotWhileEarlierPeerIsStillBlocked()
    {
        var slow = Record();
        var fast = Record();
        fast = fast with { Message = fast.Message with { ReceiverNodeId = "fast" } };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCalls = 0;
        var client = new StubClient { Records = [slow], Recover = async (id, token) =>
        {
            if (id == slow.Message.MessageId)
            {
                Interlocked.Increment(ref slowCalls);
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            finished.TrySetResult();
            return new(true, "fast", "deployment", "commit");
        } };
        using var worker = Worker(client);
        await worker.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.Records = [slow, fast];
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref slowCalls));
        }
        finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ActualParallelRecoveryPersistsBudgetsAndOnlyQueriesBeforeShutdown()
    {
        var slow = Record();
        var fast = Record();
        fast = fast with { Message = fast.Message with { ReceiverNodeId = "fast" }, Endpoint = "https://fast" };
        var slowPath = Seed(slow);
        var fastPath = Seed(fast);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new Transport(async (message, token) =>
        {
            if (message.ReceiverNodeId == "edge")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Ack(message);
        });
        using var worker = Worker(Client(transport, "fast"));
        await worker.StartAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => Read(fastPath).State == "Completed");
        }
        finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(2, transport.Messages.Count);
        Assert.All(transport.Messages, message => Assert.Equal("configuration.result", message.Type));
        Assert.Equal(2, Read(slowPath).Attempts);
        Assert.Equal("Unknown", Read(slowPath).State);
        Assert.Equal(slow.Message.ExpiresAt, Read(slowPath).Message.ExpiresAt);
        Assert.Equal(2, Read(fastPath).Attempts);
        Assert.Equal("Completed", Read(fastPath).State);
    }

    [Fact]
    public async Task IneligibleRecordsNeverReachRecoveryOrProduceLogs()
    {
        var basis = Record();
        var client = new StubClient
        {
            Records = [basis with { Attempts = 0, State = "Pending" }, basis with { Attempts = 3 },
                basis with { State = "Completed" }, basis with { State = "Rejected" }, basis with { State = "Stopped" },
                basis with { Message = basis.Message with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) } },
                basis with { NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(1) },
                basis with { Message = basis.Message with { SenderNodeId = "other" } },
                basis with { Message = basis.Message with { ClusterId = "other" } }]
        };
        var logger = new TestLogger();
        using var worker = Worker(client, logger);
        for (var i = 0; i < 5; i++) await worker.RecoverPendingAsync(default);
        Assert.Equal(0, client.Recoveries);
        Assert.Equal(0, logger.Warnings);
    }

    [Fact]
    public async Task UnavailableStoreWarningsAreRateLimitedAndShutdownDoesNotScan()
    {
        var client = new StubClient { ReadError = new IOException("locked") };
        var logger = new TestLogger();
        using var worker = Worker(client, logger);
        for (var i = 0; i < 5; i++) await worker.RecoverPendingAsync(default);
        Assert.Equal(1, logger.Warnings);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RecoverPendingAsync(cancelled.Token));
        Assert.Equal(5, client.Reads);
    }

    [Theory]
    [InlineData(false, "Controller")]
    [InlineData(true, "Node")]
    public async Task DisabledOrNodeRoleDoesNotScan(bool enabled, string role)
    {
        var config = Config;
        config.Enabled = enabled;
        config.Role = role;
        var client = new StubClient();
        using var worker = new NodeOutboxRecoveryService(client, new StaticOptionsMonitor<NodeCommunicationOptions>(config), new TestLogger());
        await worker.RecoverPendingAsync(default);
        Assert.Equal(0, client.Reads);
    }

    [Fact]
    public async Task RecoveryCannotRecreateDeletedRecordOrExecutePendingCommand()
    {
        var record = Record();
        Task<NodeAcknowledgement> Send(NodeMessage _, bool query, CancellationToken token) => throw new Exception("must not send");
        var store = new NodeCommandOutbox(_directory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteAsync(record.Message, record.Endpoint, Send, default, recoveryOnly: true));
        Assert.Empty(store.ReadRecords());
        Seed(record with { Attempts = 0, State = "Pending" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExecuteAsync(record.Message, record.Endpoint, Send, default, recoveryOnly: true));
        Assert.Equal(0, Assert.Single(store.ReadRecords()).Attempts);
    }

    private static NodeOutboxRecord Record()
    {
        var now = DateTimeOffset.UtcNow;
        return new(new(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "deployment", "configuration.commit",
            now, now.AddMinutes(5), JsonSerializer.SerializeToElement(new DistributionCommandRequest("deployment"), Json)),
            "https://edge", 1, "Unknown");
    }
    private string Seed(NodeOutboxRecord record)
    {
        var directory = Path.Combine(_directory, "outbox");
        Directory.CreateDirectory(directory);
        var m = record.Message;
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { m.ClusterId, m.SenderNodeId, m.ReceiverNodeId, m.MessageId }))) + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(record));
        return path;
    }
    private static NodeOutboxRecord Read(string path)
    {
        using var gate = new FileStream(Path.Combine(Path.GetDirectoryName(path)!, ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<NodeOutboxRecord>(stream)!;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            await Task.Delay(20, timeout.Token);
            try { if (condition()) return; }
            catch (IOException) { } // Observe through the same lock as production readers.
        }
    }
    private static HttpResponseMessage Ack(NodeMessage query)
    {
        var original = query.Payload.Deserialize<NodeMessage>(Json)!;
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new NodeAcknowledgement(true, original.ReceiverNodeId, original.CorrelationId, "commit")) };
    }
    private sealed class Transport(Func<NodeMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler, IHttpClientFactory
    {
        public ConcurrentQueue<NodeMessage> Messages { get; } = new();
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var message = (await request.Content!.ReadFromJsonAsync<NodeMessage>(Json, token))!;
            Messages.Enqueue(message);
            return await send(message, token);
        }
    }
    private sealed class StubClient : IDistributionNodeClient
    {
        public IReadOnlyList<NodeOutboxRecord> Records { get; set; } = [];
        public Exception? ReadError { get; init; }
        public int Reads { get; private set; }
        public int Recoveries { get; private set; }
        public Func<string, CancellationToken, Task<NodeAcknowledgement>>? Recover { get; init; }
        public IReadOnlyList<NodeOutboxRecord> ReadOutbox() { Reads++; if (ReadError is not null) throw ReadError; return Records; }
        public Task<NodeAcknowledgement> RecoverAsync(string id, CancellationToken token)
        { if (Recover is not null) return Recover(id, token); Recoveries++; throw new Exception("unexpected recovery"); }
        public Task<NodeAcknowledgement> PrepareAsync(string id, PrepareConfigurationRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<NodeAcknowledgement> CommandAsync(string id, string operation, DistributionCommandRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<DistributionNodeStatus> GetStatusAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<NodeDashboardSnapshot> GetDashboardAsync(string id, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class TestLogger : ILogger<NodeOutboxRecoveryService>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Warning) Warnings++; }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
