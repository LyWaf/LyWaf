using System.Text.Json;
using System.Threading.Channels;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.RuntimeState;

internal static class RuntimeStateSyncBatchPacking
{
    internal static IReadOnlyList<RuntimeStateMutation> Take(NodeCommunicationOptions config, string peer,
        RuntimeStateMutation first, ChannelReader<RuntimeStateMutation>? queue, Func<RuntimeStateMutation, bool> allowed)
    {
        var items = new List<RuntimeStateMutation> { first };
        bool Fits()
        {
            // Reserve the largest cursor and timestamp representation, including the signed envelope.
            var batch = new RuntimeStateSyncBatch(config.ClusterId, config.NodeId, peer,
                new string('a', 32), long.MaxValue - 1, items, new string('A', 64));
            var payload = JsonSerializer.SerializeToUtf8Bytes(batch, RuntimeStateSyncProtocol.Json);
            var message = new NodeMessage(2, config.ClusterId, config.NodeId, peer, Guid.Empty.ToString("D"),
                batch.Epoch, RuntimeStateSyncProtocol.Operation, DateTimeOffset.MaxValue, DateTimeOffset.MaxValue,
                JsonSerializer.SerializeToElement(batch, RuntimeStateSyncProtocol.Json));
            return payload.Length <= RuntimeStateSyncProtocol.MaximumBatchBytes
                && JsonSerializer.SerializeToUtf8Bytes(message, RuntimeStateSyncProtocol.Json).Length <= config.MaxMessageBytes;
        }
        if (!Fits()) throw new InvalidOperationException("Runtime sync mutation exceeds configured message limit");
        var inspected = 0;
        while (queue is not null && items.Count < RuntimeStateBatch.MaxItems && inspected++ < RuntimeStateBatch.MaxItems
            && queue.TryPeek(out var next))
        {
            if (!allowed(next)) { queue.TryRead(out _); continue; }
            items.Add(next);
            if (!Fits()) { items.RemoveAt(items.Count - 1); break; }
            if (!queue.TryRead(out _)) { items.RemoveAt(items.Count - 1); break; }
        }
        return items;
    }
}
