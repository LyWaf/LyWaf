using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeInboxCapacityBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbox-capacity-boundary-" + Guid.NewGuid().ToString("N"));
    private const long Limit = 64L * 1024 * 1024;
    private NodeCommandInbox Inbox() => new(new StaticOptionsMonitor<NodeCommunicationOptions>(new() { StateDirectory = _root }));

    [Theory]
    [InlineData(0, false)]
    [InlineData(4096, true)]
    public async Task AdmissionReservesIntentAndOutcomeBeforeAnyEffect(int headroom, bool succeeds)
    {
        var directory = Path.Combine(_root, "inbox");
        Directory.CreateDirectory(directory);
        var filler = Path.Combine(directory, "fixture-owned.tmp");
        using (var stream = File.Create(filler)) stream.SetLength(Limit - 1024 * 1024 - headroom);
        var message = new NodeMessage(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "deployment",
            "configuration.commit", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), JsonSerializer.SerializeToElement(new { deploymentId = "deployment" }));
        // This result fits the record limit but the original Unknown still occupies the directory during replacement.
        var empty = new NodeAcknowledgement(true, "edge", "deployment", "");
        var overhead = JsonSerializer.SerializeToUtf8Bytes(new
        { Digest = new string('A', 64), Result = empty, CompletedAt = DateTimeOffset.UtcNow, Retired = false }).Length;
        var acknowledgement = empty with { Stage = new string('x', 1024 * 1024 - overhead - 16) };
        var effects = 0;
        Task<NodeAcknowledgement> Execute() { effects++; return Task.FromResult(acknowledgement); }
        if (succeeds) Assert.True((await Inbox().ExecuteAsync(message, Execute, default)).Acknowledgement!.Ack);
        else
        {
            var rejected = await Inbox().ExecuteAsync(message, Execute, default);
            Assert.Null(rejected.Acknowledgement);
            Assert.Equal("node_inbox_full", rejected.Error?.Code);
            Assert.Equal(503, rejected.Error?.StatusCode);
            Assert.Empty(Directory.GetFiles(directory, "*.json"));
            Assert.Equal(0, effects);
        }
        Assert.True(Directory.GetFiles(directory).Sum(p => new FileInfo(p).Length) <= Limit);
        var repeated = await Inbox().ExecuteAsync(message, Execute, default);
        Assert.Equal(succeeds ? 1 : 0, effects);
        if (succeeds) Assert.True(repeated.Acknowledgement!.Ack);
        else
        {
            Assert.Null(repeated.Acknowledgement);
            Assert.Equal("node_inbox_full", repeated.Error?.Code);
            Assert.Empty(Directory.GetFiles(directory, "*.json"));
        }
    }

    [Fact]
    public async Task ExternalGrowthAfterExecutionPreservesUnknownUntilQueryReconcilesWithoutReplay()
    {
        var directory = Path.Combine(_root, "inbox");
        Directory.CreateDirectory(directory);
        var filler = Path.Combine(directory, "fixture-owned.tmp");
        using (var stream = File.Create(filler)) stream.SetLength(Limit - 1024 * 1024 - 4096);
        var message = new NodeMessage(2, "cluster", "control", "edge", Guid.NewGuid().ToString("D"), "deployment",
            "configuration.commit", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            JsonSerializer.SerializeToElement(new { deploymentId = "deployment" }));
        var acknowledgement = new NodeAcknowledgement(true, "edge", "deployment", "commit");
        var effects = 0;
        Task<NodeAcknowledgement> Execute()
        {
            effects++;
            // An external writer does not honor Inbox admission reservations. Consume the remaining
            // space only after the real durable intent exists and the simulated effect has executed.
            var intent = Assert.Single(Directory.GetFiles(directory, "*.json"));
            using var document = JsonDocument.Parse(File.ReadAllText(intent));
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("Result").ValueKind);
            var otherBytes = Directory.GetFiles(directory).Where(path => path != filler)
                .Sum(path => new FileInfo(path).Length);
            using (var stream = File.OpenWrite(filler)) stream.SetLength(Limit - otherBytes);
            return Task.FromResult(acknowledgement);
        }

        await Assert.ThrowsAsync<IOException>(() => Inbox().ExecuteAsync(message, Execute, default));
        Assert.Equal(1, effects);
        Assert.Equal(Limit, Directory.GetFiles(directory).Sum(path => new FileInfo(path).Length));
        var record = Assert.Single(Directory.GetFiles(directory, "*.json"));
        var unknownBytes = File.ReadAllBytes(record);
        using (var document = JsonDocument.Parse(unknownBytes))
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("Result").ValueKind);

        var repeated = await Inbox().ExecuteAsync(message, Execute, default);
        Assert.Null(repeated.Acknowledgement);
        Assert.Equal("node_outcome_unknown", repeated.Error?.Code);
        var query = await Inbox().ExecuteAsync(message, Execute, default, queryOnly: true);
        Assert.Equal("node_outcome_unknown", query.Error?.Code);
        Assert.Equal(unknownBytes, File.ReadAllBytes(record));
        Assert.Equal(1, effects);

        File.Delete(filler);
        var reconciled = await Inbox().ExecuteAsync(message, Execute, default,
            () => Task.FromResult<NodeAcknowledgement?>(acknowledgement), queryOnly: true);
        Assert.Equal(acknowledgement, reconciled.Acknowledgement);
        Assert.Null(reconciled.Error);
        var cached = await Inbox().ExecuteAsync(message, Execute, default);
        Assert.Equal(acknowledgement, cached.Acknowledgement);
        Assert.Equal(1, effects);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
