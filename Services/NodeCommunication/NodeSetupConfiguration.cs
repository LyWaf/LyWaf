using LyWaf.Services.Distribution;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LyWaf.Services.RuntimeState;
using LyWaf.Services.AntiBot;

namespace LyWaf.Services.NodeCommunication;

/// <summary>Projects the small setup contract onto the existing protocol settings without exposing credentials to the UI.</summary>
public static class NodeSetupConfiguration
{
    public static NodeSetupStore AddNodeSetup(this ConfigurationManager configuration, IServiceCollection services, string directory)
    {
        var oldSecret = configuration["AntiBot:Secret"];
        var store = new NodeSetupStore(directory, configuration["RuntimeState:CcReleasePrivateKeyPem"],
            NodeSetupCrypto.ValidChallengeSecret(oldSecret) ? oldSecret : null, configuration["RuntimeState:CcReleaseTrustDomain"],
            configuration.GetValue("RuntimeState:Enabled", false));
        services.AddSingleton(store);
        services.TryAddSingleton<NodeHubMembership>();
        var source = new MemoryConfigurationSource { InitialData = Values(store, configuration) };
        ((IConfigurationBuilder)configuration).Add(source);
        services.PostConfigure<NodeCommunicationOptions>(options => Apply(options, store));
        services.PostConfigure<DistributionOptions>(options => Apply(options, store));
        services.AddOptions<RuntimeStateOptions>().PostConfigure<NodeHubMembership>((options, membership) => Apply(options, store, membership));
        services.PostConfigure<AntiBotOptions>(options =>
        {
            if (!string.IsNullOrEmpty(store.ChallengeSecret)) options.Secret = store.ChallengeSecret;
        });
        return store;
    }

    internal static Dictionary<string, string?> Values(NodeSetupStore store, IConfiguration configuration)
    {
        var data = new Dictionary<string, string?>();
        var state = store.Active;
        if (state is null) return data;
        var enabled = state.Mode != "Standalone";
        data["NodeCommunication:Mode"] = state.Mode;
        data["NodeCommunication:Enabled"] = enabled.ToString();
        data["NodeCommunication:Role"] = state.Mode == "Controller" ? "Controller" : "Node";
        data["NodeCommunication:AllowLegacyProtocol"] = "false";
        data["NodeCommunication:ControllerUrl"] = state.Mode == "Node" ? state.Node!.ControllerUrl : "";
        data["Distribution:Enabled"] = enabled.ToString();
        data["Distribution:Role"] = state.Mode;
        if (!enabled)
        {
            data["RuntimeState:Enabled"] = "false";
            data["CaptchaStore:Provider"] = "Local";
            return data;
        }
        var cluster = state.Mode == "Controller" ? state.Controller!.ClusterId : state.Node!.Credential.ClusterId;
        var node = state.Mode == "Controller" ? state.Controller!.NodeId : state.Node!.Credential.NodeId;
        var authority = state.Mode == "Controller" ? node : state.Node!.Credential.ControllerNodeId;
        var directory = store.StateDirectory(state);
        data["NodeCommunication:ClusterId"] = cluster;
        data["NodeCommunication:NodeId"] = node;
        data["NodeCommunication:ControllerNodeId"] = authority;
        data["NodeCommunication:StateDirectory"] = Path.Combine(directory, "communication");
        data["NodeCommunication:RequireHttps"] = (state.Mode == "Node" ? new Uri(state.Node!.ControllerUrl).Scheme == "https"
            : configuration.GetValue("WafInfos:ControlListen:IsHttps", false)).ToString();
        data["Distribution:NodeId"] = node;
        data["Distribution:StateDirectory"] = Path.Combine(directory, "distribution");
        data["RuntimeState:NodeId"] = node;
        data["RuntimeState:StateDirectory"] = store.RuntimeDirectory(state);
        if (state.SynchronizeRuntimeState is not null)
            data["RuntimeState:Enabled"] = store.RuntimeSynchronizationEnabled(state).ToString();
        data["CaptchaStore:Provider"] = "Authority";
        data["CaptchaStore:ClusterId"] = cluster;
        data["CaptchaStore:AuthorityNodeId"] = authority;
        data["CaptchaStore:AuthorityEndpoint"] = state.Node?.ControllerUrl ?? "";
        data["CaptchaStore:StateDirectory"] = Path.Combine(directory, "captcha");
        return data;
    }

