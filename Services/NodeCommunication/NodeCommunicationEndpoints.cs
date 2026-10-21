using System.Text.Json;
using LyWaf.Services.Distribution;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;

namespace LyWaf.Services.NodeCommunication;

public static class NodeCommunicationEndpoints
{
    public static void MapNodeCommunication(this WebApplication app, int port)
    {
        foreach (var operation in NodeProtocolOperations.All)
        {
            var name = operation;
            app.MapPost(NodeMessageAuthentication.Prefix + name, (HttpContext context, NodeMessageAuthentication authentication) =>
                DispatchAsync(context, authentication, name)).RequireHost($"*:{port}");
        }
    }

    public static async Task<IResult> DispatchAsync(HttpContext context, NodeMessageAuthentication authentication, string name)
    {
                if (!NodeProtocolOperations.IsSupported(name)) return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403);
                var (message, error) = await authentication.ReadAsync(context, name, context.RequestAborted);
                if (error is not null) return NodeProtocolResponse.Error(context, error);
                NodeProtocolResponse.BindAuthenticatedMessage(context, message!);
                try
                {
                    if (name is LyWaf.Services.Captcha.AuthorityCaptchaStore.IssueOperation or LyWaf.Services.Captcha.AuthorityCaptchaStore.TakeOperation)
                    {
                        var authority = context.RequestServices.GetService<LyWaf.Services.Captcha.AuthorityCaptchaStore>();
                        try
                        {
                            return authority is null ? NodeProtocolResponse.Error(context, "captcha_authority_unavailable", 503)
                                : await authority.HandleAsync(message!, context);
                        }
                        catch (LyWaf.Services.Captcha.CaptchaStoreUnavailableException)
                        { return NodeProtocolResponse.Error(context, "captcha_authority_unavailable", 503); }
                        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                        { return Results.Empty; }
                    }
                    if (name == CcExecutionQueryService.Operation)
                        return context.RequestServices.GetRequiredService<CcExecutionQueryService>().HandleAuthenticated(message!, context);
                    if (name == CcExecutionRelayService.Operation)
                        return await context.RequestServices.GetRequiredService<CcExecutionRelayService>().HandleAsync(message!, context);
                    if (name is PluginNodeMessageService.Operation or PluginNodeMessageService.RelayOperation)
                    {
                        var messaging = context.RequestServices.GetService<PluginNodeMessageService>();
                        if (messaging is null) return NodeProtocolResponse.Error(context, "node_plugin_missing", 404);
                        var result = name == PluginNodeMessageService.RelayOperation
                            ? await messaging.HandleRelayAsync(message!, context.RequestAborted)
                            : await messaging.HandleAuthenticatedAsync(message!, context.RequestAborted);
                        return result.Error is { } denied
                            ? NodeProtocolResponse.Error(context, denied) : NodeProtocolResponse.Success(context, result.Reply);
                    }
                    if (name == "node.handshake")
                    {
                        try
                        {
                            var handshake = context.RequestServices.GetService<NodeHandshakeService>();
                            if (handshake is null) return NodeProtocolResponse.Error(context, "node_handshake_unavailable", 503);
                            return NodeProtocolResponse.Success(context, await handshake.HandleAuthenticatedAsync(message!, context.RequestAborted,
                                context.Request.Headers["X-LyWaf-KeyId"].ToString(), NodeSocketProtocol.SessionBoot(context)));
                        }
                        catch (UnauthorizedAccessException) { return NodeProtocolResponse.Error(context, "node_handshake_forbidden", 403); }
                        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
                        { return NodeProtocolResponse.Error(context, "node_handshake_invalid", 400); }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException)
                        { return NodeProtocolResponse.Error(context, "node_handshake_unavailable", 503); }
                    }
                    if (name == CcCaptchaReleaseIssuer.Operation)
                        return await context.RequestServices.GetRequiredService<CcCaptchaReleaseIssuer>().HandleAsync(message!, context);
                    if (name == RuntimeStateSyncProtocol.Operation)
                    {
                        var runtime = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
                        if (!runtime.Enabled || runtime.NodeId != message!.ReceiverNodeId)
                            return NodeProtocolResponse.Error(context, "node_runtime_disabled_or_misconfigured", 403);
                        var batch = message.Payload.Deserialize<RuntimeStateSyncBatch>(NodeMessageAuthentication.JsonOptions);
                        if (batch is null || batch.ClusterId != message.ClusterId || batch.SenderNodeId != message.SenderNodeId
                            || batch.ReceiverNodeId != message.ReceiverNodeId)
                            return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
                        try { RuntimeStateSyncProtocol.Validate(batch); }
                        catch (InvalidDataException) { return NodeProtocolResponse.Error(context, "node_invalid_payload", 400); }
                        try
                        {
                            return NodeProtocolResponse.Success(context, await context.RequestServices.GetRequiredService<RuntimeStateSyncReceiver>()
                                .AcceptAsync(batch, context.RequestAborted));
                        }
                        catch (UnauthorizedAccessException) { return NodeProtocolResponse.Error(context, "node_operation_forbidden", 403); }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        { return NodeProtocolResponse.Error(context, "node_invalid_payload", 400); }
                        catch (InvalidDataException) { return NodeProtocolResponse.Error(context, "node_runtime_store_unavailable", 503); }
                        catch (IOException) { return NodeProtocolResponse.Error(context, "node_runtime_store_unavailable", 503); }
                    }
                    if (name == "runtime.merge")
                    {
                        var runtime = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RuntimeStateOptions>>().CurrentValue;
                        if (!runtime.Enabled || runtime.NodeId != message!.ReceiverNodeId)
                            return NodeProtocolResponse.Error(context, "node_runtime_disabled_or_misconfigured", 403);
                        var mutations = RuntimeStateBatch.Read(message!.Payload);
                        var denied = RuntimeStateReplicationAuthorization.AuthorizeReceive(runtime, message.SenderNodeId, mutations);
                        if (denied is not null)
                            return NodeProtocolResponse.Error(context, denied, 403);
                        try
                        {
                            var store = context.RequestServices.GetRequiredService<IRuntimeStateStore>();
                            foreach (var mutation in mutations) store.Merge(mutation);
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        { return NodeProtocolResponse.Error(context, "node_invalid_payload", 400); }
                        catch (IOException)
                        { return NodeProtocolResponse.Error(context, "node_runtime_store_unavailable", 503); }
                        return NodeProtocolResponse.Success(context, new { success = true });
                    }
                    if (name == "node.dashboard") return NodeProtocolResponse.Success(context, context.RequestServices.GetRequiredService<NodeDashboardAgent>().Capture());
                    var node = context.RequestServices.GetRequiredService<DistributionNodeService>();
                    if (name == "node.status") return NodeProtocolResponse.Success(context, node.GetStatus());
                    var inbox = context.RequestServices.GetRequiredService<NodeCommandInbox>();
                    if (name == "configuration.result")
                    {
                        var original = message!.Payload.Deserialize<NodeMessage>(NodeMessageAuthentication.JsonOptions);
                        if (original is null || original.ClusterId != message.ClusterId
                            || original.SenderNodeId != message.SenderNodeId || original.ReceiverNodeId != message.ReceiverNodeId
                            || original.CorrelationId != message.CorrelationId || !Guid.TryParseExact(original.MessageId, "D", out _)
                            || original.Type is not ("configuration.prepare" or "configuration.commit" or "configuration.rollback"))
                            return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
                        var prepare = original.Type == "configuration.prepare"
                            ? original.Payload.Deserialize<PrepareConfigurationRequest>(NodeMessageAuthentication.JsonOptions) : null;
                        return InboxResponse(context, await inbox.ExecuteAsync(original,
                            () => throw new InvalidOperationException("Result queries cannot execute commands"), context.RequestAborted,
                            () => node.ReconcileCommandAsync(original.Type, original.CorrelationId, prepare, context.RequestAborted), queryOnly: true));
                    }
                    if (name == "configuration.prepare")
                    {
                        var payload = message!.Payload.Deserialize<PrepareConfigurationRequest>(NodeMessageAuthentication.JsonOptions);
                        if (payload is null || payload.DeploymentId != message.CorrelationId) return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
                        return InboxResponse(context, await inbox.ExecuteAsync(message,
                            () => node.PrepareAsync(payload, context.RequestAborted), context.RequestAborted,
                            () => node.ReconcileCommandAsync(name, payload.DeploymentId, payload, context.RequestAborted)));
                    }
                    var command = message!.Payload.Deserialize<DistributionCommandRequest>(NodeMessageAuthentication.JsonOptions);
                    if (command is null || command.DeploymentId != message.CorrelationId) return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
                    return InboxResponse(context, await inbox.ExecuteAsync(message, () => name == "configuration.commit"
                        ? node.CommitAsync(command.DeploymentId, context.RequestAborted)
                        : node.RollbackAsync(command.DeploymentId, context.RequestAborted), context.RequestAborted,
                        () => node.ReconcileCommandAsync(name, command.DeploymentId, null, context.RequestAborted)));
                }
                catch (JsonException)
                {
                    return NodeProtocolResponse.Error(context, "node_invalid_payload", 400);
                }
                catch (Microsoft.Extensions.Options.OptionsValidationException)
                {
                    return NodeProtocolResponse.Error(context, "node_identity_unavailable", 503);
                }
                catch (Exception ex) when (name.StartsWith("configuration.", StringComparison.Ordinal)
                    && ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    // A failed outcome write may follow a completed side effect. Query durable evidence;
                    // never turn this transport failure into a definite business rejection.
                    return NodeProtocolResponse.Error(context, "node_outcome_unknown", 409);
                }
    }

    private static IResult InboxResponse(HttpContext context, NodeInboxResult result)
    {
        if (result.Error is { } error) return NodeProtocolResponse.Error(context, error);
        var ack = result.Acknowledgement;
        if (ack is { Ack: false, ErrorCode: NodeConfigurationErrors.Unknown })
            return NodeProtocolResponse.Error(context, "node_outcome_unknown", 409);
        if (ack is { Ack: false })
            return NodeProtocolResponse.Success(context, ack with { Error = ack.ErrorCode },
                NodeConfigurationErrors.HttpStatus(ack.ErrorCode));
        return NodeProtocolResponse.Success(context, ack);
    }
}
