using System.Security.Cryptography;
using LyWaf.Services.RuntimeState;

namespace LyWaf.Tests;

internal static class CcReleaseTestKeys
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public static RuntimeStateMutation Sign(RuntimeStateMutation mutation)
        => CcReleaseAuthorization.Sign(mutation, Configure(new() { NodeId = mutation.NodeId }));
    public static RuntimeStateOptions Configure(RuntimeStateOptions options)
    {
        options.CcReleaseTrustDomain = "test-cluster";
        options.CcReleasePrivateKeyPem = Key.ExportPkcs8PrivateKeyPem();
        foreach (var node in new[] { "a", "b", "sender", "receiver", "scope", "validate", "captcha", "verify", "unwritable", options.NodeId })
            options.CcReleaseAuthorities[node] = Key.ExportSubjectPublicKeyInfoPem();
        return options;
    }
}
