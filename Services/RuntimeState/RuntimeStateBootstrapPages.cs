using System.Security.Cryptography;
using System.Text.Json;

namespace LyWaf.Services.RuntimeState;

public sealed record RuntimeStateBootstrapPage(string SnapshotId, string ImageDigest, int Index, byte[] Data);

public sealed record RuntimeStateBootstrapManifest(RuntimeStateBootstrapImage Header, long ByteLength,
    string ContentDigest, string[] PageDigests, string? ExportPolicyDigest = null)
{
    public const int PageBytes = 256 * 1024;
    internal const long MaximumSnapshotBytes = 64L * 1024 * 1024;
    internal static string HashBytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    internal void Validate()
    {
        if (Header is null || Header.Items is not { Length: 0 } || ByteLength <= 0 || ByteLength > MaximumSnapshotBytes
            || PageDigests is null || PageDigests.Length != (ByteLength + PageBytes - 1) / PageBytes
            || !IsDigest(ContentDigest) || !IsDigest(Header.Digest) || PageDigests.Any(d => !IsDigest(d)))
            throw new InvalidDataException("Invalid bootstrap manifest");
        // Validate metadata without treating the deliberately empty header as the complete image.
        (Header with { Digest = Header.ComputeDigest() }).Validate();
    }
    internal void ValidatePage(RuntimeStateBootstrapPage page)
    {
        Validate();
        if (page.SnapshotId != Header.RequestId || page.ImageDigest != Header.Digest || page.Index < 0
            || page.Index >= PageDigests.Length || page.Data is null
            || page.Data.Length != Math.Min(PageBytes, ByteLength - (long)page.Index * PageBytes)
            || HashBytes(page.Data) != PageDigests[page.Index])
            throw new InvalidDataException("Bootstrap page identity, length or digest mismatch");
    }
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

// Pages are immutable for a manifest. Save fsyncs each page before the manifest can be published.
internal sealed class RuntimeStateBootstrapPages(string directory, string direction, RuntimeStateOptions? capacityOptions = null)
{
    private readonly RuntimeStateSyncFiles _files = new(directory, capacityOptions);
    private string Path(RuntimeStateBootstrapManifest manifest, int index) => _files.PathFor(
        direction + "-" + manifest.Header.RequestId + "-" + manifest.Header.NewEpoch + "-" + index,
        manifest.Header.ClusterId, manifest.Header.AuthorityNodeId, manifest.Header.RecipientNodeId);

    internal RuntimeStateBootstrapManifest Capture(RuntimeStateBootstrapImage image, CancellationToken token)
    {
        image.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(image, RuntimeStateSyncProtocol.Json);
        if (bytes.LongLength > RuntimeStateBootstrapManifest.MaximumSnapshotBytes)
            throw new IOException("Bootstrap snapshot exceeds durable staging budget");
        var chunks = bytes.Chunk(RuntimeStateBootstrapManifest.PageBytes).ToArray();
        var manifest = new RuntimeStateBootstrapManifest(image with { Items = [] }, bytes.LongLength,
            RuntimeStateBootstrapManifest.HashBytes(bytes), chunks.Select(RuntimeStateBootstrapManifest.HashBytes).ToArray());
        manifest.Validate();
        for (var i = 0; i < chunks.Length; i++) Save(manifest, new(image.RequestId, image.Digest, i, chunks[i]), token);
        return manifest;
    }

    internal RuntimeStateBootstrapPage? Read(RuntimeStateBootstrapManifest manifest, int index)
    {
        manifest.Validate();
        if (index < 0 || index >= manifest.PageDigests.Length) throw new InvalidDataException("Invalid bootstrap page index");
        var page = _files.Read<RuntimeStateBootstrapPage>(Path(manifest, index));
        if (page is not null) manifest.ValidatePage(page);
        return page;
    }

    internal void PruneOtherSnapshots(RuntimeStateBootstrapManifest manifest)
    {
        manifest.Validate();
        var active = Path(manifest, 0);
        var suffix = System.IO.Path.GetFileName(active)[^69..]; // stream hash plus .json
        var prefix = direction + "-" + manifest.Header.RequestId + "-" + manifest.Header.NewEpoch + "-";
        foreach (var file in Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(active)!, direction + "-*-" + suffix))
            if (!System.IO.Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal)) File.Delete(file);
    }

    internal void DeleteCompletedDownload(RuntimeStateBootstrapManifest manifest, Action? afterPage = null)
    {
        if (direction != "download-page") throw new InvalidOperationException("Not a download cache");
        manifest.Validate();
        for (var index = 0; index < manifest.PageDigests.Length; index++)
        {
            File.Delete(Path(manifest, index));
            afterPage?.Invoke();
        }
    }

    internal RuntimeStateBootstrapPage? ReadDownloadCache(RuntimeStateBootstrapManifest manifest, int index)
    {
        // Only disposable download data can be repaired this way, never an authority's durable offer.
        if (direction != "download-page") throw new InvalidOperationException("Not a download cache");
        manifest.Validate();
        if (index < 0 || index >= manifest.PageDigests.Length) throw new InvalidDataException("Invalid bootstrap page index");
        try { return Read(manifest, index); }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            File.Delete(Path(manifest, index));
            return null;
        }
    }

    internal void Save(RuntimeStateBootstrapManifest manifest, RuntimeStateBootstrapPage page, CancellationToken token)
    {
        manifest.ValidatePage(page);
        var existing = Read(manifest, page.Index);
        if (existing is not null) return;
        _files.Save(Path(manifest, page.Index), page, token);
    }

    internal RuntimeStateBootstrapImage Assemble(RuntimeStateBootstrapManifest manifest)
    {
        manifest.Validate();
        using var bytes = new MemoryStream();
        for (var i = 0; i < manifest.PageDigests.Length; i++)
        {
            var page = Read(manifest, i) ?? throw new InvalidDataException("Incomplete bootstrap snapshot");
            bytes.Write(page.Data);
        }
        var content = bytes.ToArray();
        if (content.LongLength != manifest.ByteLength || RuntimeStateBootstrapManifest.HashBytes(content) != manifest.ContentDigest)
            throw new InvalidDataException("Bootstrap content digest mismatch");
        var image = JsonSerializer.Deserialize<RuntimeStateBootstrapImage>(content, RuntimeStateSyncProtocol.Json)
            ?? throw new InvalidDataException("Missing bootstrap image");
        image.Validate();
        if (RuntimeStateBootstrapImage.Hash(image with { Items = [] }) != RuntimeStateBootstrapImage.Hash(manifest.Header))
            throw new InvalidDataException("Bootstrap manifest does not describe image");
        return image;
    }
}
