using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeOutboxIsolationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "outbox-isolation-" + Guid.NewGuid().ToString("N"));
    private string Outbox => Path.Combine(_directory, "outbox");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SlowPeerDoesNotBlockOtherPeerOrSnapshotAndSamePeerRecoveryCannotResetBudget()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new Transport(async (message, token) =>
        {
            if (message.ReceiverNodeId == "slow")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new NodeAcknowledgement(true,
                message.ReceiverNodeId, message.CorrelationId, "commit")) };
        });
        var first = Client(transport).CommandAsync("slow", "commit", new("deployment"), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = Assert.Single(Client(transport).ReadOutbox());
            Assert.Equal(1, snapshot.Attempts);
            Assert.True((await Client(transport).CommandAsync("fast", "commit", new("deployment"), default)
                .WaitAsync(TimeSpan.FromSeconds(5))).Ack);
            Assert.False(first.IsCompleted);
            await Assert.ThrowsAsync<IOException>(() => Client(transport).RecoverAsync(snapshot.Message.MessageId, default));
            await Assert.ThrowsAsync<IOException>(() => Client(transport).CommandAsync("slow", "commit", new("deployment"), default));
            await Assert.ThrowsAsync<IOException>(() => Client(transport).CommandAsync("slow", "commit", new("another-deployment"), default));
            Assert.Equal(1, Client(transport).ReadOutbox().Single(r => r.Message.ReceiverNodeId == "slow").Attempts);
        }
        finally { release.TrySetResult(); await first; }
        Assert.True((await Client(transport).CommandAsync("slow", "commit", new("deployment"), default)).Ack);
        Assert.Equal(2, transport.Requests);
    }

    [Fact]
    public async Task ConcurrentAdmissionsCannotExceedRecordQuota()
    {
        Directory.CreateDirectory(Outbox);
        // Use the real production limit, with only one free admission slot.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new NodeOutboxRecord(Message("seed"), "https://seed"));
        for (var i = 0; i < 9999; i++) await File.WriteAllBytesAsync(Path.Combine(Outbox, "seed-" + i + ".json"), bytes);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Admit(string peer)
        {
            await start.Task;
            try
            {
                await new NodeCommandOutbox(_directory).ExecuteAsync(Message(peer), "https://" + peer,
                    (m, _, _) => Task.FromResult(Ack(m)), default);
                return true;
            }
            catch (IOException) { return false; }
        }
        var first = Task.Run(() => Admit("one"));
        var second = Task.Run(() => Admit("two"));
        start.SetResult();
        Assert.Single(await Task.WhenAll(first, second), success => success);
        Assert.Equal(10000, Directory.GetFiles(Outbox, "*.json").Length);
        Assert.Empty(Directory.GetFiles(Outbox, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentAcknowledgementGrowthCannotExceedByteQuota()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<NodeAcknowledgement> Send(NodeMessage m, bool query, CancellationToken token)
        {
            if (Interlocked.Increment(ref arrivals) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Ack(m) with { Error = new string('x', 512 * 1024) };
        }
        async Task<bool> Execute(string peer)
        {
            try { await new NodeCommandOutbox(_directory).ExecuteAsync(Message(peer), "https://" + peer, Send, default); return true; }
            catch (IOException) { return false; }
        }
        var first = Execute("one");
        var second = Execute("two");
        bool[] results;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Both records exist. Reserve enough bytes for only one large ACK, including its temp file.
            var used = new DirectoryInfo(Outbox).GetFiles().Sum(f => f.Length);
            using var filler = File.Create(Path.Combine(Outbox, "quota-reservation"));
            filler.SetLength(64L * 1024 * 1024 - used - 768 * 1024);
        }
        finally { release.TrySetResult(); results = await Task.WhenAll(first, second); }
        Assert.Single(results, success => success);
        Assert.True(new DirectoryInfo(Outbox).GetFiles().Sum(f => f.Length) <= 64L * 1024 * 1024);
        Assert.Single(new NodeCommandOutbox(_directory).ReadRecords(), r => r.State == "Unknown");
        Assert.Empty(Directory.GetFiles(Outbox, "*.tmp"));
    }

    [Fact]
    public async Task BriefMetadataContentionWaitsThenConsumesExactlyOneAttempt()
    {
        Directory.CreateDirectory(Outbox);
        var message = Message("peer");
        Task<NodeAcknowledgement> pending;
        using (var held = new FileStream(Path.Combine(Outbox, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            pending = new NodeCommandOutbox(_directory).ExecuteAsync(message, "https://peer",
                (m, _, _) => Task.FromResult(Ack(m)), default);
            Assert.False(pending.IsCompleted);
        }
        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Ack);
        Assert.Equal(1, Assert.Single(new NodeCommandOutbox(_directory).ReadRecords()).Attempts);
    }

    [Fact]
    public async Task CancelledMetadataWaitReleasesPeerLockAndDoesNotCreateRecord()
    {
        Directory.CreateDirectory(Outbox);
        var message = Message("peer");
        using (var held = new FileStream(Path.Combine(Outbox, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NodeCommandOutbox(_directory)
                .ExecuteAsync(message, "https://peer", (m, _, _) => Task.FromResult(Ack(m)), cancel.Token));
        Assert.Empty(Directory.GetFiles(Outbox, "*.json"));
        Assert.True((await new NodeCommandOutbox(_directory).ExecuteAsync(message, "https://peer",
            (m, _, _) => Task.FromResult(Ack(m)), default)).Ack);
    }

    [Fact]
    public async Task FailedAtomicWriteReleasesBothLocksForAnotherPeerAndRetry()
    {
        var message = Message("peer");
        Directory.CreateDirectory(Outbox);
        var path = RecordPath(message);
        Directory.CreateDirectory(path + ".tmp");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new NodeCommandOutbox(_directory)
            .ExecuteAsync(message, "https://peer", (m, _, _) => Task.FromResult(Ack(m)), default));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Directory.Delete(path + ".tmp");
        Assert.True((await new NodeCommandOutbox(_directory).ExecuteAsync(Message("other"), "https://other",
            (m, _, _) => Task.FromResult(Ack(m)), default)).Ack);
        Assert.True((await new NodeCommandOutbox(_directory).ExecuteAsync(message, "https://peer",
            (m, _, _) => Task.FromResult(Ack(m)), default)).Ack);
    }

    [Fact]
    public async Task FailedSendReleasesPeerAndNewInstanceOnlyQueriesWithRemainingBudget()
    {
        var message = Message("peer");
        await Assert.ThrowsAsync<InvalidDataException>(() => new NodeCommandOutbox(_directory).ExecuteAsync(message, "https://peer",
            (_, _, _) => throw new InvalidDataException("invalid ACK"), default));
        Assert.True((await new NodeCommandOutbox(_directory).ExecuteAsync(message, "https://peer", (m, query, _) =>
        {
            Assert.True(query);
            return Task.FromResult(Ack(m));
        }, default, recoveryOnly: true)).Ack);
        Assert.Equal(2, Assert.Single(new NodeCommandOutbox(_directory).ReadRecords()).Attempts);
    }

    private string RecordPath(NodeMessage m) => Path.Combine(Outbox, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new[] { m.ClusterId, m.SenderNodeId, m.ReceiverNodeId, m.MessageId }))) + ".json");
    private static NodeMessage Message(string peer)
    {
        var now = DateTimeOffset.UtcNow;
        return new(2, "cluster", "control", peer, Guid.NewGuid().ToString("D"), "deployment", "configuration.commit",
            now, now.AddMinutes(5), JsonSerializer.SerializeToElement(new DistributionCommandRequest("deployment"), Json));
    }
    private static NodeAcknowledgement Ack(NodeMessage message) => new(true, message.ReceiverNodeId, message.CorrelationId, "commit");
    private DistributionNodeClient Client(Transport transport) => new(
        new StaticOptionsMonitor<DistributionOptions>(new()
        {
            Nodes = new() { ["slow"] = new() { Endpoint = "https://slow", ProtocolVersion = 2 },
                ["fast"] = new() { Endpoint = "https://fast", ProtocolVersion = 2 } }
        }), transport, new StaticOptionsMonitor<NodeCommunicationOptions>(new()
        {
            Enabled = true, ClusterId = "cluster", NodeId = "control", StateDirectory = _directory,
            Peers = new() { ["slow"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('k', 32) } },
                ["fast"] = new() { ActiveKeyId = "key", Keys = new() { ["key"] = new string('k', 32) } } }
        }));
    private sealed class Transport(Func<NodeMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler, IHttpClientFactory
    {
        private int _requests;
        public int Requests => _requests;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _requests);
            return await send((await request.Content!.ReadFromJsonAsync<NodeMessage>(Json, token))!, token);
        }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
