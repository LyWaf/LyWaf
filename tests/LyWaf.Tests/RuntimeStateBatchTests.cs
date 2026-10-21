using System.Text.Json;
using System.Threading.Channels;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateBatchTests
{
    private static RuntimeStateMutation Mutation(int index) => new("cc", index.ToString(), "node", index,
        DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement("value"));
    private static NodeMessage Envelope() => new(2, "cluster", "sender", "receiver", Guid.NewGuid().ToString("D"),
        "runtime-state", "runtime.merge", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), default);

    [Fact]
    public void BatchHasBoundedItemCountAndLeavesRemainingItemsQueued()
    {
        var queue = Channel.CreateUnbounded<RuntimeStateMutation>();
        for (var i = 1; i <= 40; i++) queue.Writer.TryWrite(Mutation(i));
        var result = RuntimeStateBatch.Pack(Envelope(), Mutation(0), queue.Reader, 100000);
        Assert.Equal(32, RuntimeStateBatch.Read(result.Message.Payload).Length);
        Assert.True(queue.Reader.TryRead(out var remaining));
        Assert.Equal("32", remaining.Key);
    }

    [Fact]
    public void ExactEnvelopeByteBudgetDoesNotConsumeOversizedNextItem()
    {
        var queue = Channel.CreateUnbounded<RuntimeStateMutation>();
        var envelope = Envelope();
        var first = Mutation(0);
        var single = RuntimeStateBatch.Pack(envelope, first, queue.Reader, 100000);
        queue.Writer.TryWrite(Mutation(1));
        var result = RuntimeStateBatch.Pack(envelope, first, queue.Reader, single.Body.Length);
        Assert.Single(RuntimeStateBatch.Read(result.Message.Payload));
        Assert.Equal(single.Body.Length, result.Body.Length);
        Assert.True(queue.Reader.TryPeek(out _));
        Assert.Throws<InvalidOperationException>(() => RuntimeStateBatch.Pack(envelope, first, queue.Reader, 1));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("null")]
    public void InvalidBatchesAreRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => RuntimeStateBatch.Read(document.RootElement));
    }
}
