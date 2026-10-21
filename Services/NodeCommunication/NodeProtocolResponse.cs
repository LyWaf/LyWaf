using System.Text.Json;
using System.Text.Json.Nodes;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeErrorResponse(bool Success, string ErrorCode, string MessageKey, string TraceId,
    string? MessageId = null, string? OperationId = null, string Status = "Rejected",
    object? MessageArgs = null, bool Retryable = false);

public static class NodeProtocolResponse
{
    private static readonly object AuthenticatedMessageKey = new();

    public static void BindAuthenticatedMessage(HttpContext context, NodeMessage message) =>
        context.Items[AuthenticatedMessageKey] = message;

    public static IResult Success(HttpContext context, object? value, int statusCode = 200)
    {
        var body = JsonSerializer.SerializeToNode(value, NodeMessageAuthentication.JsonOptions) as JsonObject ?? new JsonObject();
        var message = context.Items[AuthenticatedMessageKey] as NodeMessage;
        // Keep operation-specific fields at the top level for existing v2 clients.
        var accepted = body["ack"]?.GetValue<bool>() ??
            (body["status"]?.GetValue<string>() is not ("Conflict" or "Gap" or "SnapshotRequired" or "ProtocolConflict"));
        body["success"] ??= accepted;
        body["messageId"] = message?.MessageId;
        body["operationId"] = message?.CorrelationId;
        body["status"] ??= accepted ? "Completed" : "Rejected";
        body["messageArgs"] ??= new JsonObject();
        body["retryable"] ??= false;
        if (value is LyWaf.Services.RuntimeState.RuntimeStateSyncAck sync)
        {
            var code = sync.Status switch
            {
                "Conflict" => "node_sync_conflict",
                "Gap" => "node_sync_gap",
                "SnapshotRequired" => "node_sync_snapshot_required",
                _ => null
            };
            if (code is not null)
            {
                // Repair signals remain HTTP 200 business replies; transport dispatches on Status.
                body["errorCode"] = code;
                body["messageKey"] = "node.errors." + code;
            }
        }
        body["traceId"] = context.TraceIdentifier;
        return Results.Json(body, statusCode: statusCode);
    }

    public static IResult Error(HttpContext context, string code, int statusCode) =>
        Results.Json(new NodeErrorResponse(false, code, "node.errors." + code, context.TraceIdentifier,
            (context.Items[AuthenticatedMessageKey] as NodeMessage)?.MessageId,
            (context.Items[AuthenticatedMessageKey] as NodeMessage)?.CorrelationId,
            code == "node_outcome_unknown" ? "Unknown" : "Rejected", new { }, statusCode is 429 or 503),
            statusCode: statusCode);

    public static IResult Error(HttpContext context, NodeProtocolError error) =>
        Error(context, error.Code, error.StatusCode);
}
