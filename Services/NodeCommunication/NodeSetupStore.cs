using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.NodeCommunication;

public sealed record NodeSetupCredential(int Version, string ClusterId, string ControllerNodeId,
    string NodeId, string KeyId, string Secret, string ReleasePublicKeyPem = "", string ChallengeSecret = "", string ReleaseTrustDomain = "");
public sealed record NodeSetupPeer(string NodeId, string Name, string KeyId, string Secret, bool Enabled = true);
public sealed record NodeControllerSetup(string ClusterId, string NodeId, IReadOnlyList<NodeSetupPeer> Peers,
    string ReleasePrivateKeyPem = "", string ChallengeSecret = "", string ReleaseTrustDomain = "",
    string SharedSecret = "", string SharedKeyId = "", IReadOnlyList<string>? RetiredNodeIds = null);
public sealed record NodeClientSetup(string ControllerUrl, NodeSetupCredential Credential, string AuthenticationMode = "Credential");
public sealed record NodeSetupState(int Version, long Revision, string Mode,
    NodeControllerSetup? Controller = null, NodeClientSetup? Node = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? SynchronizeRuntimeState = null);
public sealed record NodeSetupStatus(string Mode, string ActiveMode, string? NodeId, string? ClusterId,
    string? ControllerUrl, bool HasCredential, bool RestartRequired, long Revision, bool AuthorityUpgradePending = false,
    bool SynchronizeRuntimeState = false, bool RuntimeSynchronizationActive = false, bool RuntimeSynchronizationConfigured = false,
    bool RuntimeHistoryChangePending = false, bool HasSharedCommunicationKey = false, string AuthenticationMode = "Credential");
public sealed record NodeSetupPeerStatus(string NodeId, string Name, bool Enabled);

