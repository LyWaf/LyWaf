namespace LyWaf.Services.NodeCommunication;

public static class NodeAdmissionPolicy
{
    public static bool HasSingleConfigurationWriter(NodeCommunicationOptions config) => config.Role != "Node" ||
        config.Peers.Values.Count(peer => peer.Role == "Controller" && (peer.AllowedOperations ?? []).Any(operation =>
            (operation is "configuration.prepare" or "configuration.commit" or "configuration.rollback")
            && Allows(peer, operation))) <= 1;

    public static bool IsValid(string state) => state is "PendingApproval" or "Active" or "Draining" or "Revoked";

    public static bool Allows(NodePeerOptions peer, string operation) => peer.Enabled && (peer.AdmissionState switch
    {
        "Active" => true,
        "PendingApproval" => operation == "node.handshake",
        "Draining" => operation is "node.handshake" or "node.status" or "node.dashboard"
            or "configuration.result" or "configuration.rollback",
        _ => false
    });
}
