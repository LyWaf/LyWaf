using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

public sealed record RuntimeStateSyncBatch(string ClusterId, string SenderNodeId, string ReceiverNodeId,
    string Epoch, long Sequence, IReadOnlyList<RuntimeStateMutation> Mutations, string Digest,
    RuntimeStateBootstrapRequest? Bootstrap = null);

public sealed record RuntimeStateSyncAck(string ClusterId, string SenderNodeId, string ReceiverNodeId,
    string Epoch, long Sequence, string Digest, string Status = "Applied", long ExpectedSequence = 0,
    RuntimeStateBootstrapImage? Snapshot = null, RuntimeStateBootstrapManifest? Manifest = null,
    RuntimeStateBootstrapPage? Page = null);

public static class RuntimeStateSyncProtocol
{
    public const string Operation = "runtime.sync";
    public const int MaximumBatchBytes = 8 * 1024 * 1024;
    public const int MaximumHistory = 10000;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ComputeDigest(RuntimeStateSyncBatch batch) => batch.Bootstrap is not null
        ? Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence, batch.Mutations, batch.Bootstrap }, Json)))
        : Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            batch.ClusterId, batch.SenderNodeId, batch.ReceiverNodeId, batch.Epoch, batch.Sequence, batch.Mutations
        }, Json)));

    public static void Validate(RuntimeStateSyncBatch batch)
    {
        if (!ValidIdentity(batch.ClusterId) || !ValidIdentity(batch.SenderNodeId) || !ValidIdentity(batch.ReceiverNodeId)
            || !Guid.TryParseExact(batch.Epoch, "N", out _) || batch.Sequence is <= 0 or >= long.MaxValue
            || batch.Mutations is null || batch.Mutations.Count > 32 || batch.Mutations.Any(x => x is null)
            || (batch.Bootstrap is null ? batch.Mutations.Count == 0 : batch.Mutations.Count != 0
                || batch.Bootstrap.Phase is not ("fetch" or "commit" or "probe" or "page") || !Guid.TryParseExact(batch.Bootstrap.RequestId, "N", out _)
                || (batch.Bootstrap.Phase == "page" && batch.Bootstrap.PageIndex is not >= 0))
            || batch.Digest != ComputeDigest(batch)
            || JsonSerializer.SerializeToUtf8Bytes(batch, Json).Length > MaximumBatchBytes)
            throw new InvalidDataException("Invalid runtime sync batch");
    }

    internal static bool ValidIdentity(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