/// <summary>Node-local role and per-node credentials. Role changes apply on restart; peer grants can refresh live.</summary>
public sealed class NodeSetupStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);
    private NodeSetupState? desired;
    private NodeSetupState? active;
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const string Prefix = "lywaf_node_";
    private readonly string? legacyPrivateKey, legacyChallengeSecret, legacyTrustDomain;
    private readonly bool legacyRuntimeStateEnabled;

    public NodeSetupStore(string directory, string? signingKey = null, string? challengeSecret = null, string? trustDomain = null,
        bool legacyRuntimeStateEnabled = false)
    {
        root = Path.GetFullPath(directory);
        if (Path.GetPathRoot(root) == root) throw new InvalidDataException("Node setup cannot use a filesystem root");
        legacyPrivateKey = signingKey; legacyChallengeSecret = challengeSecret; legacyTrustDomain = trustDomain;
        this.legacyRuntimeStateEnabled = legacyRuntimeStateEnabled;
        desired = active = Read();
        if (active?.Mode == "Controller" && string.IsNullOrEmpty(active.Controller?.ReleasePrivateKeyPem))
            Task.Run(() => MutateAsync(state => state with { Controller = EnsureController(state.Controller) }, true, CancellationToken.None))
                .GetAwaiter().GetResult();
    }

    internal NodeSetupState? Active => Volatile.Read(ref active);
    internal bool RuntimeSynchronizationEnabled(NodeSetupState? state)
    {
        if (state is null || state.Mode == "Standalone") return false;
        if (state.SynchronizeRuntimeState is null) return legacyRuntimeStateEnabled;
        if (state.SynchronizeRuntimeState != true) return false;
        return state.Mode == "Controller" ? !string.IsNullOrEmpty(state.Controller!.ReleasePrivateKeyPem)
            : !string.IsNullOrEmpty(state.Node!.Credential.ReleasePublicKeyPem);
    }
    internal string ChallengeSecret
    {
        get
        {
            var state = Active;
            return state?.Mode switch
            {
                "Controller" => state.Controller!.ChallengeSecret,
                "Node" => state.Node!.Credential.ChallengeSecret,
                _ => ""
            };
        }
    }
    public NodeSetupStatus Status()
    {
        var state = Volatile.Read(ref desired);
        var running = Active;
        var client = state?.Mode == "Node" ? state.Node : null;
        return new(state?.Mode ?? "Standalone", running?.Mode ?? "Standalone",
            client?.Credential.NodeId ?? state?.Controller?.NodeId,
            client?.Credential.ClusterId ?? state?.Controller?.ClusterId, client?.ControllerUrl,
            client is not null, state?.Revision != running?.Revision, state?.Revision ?? 0,
            running?.Mode == "Node" && client is not null && client.Credential.NodeId == running.Node!.Credential.NodeId
                && client.Credential.ClusterId == running.Node.Credential.ClusterId
                && client.Credential.ControllerNodeId == running.Node.Credential.ControllerNodeId
                && string.IsNullOrEmpty(running.Node.Credential.ReleasePublicKeyPem) && !string.IsNullOrEmpty(client.Credential.ReleasePublicKeyPem),
            state?.SynchronizeRuntimeState ?? legacyRuntimeStateEnabled, RuntimeSynchronizationEnabled(running), state?.SynchronizeRuntimeState is not null,
            state is not null && (state.Mode != (running?.Mode ?? "Standalone")
                || state.Mode != "Standalone" && running is not null && !string.Equals(RuntimeDirectory(state), RuntimeDirectory(running),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)),
            !string.IsNullOrEmpty(state?.Controller?.SharedSecret), client?.AuthenticationMode ?? "Credential");
    }

    internal NodeHubAuthority? ControllerAuthority(NodeCommunicationOptions config)
    {
        var state = Active;
        var controller = state?.Controller;
        return state?.Mode == "Controller" && config.Mode == "Controller" && config.Role == "Controller"
            && controller is not null && controller.NodeId == config.NodeId && controller.ClusterId == config.ClusterId
            ? new(NodeSetupCrypto.PublicKey(controller.ReleasePrivateKeyPem), controller.ChallengeSecret, controller.ReleaseTrustDomain) : null;
    }

    internal async Task<bool> StageAuthorityUpgradeAsync(NodeCommunicationOptions config, NodeMessage request,
        NodeHandshake remote, NodeHubMembershipProof? proof, CancellationToken token)
    {
        NodeHubMembership.Validate(config, request, remote, proof, DateTimeOffset.UtcNow);
        if (proof!.Authority is not { } authority) return false;
        var running = Active;
        if (running?.Mode != "Node") return false;
        var credential = running.Node!.Credential;
        if (credential.NodeId != config.NodeId || credential.ClusterId != config.ClusterId
            || credential.ControllerNodeId != config.ControllerNodeId || credential.KeyId != proof.KeyId
            || !config.Peers[config.ControllerNodeId].Keys.TryGetValue(credential.KeyId, out var secret) || secret != credential.Secret)
            throw new InvalidDataException("Node authority upgrade identity mismatch");
        if (!string.IsNullOrEmpty(credential.ReleasePublicKeyPem))
        {
            if (new NodeHubAuthority(credential.ReleasePublicKeyPem, credential.ChallengeSecret, credential.ReleaseTrustDomain) != authority)
                throw new InvalidDataException("Pinned controller authority changed");
            return false;
        }
        var changed = false;
        await MutateAsync(current =>
        {
            // Never overwrite a pending operator edit, even if it retains the same node identity.
            if (current.Revision != running.Revision || current.Mode != "Node" || current.Node != running.Node) return current;
            changed = true;
            return current with { Node = current.Node with { Credential = credential with
            {
                ReleasePublicKeyPem = authority.ReleasePublicKeyPem,
                ChallengeSecret = authority.ChallengeSecret,
                ReleaseTrustDomain = authority.ReleaseTrustDomain
            } } };
        }, refreshPeers: false, token);
        return changed;
    }

    public IReadOnlyList<NodeSetupPeerStatus> Peers() => Volatile.Read(ref desired)?.Controller?.Peers
        .Select(peer => new NodeSetupPeerStatus(peer.NodeId, peer.Name, peer.Enabled)).ToArray() ?? [];

    public async Task<NodeSetupStatus> ConfigureAsync(string mode, string? controllerUrl, string? credential, CancellationToken token,
        bool? synchronizeRuntimeState = null, string? authenticationMode = null, string? sharedKey = null, string? nodeName = null)
    {
        if (mode is not ("Standalone" or "Controller" or "Node")) throw new ArgumentException("Invalid node mode");
        NodeClientSetup? client = null;
        if (mode == "Node")
        {
            var endpoint = NormalizeUrl(controllerUrl);
            var previous = Volatile.Read(ref desired)?.Node;
            var method = authenticationMode ?? previous?.AuthenticationMode ?? "Credential";
            if (method is not ("Credential" or "SharedKey")) throw new ArgumentException("Invalid authentication mode");
            NodeSetupCredential key;
            if (method == "SharedKey" && !string.IsNullOrEmpty(sharedKey))
            {
                if (!string.IsNullOrWhiteSpace(credential)) throw new ArgumentException("Ambiguous authentication");
                var secret = NormalizeSharedKey(sharedKey);
                var id = previous?.ControllerUrl == endpoint ? previous.Credential.NodeId : await EnrollmentIdentityAsync(token);
                var name = string.IsNullOrWhiteSpace(nodeName) ? id : nodeName.Trim();
                key = await NodeSharedKeyEnrollment.JoinAsync(endpoint, secret, id, name, token);
            }
            else if (method == "SharedKey")
            {
                if (!string.IsNullOrWhiteSpace(credential) || previous?.AuthenticationMode != method || previous.ControllerUrl != endpoint)
                    throw new ArgumentException("Shared communication key is required");
                key = previous.Credential;
            }
            else
            {
                if (!string.IsNullOrEmpty(sharedKey)) throw new ArgumentException("Ambiguous authentication");
                key = string.IsNullOrWhiteSpace(credential)
                    ? previous is { AuthenticationMode: "Credential" } ? previous.Credential : throw new ArgumentException("Node credential is required")
                    : DecodeCredential(credential);
            }
            client = new(endpoint, key, method);
        }
        await MutateAsync(state => state with
        {
            Mode = mode,
            Controller = mode == "Controller" ? EnsureController(state.Controller) : state.Controller,
            Node = client ?? state.Node,
            SynchronizeRuntimeState = synchronizeRuntimeState ?? state.SynchronizeRuntimeState
                ?? (state.Mode == "Standalone" && mode != "Standalone" ? true : (bool?)null)
        }, refreshPeers: false, token);
        return Status();
    }

    public async Task<(NodeSetupPeerStatus Peer, string Credential)> CreateCredentialAsync(string name, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Invalid node name");
        name = name.Trim();
        if (name.Length is < 1 or > 128 || name.Any(char.IsControl)) throw new ArgumentException("Invalid node name");
        var peer = new NodeSetupPeer("node-" + Guid.NewGuid().ToString("N"), name,
            "key-" + Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        NodeSetupCredential? issued = null;
        await MutateAsync(state =>
        {
            var controller = state.Controller;
            if (state.Mode != "Controller" || controller is null) throw new InvalidOperationException("Controller mode is required");
            if (controller.Peers.Count + (controller.RetiredNodeIds?.Count ?? 0) >= 1024) throw new InvalidOperationException("Node identity history capacity exceeded");
            controller = EnsureController(controller);
            if (!string.IsNullOrEmpty(controller.SharedSecret)) peer = peer with { Secret = controller.SharedSecret, KeyId = controller.SharedKeyId };
            issued = new(1, controller.ClusterId, controller.NodeId, peer.NodeId, peer.KeyId, peer.Secret,
                NodeSetupCrypto.PublicKey(controller.ReleasePrivateKeyPem), controller.ChallengeSecret, controller.ReleaseTrustDomain);
            return state with { Controller = controller with { Peers = controller.Peers.Append(peer).ToArray() } };
        }, refreshPeers: true, token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(issued!, Json);
        return (new(peer.NodeId, peer.Name, peer.Enabled), Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }

    public async Task SetPeerEnabledAsync(string nodeId, bool enabled, CancellationToken token)
    {
        await MutateAsync(state =>
        {
            if (state.Mode != "Controller" || state.Controller is null) throw new InvalidOperationException("Controller mode is required");
            if (!state.Controller.Peers.Any(peer => peer.NodeId == nodeId)) throw new KeyNotFoundException("Node is not configured");
            return state with { Controller = state.Controller with { Peers = state.Controller.Peers
                .Select(peer => peer.NodeId == nodeId ? peer with { Enabled = enabled } : peer).ToArray() } };
        }, refreshPeers: true, token);
    }

    public async Task DeletePeerAsync(string nodeId, CancellationToken token)
    {
        await MutateAsync(state =>
        {
            if (state.Mode != "Controller" || state.Controller is null) throw new InvalidOperationException("Controller mode is required");
            if (Active is { Mode: "Controller" } running && running.Revision != state.Revision)
                throw new InvalidOperationException("Apply pending controller changes before editing membership");
            var controller = state.Controller;
            if (!controller.Peers.Any(peer => peer.NodeId == nodeId)) throw new KeyNotFoundException("Node is not configured");
            return state with { Controller = controller with
            {
                Peers = controller.Peers.Where(peer => peer.NodeId != nodeId).ToArray(),
                RetiredNodeIds = (controller.RetiredNodeIds ?? []).Append(nodeId).Distinct(StringComparer.Ordinal).ToArray()
            } };
        }, refreshPeers: true, token);
    }

    public async Task<bool> SetSharedCommunicationKeyAsync(string key, CancellationToken token)
    {
        var secret = NormalizeSharedKey(key);
        var changed = false;
        await MutateAsync(state =>
        {
            if (state.Mode != "Controller" || state.Controller is null) throw new InvalidOperationException("Controller mode is required");
            if (Active is { Mode: "Controller" } running && running.Revision != state.Revision)
                throw new InvalidOperationException("Apply pending controller changes before changing the shared key");
            if (state.Controller.SharedSecret == secret) return state;
            changed = true;
            var keyId = "shared-" + Guid.NewGuid().ToString("N");
            return state with { Controller = state.Controller with
            {
                SharedSecret = secret, SharedKeyId = keyId,
                Peers = state.Controller.Peers.Select(peer => peer with { Secret = secret, KeyId = keyId }).ToArray()
            } };
        }, refreshPeers: true, token);
        return changed;
    }

    internal static string NormalizeSharedKey(string key)
    {
        if (key is not { Length: >= 32 and <= 256 } || key.Any(char.IsControl) || string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Invalid shared communication key");
        return key.Length == 64 && key.All(char.IsAsciiHexDigit) ? key.ToLowerInvariant()
            : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    private async Task<string> EnrollmentIdentityAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var path = Path.Combine(root, "shared-node-id");
            RejectLinks(path);
            Directory.CreateDirectory(root);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 128) throw new InvalidDataException("Invalid enrollment identity");
                var existing = await File.ReadAllTextAsync(path, token);
                if (!SafeId(existing)) throw new InvalidDataException("Invalid enrollment identity");
                return existing;
            }
            var id = "node-" + Guid.NewGuid().ToString("N");
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes(id), token);
            output.Flush(true);
            return id;
        }
        finally { gate.Release(); }
    }

    internal async Task<string> EnrollSharedKeyAsync(NodeSharedKeyRequest request, CancellationToken token)
    {
        var running = Active;
        var controller = running?.Mode == "Controller" ? running.Controller : null;
        if (controller is null || string.IsNullOrEmpty(controller.SharedSecret)) throw new UnauthorizedAccessException();
        NodeSharedKeyEnrollment.Validate(request, controller.SharedSecret);
        var claim = await PersistentReplayGuard.ClaimAsync(Path.Combine(root, "shared-enrollment-replay"),
            controller.ClusterId + ":" + controller.SharedKeyId + ":" + request.Nonce, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), token);
        if (claim == ReplayClaim.Replay) throw new UnauthorizedAccessException();
        if (claim != ReplayClaim.Accepted) throw new IOException("Enrollment replay store unavailable");
        await MutateAsync(state =>
        {
            if (state.Mode != "Controller" || state.Revision != Active?.Revision
                || state.Controller?.SharedSecret != controller.SharedSecret || state.Controller.SharedKeyId != controller.SharedKeyId)
                throw new InvalidOperationException("Controller configuration changed");
            var current = state.Controller;
            if (request.NodeId == current.NodeId || current.RetiredNodeIds?.Contains(request.NodeId) == true)
                throw new UnauthorizedAccessException();
            var peer = current.Peers.FirstOrDefault(peer => peer.NodeId == request.NodeId);
            if (peer is not null)
            {
                if (!peer.Enabled) throw new UnauthorizedAccessException();
                return state;
            }
            if (current.Peers.Count + (current.RetiredNodeIds?.Count ?? 0) >= 1024)
                throw new InvalidOperationException("Node identity history capacity exceeded");
            return state with { Controller = state.Controller with
            {
                Peers = current.Peers.Append(new(request.NodeId, request.Name, current.SharedKeyId, current.SharedSecret)).ToArray()
            } };
        }, refreshPeers: true, token);
        // Use the validated generation, not a possibly rotated key read after releasing the store lock.
        var credential = new NodeSetupCredential(1, controller.ClusterId, controller.NodeId, request.NodeId,
            controller.SharedKeyId, controller.SharedSecret, NodeSetupCrypto.PublicKey(controller.ReleasePrivateKeyPem),
            controller.ChallengeSecret, controller.ReleaseTrustDomain);
        return Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(credential, Json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public async Task<string> IssuePeerCredentialAsync(string nodeId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var state = Volatile.Read(ref desired);
            if (state?.Mode != "Controller" || state.Controller is null) throw new InvalidOperationException("Controller mode is required");
            var controller = state.Controller;
            var peer = controller.Peers.FirstOrDefault(peer => peer.NodeId == nodeId)
                ?? throw new KeyNotFoundException("Node is not configured");
            var credential = new NodeSetupCredential(1, controller.ClusterId, controller.NodeId, peer.NodeId, peer.KeyId, peer.Secret,
                NodeSetupCrypto.PublicKey(controller.ReleasePrivateKeyPem), controller.ChallengeSecret, controller.ReleaseTrustDomain);
            return Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(credential, Json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally { gate.Release(); }
    }

    internal string StateDirectory(NodeSetupState state) => Path.Combine(root, "clusters",
        state.Mode == "Controller" ? state.Controller!.ClusterId : state.Node!.Credential.ClusterId);
    internal string RuntimeDirectory(NodeSetupState state) => Path.Combine(StateDirectory(state),
        state.SynchronizeRuntimeState is null ? "runtime" : RuntimeSynchronizationEnabled(state) ? "runtime-shared" : "runtime-local");

    private NodeControllerSetup EnsureController(NodeControllerSetup? controller)
    {
        controller ??= new("cluster-" + Guid.NewGuid().ToString("N"), "controller-" + Guid.NewGuid().ToString("N"), []);
        if (!string.IsNullOrEmpty(controller.ReleasePrivateKeyPem)) return controller;
        var key = string.IsNullOrWhiteSpace(legacyPrivateKey) ? NodeSetupCrypto.NewPrivateKey() : legacyPrivateKey;
        var secret = string.IsNullOrEmpty(legacyChallengeSecret)
            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() : legacyChallengeSecret;
        var domain = string.IsNullOrWhiteSpace(legacyTrustDomain) ? controller.ClusterId : legacyTrustDomain;
        if (!NodeSetupCrypto.ValidPrivateKey(key) || !NodeSetupCrypto.ValidChallengeSecret(secret) || !NodeSetupCrypto.ValidTrustDomain(domain))
            throw new InvalidDataException("Invalid controller authority configuration");
        return controller with { ReleasePrivateKeyPem = key, ChallengeSecret = secret, ReleaseTrustDomain = domain };
    }

    public static string NormalizeUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.HostNameType == UriHostNameType.Unknown
            || System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)
                && (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)))
            throw new ArgumentException("Invalid controller URL");
        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }

    public static NodeSetupCredential DecodeCredential(string value)
    {
        if (value.Length > 4096 || !value.StartsWith(Prefix, StringComparison.Ordinal)) throw new ArgumentException("Invalid node credential");
        try
        {
            var encoded = value[Prefix.Length..];
            if (encoded.Length == 0 || encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new ArgumentException("Invalid node credential");
            encoded = encoded.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var key = JsonSerializer.Deserialize<NodeSetupCredential>(Convert.FromBase64String(encoded), Json);
            if (key is null || !Valid(key)) throw new ArgumentException("Invalid node credential");
            return key;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new ArgumentException("Invalid node credential"); }
    }

    private static bool SafeId(string? value) => NodeMessageAuthentication.ValidId(value) && value is not ("." or "..")
        && !value!.EndsWith('.');
    private static bool SameRunningConfiguration(NodeSetupState a, NodeSetupState b) => a.Mode == b.Mode
        && (a.Mode == "Standalone" || a.SynchronizeRuntimeState == b.SynchronizeRuntimeState) && (a.Mode switch
    {
        "Controller" => JsonSerializer.Serialize(a.Controller, Json) == JsonSerializer.Serialize(b.Controller, Json),
        "Node" => JsonSerializer.Serialize(a.Node, Json) == JsonSerializer.Serialize(b.Node, Json),
        _ => true
    });
    private static bool Valid(NodeSetupCredential key) => key.Version == 1
        && SafeId(key.ClusterId) && SafeId(key.ControllerNodeId)
        && SafeId(key.NodeId) && key.NodeId != key.ControllerNodeId
        && SafeId(key.KeyId) && key.Secret is { Length: 64 } && key.Secret.All(char.IsAsciiHexDigit)
        && (string.IsNullOrEmpty(key.ReleasePublicKeyPem) && string.IsNullOrEmpty(key.ChallengeSecret) && string.IsNullOrEmpty(key.ReleaseTrustDomain)
            || NodeSetupCrypto.ValidPublicKey(key.ReleasePublicKeyPem) && NodeSetupCrypto.ValidChallengeSecret(key.ChallengeSecret)
                && NodeSetupCrypto.ValidTrustDomain(key.ReleaseTrustDomain));

    private async Task MutateAsync(Func<NodeSetupState, NodeSetupState> update, bool refreshPeers, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            RejectLinks(root);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
            else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            RejectLinks(root);
            var lockPath = Path.Combine(root, "setup.lock");
            RejectLinks(lockPath);
            using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = Read();
            if (current?.Revision != desired?.Revision) throw new IOException("Node setup changed externally; restart before editing");
            var candidate = update(current ?? new(1, 0, "Standalone")) with { Revision = current?.Revision ?? 0 };
            Validate(candidate);
            token.ThrowIfCancellationRequested();
            if (current is not null && JsonSerializer.Serialize(candidate, Json) == JsonSerializer.Serialize(current, Json)) return;
            var next = candidate with { Revision = checked((current?.Revision ?? 0) + 1) };
            Validate(next);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            if (bytes.Length > MaximumBytes) throw new IOException("Node setup exceeds storage capacity");
            var temporary = Path.Combine(root, "setup-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    Share = FileShare.None, Options = FileOptions.WriteThrough | FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(temporary, fileOptions))
                {
                    await stream.WriteAsync(bytes, token);
                    await stream.FlushAsync(token);
                    stream.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, Path.Combine(root, "setup.json"), overwrite: true);
                Volatile.Write(ref desired, next);
                // A pending role/identity switch must not accidentally become live when adding a peer.
                var running = Active;
                if (running is not null && (SameRunningConfiguration(running, next)
                    || refreshPeers && running.Mode == "Controller" && current?.Mode == "Controller"
                        && running.Revision == current.Revision && running.Controller?.NodeId == next.Controller?.NodeId))
                    Volatile.Write(ref active, next);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private NodeSetupState? Read()
    {
        var path = Path.Combine(root, "setup.json");
        RejectLinks(path);
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > MaximumBytes) throw new InvalidDataException("Node setup exceeds storage capacity");
        var state = JsonSerializer.Deserialize<NodeSetupState>(file, Json) ?? throw new InvalidDataException("Invalid node setup");
        Validate(state);
        return state;
    }

    private static void Validate(NodeSetupState state)
    {
        if (state.Version != 1 || state.Revision < 0 || state.Mode is not ("Standalone" or "Controller" or "Node")
            || state.Mode == "Controller" && state.Controller is null || state.Mode == "Node" && state.Node is null)
            throw new InvalidDataException("Invalid node setup");
        if (state.Controller is { } controller && (!SafeId(controller.ClusterId)
            || !SafeId(controller.NodeId) || controller.Peers is null || controller.Peers.Count > 1024
            || controller.Peers.Any(peer => peer is null || string.IsNullOrWhiteSpace(peer.Name) || peer.Name.Length > 128
                || peer.Name.Any(char.IsControl) || !Valid(new(1, controller.ClusterId, controller.NodeId, peer.NodeId, peer.KeyId, peer.Secret)))
            || controller.Peers.Select(peer => peer.NodeId).Distinct(StringComparer.Ordinal).Count() != controller.Peers.Count))
            throw new InvalidDataException("Invalid controller setup");
        if (state.Controller is { } shared)
        {
            if (!(string.IsNullOrEmpty(shared.SharedSecret) && string.IsNullOrEmpty(shared.SharedKeyId))
                && (!SafeId(shared.SharedKeyId) || shared.SharedSecret is not { Length: 64 } || !shared.SharedSecret.All(char.IsAsciiHexDigit)
                    || shared.Peers.Any(peer => peer.Secret != shared.SharedSecret || peer.KeyId != shared.SharedKeyId)))
                throw new InvalidDataException("Invalid shared communication key");
            if (shared.RetiredNodeIds is { } retired && (retired.Count + shared.Peers.Count > 1024
                || retired.Any(id => !SafeId(id) || id == shared.NodeId || shared.Peers.Any(peer => peer.NodeId == id))
                || retired.Distinct(StringComparer.Ordinal).Count() != retired.Count))
                throw new InvalidDataException("Invalid retired node identity history");
        }
        if (state.Controller is { } authority && !(string.IsNullOrEmpty(authority.ReleasePrivateKeyPem)
                && string.IsNullOrEmpty(authority.ChallengeSecret) && string.IsNullOrEmpty(authority.ReleaseTrustDomain))
            && (!NodeSetupCrypto.ValidPrivateKey(authority.ReleasePrivateKeyPem) || !NodeSetupCrypto.ValidChallengeSecret(authority.ChallengeSecret)
                || !NodeSetupCrypto.ValidTrustDomain(authority.ReleaseTrustDomain)))
            throw new InvalidDataException("Invalid controller authority configuration");
        if (state.Node is { } node)
        {
            if (node.AuthenticationMode is not ("Credential" or "SharedKey") || node.Credential is null || !Valid(node.Credential)) throw new InvalidDataException("Invalid node setup");
            try { NormalizeUrl(node.ControllerUrl); }
            catch (ArgumentException) { throw new InvalidDataException("Invalid node controller URL"); }
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Node setup paths cannot be symbolic links");
    }
}
