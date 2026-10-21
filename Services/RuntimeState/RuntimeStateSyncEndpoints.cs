using System.Text.Json;
using LyWaf.Services.NodeCommunication;

namespace LyWaf.Services.RuntimeState;

public static class RuntimeStateSyncEndpoints
{
    public static async Task<IResult> HandleAsync(HttpContext context, NodeMessage message)
    {
            try
            {
                var batch = message!.Payload.Deserialize<RuntimeStateSyncBatch>(RuntimeStateSyncProtocol.Json);
                if (batch is null || batch.ClusterId != message.ClusterId || batch.SenderNodeId != message.SenderNodeId
                    || batch.ReceiverNodeId != message.ReceiverNodeId) return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
                var ack = await context.RequestServices.GetRequiredService<RuntimeStateSyncReceiver>().AcceptAsync(batch, context.RequestAborted);
                return Results.Json(ack, statusCode: ack.Status == "Applied" ? 200 : 409);
            }
            catch (UnauthorizedAccessException) { return NodeProtocolResponse.Error(context, "runtime_peer_forbidden", 403); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException or InvalidOperationException)
            { return NodeProtocolResponse.Error(context, "node_invalid_payload", 400); }
            catch (IOException) { return NodeProtocolResponse.Error(context, "node_inbox_unavailable", 503); }
    }
}
