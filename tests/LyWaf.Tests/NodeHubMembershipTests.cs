using System.Text.Json;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeHubMembershipTests
{
    private const string Secret = "membership-test-secret-at-least-32-bytes";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static NodeCommunicationOptions Controller() => new()
    {
        Enabled = true, Mode = "Controller", Role = "Controller", ClusterId = "cluster", NodeId = "controller",
        Peers = new()
        {
            ["a"] = new() { Role = "Node", ActiveKeyId = "new", Keys = new() { ["new"] = Secret, ["old"] = Secret + "old" },
                AllowedOperations = ["node.handshake"] },
            ["b"] = new() { Role = "Node", Enabled = false, AdmissionState = "Revoked" }
        }
    };
    private static NodeCommunicationOptions Node() => new()
    {
        Enabled = true, Mode = "Node", Role = "Node", ClusterId = "cluster", NodeId = "a", ControllerNodeId = "controller",
        Peers = new() { ["controller"] = new() { Role = "Controller", ActiveKeyId = "new", Keys = new()
            { ["new"] = Secret, ["old"] = Secret + "old" }, AllowedOperations = ["node.handshake"] } }
    };
    private static NodeMessage Request() => new(2, "cluster", "a", "controller", Guid.NewGuid().ToString("D"), "membership",
        "node.handshake", Now, Now.AddMinutes(1), JsonSerializer.SerializeToElement(new { }));
    private static NodeHandshake Remote() => new("cluster", "controller", Guid.NewGuid().ToString("N"), "Controller", Now, "1", [2]);

    [Fact]
    public void OmittingAuthorityPreservesLegacyCanonicalProofSignature()
    {
        var proof = NodeHubMembership.Create(Controller(), Request(), Remote(), "new", Now);
        var legacy = new { proof.ClusterId, proof.ControllerNodeId, proof.ControllerBootId, proof.ReceiverNodeId,
            proof.RequestId, proof.IssuedAt, proof.ExpiresAt, proof.KnownNodes, proof.KeyId, Signature = "" };
        var signature = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Secret),
            JsonSerializer.SerializeToUtf8Bytes(legacy, NodeMessageAuthentication.JsonOptions)));
        Assert.Equal(signature, proof.Signature);
        Assert.DoesNotContain("authority", JsonSerializer.Serialize(proof, NodeMessageAuthentication.JsonOptions));
    }

    [Theory]
    [InlineData("PendingApproval")]
    [InlineData("Draining")]
    [InlineData("missing-issue")]
    [InlineData("missing-take")]
    public void HandshakePermissionAloneDoesNotDiscloseSharedChallengeAuthority(string denial)
    {
        var controller = Controller();
        var peer = controller.Peers["a"];
        peer.AllowedOperations.Add("captcha.store.issue"); peer.AllowedOperations.Add("captcha.store.take");
        if (denial.StartsWith("missing-")) peer.AllowedOperations.Remove(denial == "missing-issue" ? "captcha.store.issue" : "captcha.store.take");
        else peer.AdmissionState = denial;
        var authority = new NodeHubAuthority(NodeSetupCrypto.PublicKey(NodeSetupCrypto.NewPrivateKey()), new string('c', 64), "cluster");
        var proof = NodeHubMembership.Create(controller, Request(), Remote(), "new", Now, authority);
        Assert.Null(proof.Authority);
        Assert.DoesNotContain(authority.ChallengeSecret, JsonSerializer.Serialize(proof));
    }

    [Fact]
    public void ProofBindsKnownHistoricalSourcesWithoutClaimingOnlineStatusAndExpires()
    {
        var clock = new Clock();
        var cache = new NodeHubMembership(clock);
        var request = Request(); var remote = Remote();
        var proof = NodeHubMembership.Create(Controller(), request, remote, "new", Now);
        cache.Accept(Node(), request, remote, proof);
        Assert.Equal(new[] { "a", "b", "controller" }, cache.KnownNodes("cluster", "controller"));
        Assert.Empty(cache.KnownNodes("another-cluster", "controller"));
        Assert.Empty(cache.KnownNodes("cluster", "another-controller"));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(proof));
        clock.Now = Now.AddMinutes(1);
        Assert.False(cache.IsFresh("cluster", "controller"));
        Assert.Empty(cache.KnownNodes("cluster", "controller"));
    }

    [Fact]
    public void AcceptedSourcesAreImmutableAndDisconnectInvalidatesThem()
    {
        var cache = new NodeHubMembership(new Clock());
        var request = Request(); var remote = Remote();
        var proof = NodeHubMembership.Create(Controller(), request, remote, "new", Now);
        cache.Accept(Node(), request, remote, proof);
        ((string[])proof.KnownNodes)[0] = "tampered";
        Assert.DoesNotContain("tampered", cache.KnownNodes("cluster", "controller"));
        cache.Invalidate();
        Assert.Empty(cache.KnownNodes("cluster", "controller"));
    }

    [Fact]
    public void KeyRotationOverlapSignsWithTheKeyThatAuthenticatedTheRequest()
    {
        var request = Request(); var remote = Remote();
        var proof = NodeHubMembership.Create(Controller(), request, remote, "old", Now);
        Assert.Equal("old", proof.KeyId);
        var node = Node();
        node.Peers["controller"].Keys.Remove("new");
        NodeHubMembership.Validate(node, request, remote, proof, Now);
        node.Peers["controller"].Keys.Clear();
        Assert.Throws<InvalidDataException>(() => NodeHubMembership.Validate(node, request, remote, proof, Now));
    }

    public static IEnumerable<object[]> Tampering() => new[]
    {
        "cluster", "controller", "receiver", "request", "boot", "expiry", "signature", "sources", "duplicates", "ordering", "key"
    }.Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(Tampering))]
    public void TamperingCannotReplaceAnExistingValidSnapshot(string field)
    {
        var request = Request(); var remote = Remote();
        var proof = NodeHubMembership.Create(Controller(), request, remote, "new", Now);
        var cache = new NodeHubMembership(new Clock());
        cache.Accept(Node(), request, remote, proof);
        var invalid = field switch
        {
            "cluster" => proof with { ClusterId = "other" },
            "controller" => proof with { ControllerNodeId = "other" },
            "receiver" => proof with { ReceiverNodeId = "b" },
            "request" => proof with { RequestId = Guid.NewGuid().ToString("D") },
            "boot" => proof with { ControllerBootId = Guid.NewGuid().ToString("N") },
            "expiry" => proof with { ExpiresAt = Now.AddHours(1) },
            "signature" => proof with { Signature = new string('0', 64) },
            "sources" => proof with { KnownNodes = ["a", "controller", "forged"] },
            "duplicates" => proof with { KnownNodes = ["a", "a", "controller"] },
            "ordering" => proof with { KnownNodes = ["controller", "b", "a"] },
            _ => proof with { KeyId = "unknown" }
        };
        Assert.Throws<InvalidDataException>(() => cache.Accept(Node(), request, remote, invalid));
        Assert.Equal(new[] { "a", "b", "controller" }, cache.KnownNodes("cluster", "controller"));
    }

    [Fact]
    public void AValidProofCannotBeConsumedByAnotherRequestOrControllerBoot()
    {
        var request = Request(); var remote = Remote();
        var proof = NodeHubMembership.Create(Controller(), request, remote, "new", Now);
        Assert.Throws<InvalidDataException>(() => NodeHubMembership.Validate(Node(), request with
            { MessageId = Guid.NewGuid().ToString("D") }, remote, proof, Now));
        Assert.Throws<InvalidDataException>(() => NodeHubMembership.Validate(Node(), request, remote with
            { BootId = Guid.NewGuid().ToString("N") }, proof, Now));
        Assert.Throws<InvalidDataException>(() => NodeHubMembership.Validate(Node(), request, remote, null, Now));
    }

    [Fact]
    public void NegotiatedCcMembersRetainOfflineAndDrainingNodesButExcludeRevokedAndPending()
    {
        var config = Controller();
        config.Peers["draining"] = new() { Role = "Node", AdmissionState = "Draining" };
        config.Peers["pending"] = new() { Role = "Node", AdmissionState = "PendingApproval" };
        config.Peers["disabled"] = new() { Role = "Node", Enabled = false };
        var request = Request(); var remote = Remote();
        var proof = NodeHubMembership.Create(config, request, remote, "new", Now, includeCcMembership: true);
        var clock = new Clock(); var cache = new NodeHubMembership(clock);
        cache.Accept(Node(), request, remote, proof);
        Assert.Equal(new[] { "a", "controller", "draining" }, cache.ExpectedCcNodes("cluster", "controller"));
        Assert.Contains("b", cache.KnownNodes("cluster", "controller"));
        ((string[])proof.ExpectedCcNodes!)[0] = "tampered";
        Assert.DoesNotContain("tampered", cache.ExpectedCcNodes("cluster", "controller")!);
        Assert.Null(cache.ExpectedCcNodes("other", "controller"));
        clock.Now = Now.AddMinutes(1);
        Assert.Null(cache.ExpectedCcNodes("cluster", "controller"));
        clock.Now = Now;
        cache.Invalidate();
        Assert.Null(cache.ExpectedCcNodes("cluster", "controller"));
    }

    [Fact]
    public void CcMembershipIsSignedAndNeverInferredFromLegacyHistoricalSources()
    {
        var request = Request(); var remote = Remote(); var cache = new NodeHubMembership(new Clock());
        var legacy = NodeHubMembership.Create(Controller(), request, remote, "new", Now);
        cache.Accept(Node(), request, remote, legacy);
        Assert.Null(cache.ExpectedCcNodes("cluster", "controller"));
        Assert.DoesNotContain("expectedCcNodes", JsonSerializer.Serialize(legacy, NodeMessageAuthentication.JsonOptions));
        var proof = NodeHubMembership.Create(Controller(), request, remote, "new", Now, includeCcMembership: true);
        foreach (var nodes in new string[][] { ["controller"], ["a", "b", "controller"], [], ["controller", "a"], ["a", "a", "controller"] })
            Assert.Throws<InvalidDataException>(() => cache.Accept(Node(), request, remote, proof with { ExpectedCcNodes = nodes }));
        cache.Accept(Node(), request, remote, proof);
        Assert.Equal(new[] { "a", "controller" }, cache.ExpectedCcNodes("cluster", "controller"));
        var current = Node();
        Assert.Equal(new[] { "a", "controller" }, cache.ExpectedCcNodes(current));
        current.Peers["controller"].Keys.Clear();
        Assert.Null(cache.ExpectedCcNodes(current));
        current = Node();
        current.Peers["controller"].AdmissionState = "Revoked";
        Assert.Null(cache.ExpectedCcNodes(current));
        current = Node();
        current.Peers["controller"].AllowedOperations.Clear();
        Assert.Null(cache.ExpectedCcNodes(current));
    }

    [Fact]
    public void LegacyCapabilityShapeDoesNotGainAnUnnegotiatedField()
    {
        var bytes = JsonSerializer.Serialize(new NodeControlCapabilities(1, 1), NodeMessageAuthentication.JsonOptions);
        Assert.DoesNotContain("ccMembership", bytes);
        Assert.Contains("\"ccMembership\":1", JsonSerializer.Serialize(new NodeControlCapabilities(1, 1, 1),
            NodeMessageAuthentication.JsonOptions));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = NodeHubMembershipTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
