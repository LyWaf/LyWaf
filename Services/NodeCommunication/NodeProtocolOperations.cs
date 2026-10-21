using LyWaf.Services.RuntimeState;
using LyWaf.Services.Statistic;

namespace LyWaf.Services.NodeCommunication;

public static class NodeProtocolOperations
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        "configuration.prepare", "configuration.commit", "configuration.rollback", "configuration.result",
        "node.status", "node.dashboard", "node.handshake", "runtime.merge", CcCaptchaReleaseIssuer.Operation,
        RuntimeStateSyncProtocol.Operation, PluginNodeMessageService.Operation, PluginNodeMessageService.RelayOperation, CcExecutionQueryService.Operation,
        CcExecutionRelayService.Operation,
        LyWaf.Services.Captcha.AuthorityCaptchaStore.IssueOperation, LyWaf.Services.Captcha.AuthorityCaptchaStore.TakeOperation
    });

    public static bool IsSupported(string operation) => All.Contains(operation, StringComparer.Ordinal);
}