    internal static void Apply(NodeCommunicationOptions options, NodeSetupStore store)
    {
        var state = store.Active;
        if (state is null) return;
        options.Peers.Clear();
        if (state.Mode == "Controller")
        {
            options.HistoricalNodeIds = (state.Controller!.RetiredNodeIds ?? []).ToList();
            foreach (var peer in state.Controller!.Peers)
                options.Peers[peer.NodeId] = Peer("Node", peer.KeyId, peer.Secret, peer.Enabled);
        }
        else if (state.Mode == "Node")
        {
            var peer = state.Node!.Credential;
            options.Peers[peer.ControllerNodeId] = Peer("Controller", peer.KeyId, peer.Secret, true);
        }
    }

    internal static void Apply(DistributionOptions options, NodeSetupStore store)
    {
        var state = store.Active;
        if (state is null) return;
        options.Nodes.Clear();
        if (state.Mode == "Controller")
            foreach (var peer in state.Controller!.Peers)
                options.Nodes[peer.NodeId] = new() { Enabled = peer.Enabled, ProtocolVersion = 2,
                    Endpoint = "https://node-socket.invalid/" };
        else if (state.Mode == "Node") options.Nodes[state.Node!.Credential.ControllerNodeId] = new()
            { Enabled = true, ProtocolVersion = 2, Endpoint = state.Node.ControllerUrl };
    }

    private static NodePeerOptions Peer(string role, string keyId, string secret, bool enabled) => new()
    {
        Role = role, ActiveKeyId = keyId, Keys = new() { [keyId] = secret }, Enabled = enabled,
        AdmissionState = enabled ? "Active" : "Revoked", AllowedOperations = NodeProtocolOperations.All
            .Where(operation => role == "Controller" || !operation.StartsWith("configuration.", StringComparison.Ordinal)).ToList()
    };

    internal static void Apply(RuntimeStateOptions options, NodeSetupStore store, NodeHubMembership membership)
    {
        var state = store.Active;
        if (state is null) return;
        options.Peers.Clear();
        options.ManagedTopologyRole = state.Mode is "Controller" or "Node" ? state.Mode : "";
        if (state.Mode == "Standalone") { options.Enabled = false; return; }
        if (state.SynchronizeRuntimeState is not null) options.Enabled = store.RuntimeSynchronizationEnabled(state);
        var controller = state.Mode == "Controller" ? state.Controller!.NodeId : state.Node!.Credential.ControllerNodeId;
        var cluster = state.Mode == "Controller" ? state.Controller!.ClusterId : state.Node!.Credential.ClusterId;
        options.HubControllerNodeId = controller;
        if (state.Mode == "Controller")
        {
            options.CcReleasePrivateKeyPem = state.Controller!.ReleasePrivateKeyPem;
            options.CcReleaseTrustDomain = state.Controller.ReleaseTrustDomain;
            options.CcReleaseAuthorities.Clear();
            options.CcReleaseAuthorities[controller] = NodeSetupCrypto.PublicKey(state.Controller.ReleasePrivateKeyPem);
            var known = state.Controller!.Peers.Select(peer => peer.NodeId).Concat(state.Controller.RetiredNodeIds ?? [])
                .Append(controller).ToHashSet(StringComparer.Ordinal);
            options.HubSourceIsKnown = known.Contains;
            foreach (var peer in state.Controller.Peers)
                options.Peers[peer.NodeId] = RuntimePeer("https://node-socket.invalid/", peer.Enabled, false);
        }
        else
        {
            options.CcReleasePrivateKeyPem = "";
            options.CcCaptchaAuthorityNodeId = controller;
            var credential = state.Node!.Credential;
            if (!string.IsNullOrEmpty(credential.ReleasePublicKeyPem))
            {
                options.CcReleaseTrustDomain = credential.ReleaseTrustDomain;
                options.CcReleaseAuthorities.Clear();
                options.CcReleaseAuthorities[controller] = credential.ReleasePublicKeyPem;
            }
            options.HubSourceIsKnown = source => membership.IsKnown(cluster, controller, source);
            options.Peers[controller] = RuntimePeer(state.Node!.ControllerUrl, true, true);
        }

        RuntimeStatePeerOptions RuntimePeer(string endpoint, bool enabled, bool authority) => new()
        {
            Endpoint = endpoint, Enabled = enabled, ProtocolVersion = 2, DurableSyncEnabled = true,
            BootstrapAuthority = authority, AllowBootstrapExport = !authority,
            Send = Policy(), Receive = Policy()
        };
        RuntimeStateReplicationPolicy Policy() => new()
        {
            AllowedDomains = ["cc.access", "cc.attack", "cc.error", "cc.triggered", "cc.ban.logged", "cc.policy",
                "cc.decision", CcPolicyGeneration.Domain, "ip.blocked"],
            AllowedProtectionGroups = options.ProtectionGroupKeyPrefixes.Keys.ToList(), AllowUngroupedKeys = true
        };
    }
}
