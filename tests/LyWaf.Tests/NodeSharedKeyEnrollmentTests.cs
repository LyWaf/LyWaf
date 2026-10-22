using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSharedKeyEnrollmentTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void SignaturesBindEveryRequestFieldAndDirection()
    {
        var request = NodeSharedKeyEnrollment.Create(Secret, "node-test", "Node test");
        NodeSharedKeyEnrollment.Validate(request, Secret);
        foreach (var tampered in new[]
        {
            request with { NodeId = "node-other" }, request with { Name = "Other" },
            request with { Version = 2 }, request with { Nonce = new string('0', 32) },
            request with { Timestamp = request.Timestamp - 120 },
            request with { Signature = NodeSharedKeyEnrollment.Reply(Secret, request, "credential").Signature }
        }) Assert.Throws<UnauthorizedAccessException>(() => NodeSharedKeyEnrollment.Validate(tampered, Secret));
        Assert.Throws<UnauthorizedAccessException>(() => NodeSharedKeyEnrollment.Validate(request, new string('f', 64)));
    }

    [Theory]
    [InlineData("Credential", "", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("SharedKey", "lywaf_node_x", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("Unknown", "", "")]
    [InlineData("SharedKey", "", "")]
    public async Task InvalidOrMixedAuthenticationDoesNotSaveConfiguration(string mode, string credential, string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-join-validation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NodeSetupStore(root);
            await Assert.ThrowsAsync<ArgumentException>(() => store.ConfigureAsync("Node", "http://localhost:7030/", credential,
                default, authenticationMode: mode, sharedKey: key));
            Assert.Equal("Standalone", store.Status().Mode);
            Assert.False(File.Exists(Path.Combine(root, "setup.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
