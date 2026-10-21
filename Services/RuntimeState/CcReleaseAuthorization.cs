using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

public sealed record CcReleaseProof(string Issuer, string TrustDomain, string Signature);

/// <summary>End-to-end release authority, independent of the peer forwarding a runtime batch.</summary>
internal static class CcReleaseAuthorization
{
    private static byte[] Payload(RuntimeStateMutation mutation, string issuer, string domain)
    {
        var generation = mutation.Value!.Value.Deserialize<CcPolicyGeneration>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return JsonSerializer.SerializeToUtf8Bytes(new object[]
        { "lywaf.cc.release.v1", domain, issuer, mutation.Domain, mutation.Key, mutation.Sequence,
            mutation.ChangedAt, generation.Version, generation.ReleaseId });
    }

    internal static RuntimeStateMutation Sign(RuntimeStateMutation mutation, RuntimeStateOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CcReleasePrivateKeyPem)) return mutation;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(options.CcReleasePrivateKeyPem);
            var signature = key.SignData(Payload(mutation, mutation.NodeId, options.CcReleaseTrustDomain), HashAlgorithmName.SHA256);
            var signed = mutation with { CcReleaseProof = new(mutation.NodeId, options.CcReleaseTrustDomain, Convert.ToBase64String(signature)) };
            Verify(signed, options);
            return signed;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        { throw new InvalidOperationException("CC release signing key is invalid.", ex); }
    }

    internal static void Verify(RuntimeStateMutation mutation, RuntimeStateOptions options)
    {
        var proof = mutation.CcReleaseProof;
        if (proof is null || string.IsNullOrWhiteSpace(options.CcReleaseTrustDomain)
            || proof.TrustDomain != options.CcReleaseTrustDomain || proof.Issuer != mutation.NodeId
            || !options.CcReleaseAuthorities.TryGetValue(proof.Issuer, out var pem))
            throw new InvalidOperationException("CC release authority is not trusted.");
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (key.KeySize != 256 || !key.VerifyData(Payload(mutation, proof.Issuer, proof.TrustDomain),
                Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256))
                throw new InvalidOperationException("Invalid CC release authority signature.");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        { throw new InvalidOperationException("Invalid CC release authority signature.", ex); }
    }
}
