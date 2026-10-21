using System.Text.Json;
using LyWaf.Services.RuntimeState;
using Xunit;

namespace LyWaf.Tests;

public sealed class RuntimeStateBootstrapPagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootstrap-pages-" + Guid.NewGuid().ToString("N"));
    private static RuntimeStateBootstrapImage Image()
    {
        var image = new RuntimeStateBootstrapImage("cluster", "authority", "recipient", Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), new(["test"], [], [], true),
            Enumerable.Range(0, 400).Select(i => new RuntimeStateMutation("test", "key-" + i, "authority", i + 1,
                DateTimeOffset.UtcNow, null, false, JsonSerializer.SerializeToElement(new string('x', 1000)))).ToArray(),
            DateTimeOffset.UtcNow, "");
        return image with { Digest = image.ComputeDigest() };
    }

    [Fact]
    public void RestartDuplicateOutOfOrderAndMissingPagesPreserveOneImage()
    {
        var image = Image();
        var export = new RuntimeStateBootstrapPages(_root, "export");
        var manifest = export.Capture(image, default);
        var download = new RuntimeStateBootstrapPages(_root, "download-page");
        Assert.True(manifest.PageDigests.Length > 1);
        var last = export.Read(manifest, manifest.PageDigests.Length - 1)!;
        download.Save(manifest, last, default);
        download.Save(manifest, last, default);
        Assert.Throws<InvalidDataException>(() => download.Assemble(manifest));
        download = new RuntimeStateBootstrapPages(_root, "download-page");
        for (var i = 0; i < manifest.PageDigests.Length - 1; i++) download.Save(manifest, export.Read(manifest, i)!, default);
        Assert.Equal(image.Digest, download.Assemble(manifest).Digest);
    }

    [Fact]
    public void CorruptBytesWrongSnapshotAndChangedManifestAreRejected()
    {
        var export = new RuntimeStateBootstrapPages(_root, "export");
        var manifest = export.Capture(Image(), default);
        var page = export.Read(manifest, 0)!;
        var download = new RuntimeStateBootstrapPages(_root, "download-page");
        Assert.Throws<InvalidDataException>(() => download.Save(manifest, page with { SnapshotId = Guid.NewGuid().ToString("N") }, default));
        Assert.Throws<InvalidDataException>(() => download.Save(manifest, page with { Index = 1 }, default));
        var damaged = page.Data.ToArray();
        damaged[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => download.Save(manifest, page with { Data = damaged }, default));
        Assert.Null(download.Read(manifest, 0));
        for (var i = 0; i < manifest.PageDigests.Length; i++) download.Save(manifest, export.Read(manifest, i)!, default);
        Assert.Throws<InvalidDataException>(() => download.Assemble(manifest with { ContentDigest = new string('0', 64) }));
        Assert.Equal(manifest.Header.Digest, download.Assemble(manifest).Digest);
        var path = Directory.GetFiles(Path.Combine(_root, "runtime-sync"), "download-*.json")[0];
        File.WriteAllText(path, "{}");
        Assert.Throws<InvalidDataException>(() => new RuntimeStateBootstrapPages(_root, "download-page").Assemble(manifest));
        for (var i = 0; i < manifest.PageDigests.Length; i++)
            if (download.ReadDownloadCache(manifest, i) is null) download.Save(manifest, export.Read(manifest, i)!, default);
        Assert.Equal(manifest.Header.Digest, download.Assemble(manifest).Digest);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
