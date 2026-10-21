using System.Text.Json;
using System.Threading.Channels;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.RuntimeState;

internal static class RuntimeStateBatch
{
    internal const int MaxItems = 32;

    internal static (NodeMessage Message, byte[] Body) Pack(NodeMessage envelope, RuntimeStateMutation first,
        ChannelReader<RuntimeStateMutation> queue, long maxBytes, Func<RuntimeStateMutation, bool>? canInclude = null)
    {
        var items = new List<RuntimeStateMutation> { first };
        (NodeMessage Message, byte[] Body) Serialize()
        {
            var message = envelope with { Payload = JsonSerializer.SerializeToElement(items, NodeMessageAuthentication.JsonOptions) };
            return (message, JsonSerializer.SerializeToUtf8Bytes(message, NodeMessageAuthentication.JsonOptions));
        }
        var result = Serialize();
        if (result.Body.Length > maxBytes) throw new InvalidOperationException("Runtime mutation exceeds the node message size limit");
        var inspected = 0;
        while (items.Count < MaxItems && inspected++ < MaxItems && queue.TryPeek(out var next))
        {
            if (canInclude is not null && !canInclude(next))
            {
                queue.TryRead(out _);
                continue;
            }
            items.Add(next);
            var candidate = Serialize();
            if (candidate.Body.Length > maxBytes) break;
            if (!queue.TryRead(out _)) break;
            result = candidate;
        }
        return result;
    }

    internal static RuntimeStateMutation[] Read(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            if (payload.GetArrayLength() is < 1 or > MaxItems) throw new JsonException("Invalid runtime batch size");
            var items = payload.Deserialize<RuntimeStateMutation[]>(NodeMessageAuthentication.JsonOptions)!;
            if (items.Any(x => x is null)) throw new JsonException("Null runtime mutation");
            return items;
        }
        return [payload.Deserialize<RuntimeStateMutation>(NodeMessageAuthentication.JsonOptions) ?? throw new JsonException("Missing mutation")];
    }
}
