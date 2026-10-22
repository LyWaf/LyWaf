using LyWaf.Plugins.Core;
using LyWaf.Services.NodeCommunication;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LyWaf.Plugins.Sys.Analysis;

public sealed class AnalysisNodeRequests : IPluginNodeRequestHandler
{
    public const string Id = "analysis";
    public static IReadOnlyList<PluginNodeRequestCapability> Capabilities { get; } = Array.AsReadOnly(new[]
    {
        new PluginNodeRequestCapability(CcExecutionQueryService.Operation, 1, CcExecutionQueryService.Operation),
        new PluginNodeRequestCapability(CcExecutionRelayService.Operation, 1, CcExecutionRelayService.Operation),
        new PluginNodeRequestCapability(CcCaptchaReleaseIssuer.Operation, 1, CcCaptchaReleaseIssuer.Operation)
    });
    public string PluginId => Id;
    public IReadOnlyList<PluginNodeRequestCapability> Requests => Capabilities;
    public Task<IResult> HandleNodeRequestAsync(PluginNodeRequest request, HttpContext context)
    {
        var message = new NodeMessage(2, request.ClusterId, request.SenderNodeId, request.ReceiverNodeId,
            request.MessageId, request.CorrelationId, request.MessageType, request.CreatedAt, request.ExpiresAt, request.Payload);
        return message.Type switch
        {
        CcExecutionQueryService.Operation => Task.FromResult(context.RequestServices.GetRequiredService<CcExecutionQueryService>().HandleAuthenticated(message, context)),
        CcExecutionRelayService.Operation => context.RequestServices.GetRequiredService<CcExecutionRelayService>().HandleAsync(message, context),
        CcCaptchaReleaseIssuer.Operation => context.RequestServices.GetRequiredService<CcCaptchaReleaseIssuer>().HandleAsync(message, context),
        _ => Task.FromResult(NodeProtocolResponse.Error(context, "node_plugin_schema_unsupported", 422))
        };
    }

    public static void Register(IServiceCollection services) => services.TryAddEnumerable(
        ServiceDescriptor.Singleton<IPluginNodeRequestHandler, AnalysisNodeRequests>());
}
