using System.Text;
using LyWaf.Services.NodeCommunication;
using Xunit;

namespace LyWaf.Tests;

public sealed class NodeSignatureVectorTests
{
    [Fact]
    public void CanonicalSignatureMatchesIndependentNodeCryptoVector()
    {
        // Generated independently with node:crypto, UTF-8, LF separators and no trailing LF.
        var signature = NodeMessageAuthentication.Compute(
            "0123456789abcdef0123456789abcdef", "node.status", "cluster-a", "controller-1", "edge-2",
            "key-v1", "1792410000", "00112233445566778899aabbccddeeff",
            Encoding.UTF8.GetBytes("""{"hello":"node"}"""));
        Assert.Equal("46602CEF3C3C980CA6C1C00DEE0328F9D27358599E0BF1BCFDF60AF62F42AA2E", signature);
    }
}
