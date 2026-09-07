using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LyWaf.Plugins.Core;
using LyWaf.Plugins.Packaging;
using Xunit;

namespace LyWaf.Tests;

public sealed class PluginPackageManagerTests
{
    [Fact]
    public async Task InstallAndRollbackUseVerifiedVersionPointers()
    {
        var root = CreateRoot();
        try
        {
            var manager = CreateManager(root);
            await manager.InstallAsync(CreatePackage("sample", "1.0.0", [1, 2, 3]), null, default);
            await manager.InstallAsync(CreatePackage("sample", "2.0.0", [4, 5, 6]), null, default);
            var current = PluginPackageManager.ReadPointer(Path.Combine(root, "sample"));
            Assert.Equal("2.0.0", current!.Version);
            Assert.Equal("1.0.0", current.PreviousVersion);

            var rollback = manager.Rollback("sample");
            Assert.True(rollback.RequiresRestart);
            Assert.Equal("1.0.0", PluginPackageManager.ReadPointer(Path.Combine(root, "sample"))!.Version);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InstallRejectsModifiedPayload()
    {
        var root = CreateRoot();
        try
        {
            var manager = CreateManager(root);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                manager.InstallAsync(CreatePackage("sample", "1.0.0", [1, 2, 3], "00"), null, default));
            Assert.False(Directory.Exists(Path.Combine(root, "sample")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SignedModeRejectsUnsignedPackage()
    {
        var root = CreateRoot();
        try
        {
            var manager = CreateManager(root, requireSignature: true);
            await Assert.ThrowsAsync<CryptographicException>(() =>
                manager.InstallAsync(CreatePackage("sample", "1.0.0", [1]), null, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrustedSignatureIsAccepted()
    {
        var root = CreateRoot();
        using var rsa = RSA.Create(2048);
        try
        {
            var options = new PluginOptions { PluginDirectory = root, RequireSignedPackages = true };
            options.TrustedSigningKeys["test"] = rsa.ExportSubjectPublicKeyInfoPem();
            var manager = new PluginPackageManager(new StaticOptionsMonitor<PluginOptions>(options));
            var result = await manager.InstallAsync(CreatePackage("signed", "1.0.0", [7, 8], signer: rsa), null, default);
            Assert.Equal("signed", result.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InstalledManifestMustMatchPluginMetadata()
    {
        var root = CreateRoot();
        try
        {
            var manifest = new PluginPackageManifest
            {
                Id = "sample", Version = "2.0.0", EntryAssembly = "plugin.dll", DataVersion = 3,
                FrontendVersion = "2.0.0", Files = new() { ["plugin.dll"] = "00" }
            };
            File.WriteAllText(Path.Combine(root, "plugin.json"), JsonSerializer.Serialize(manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Throws<InvalidDataException>(() => PluginPackageManager.ValidateLoadedMetadata(
                Path.Combine(root, "plugin.dll"), new PluginMetadata { Id = "sample", Name = "Sample", Version = "1.0.0" }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PackageCannotCarryHostContractAssembly()
    {
        var root = CreateRoot();
        try
        {
            var manager = CreateManager(root);
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(
                CreatePackage("sample", "1.0.0", [1], fileName: "LyWaf.PluginSdk.dll"), null, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExtractedPayloadHasIndependentSizeLimit()
    {
        var root = CreateRoot();
        try
        {
            var options = new PluginOptions { PluginDirectory = root, MaxExtractedPackageBytes = 1 };
            var manager = new PluginPackageManager(new StaticOptionsMonitor<PluginOptions>(options));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                manager.InstallAsync(CreatePackage("sample", "1.0.0", [1, 2]), null, default));
        }
        finally { Directory.Delete(root, true); }
    }

    private static PluginPackageManager CreateManager(string root, bool requireSignature = false) =>
        new(new StaticOptionsMonitor<PluginOptions>(new PluginOptions
        {
            PluginDirectory = root,
            RequireSignedPackages = requireSignature
        }));

    private static MemoryStream CreatePackage(string id, string version, byte[] payload, string? hash = null,
        RSA? signer = null, string fileName = "plugin.dll")
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var dll = archive.CreateEntry(fileName);
            using (var output = dll.Open()) output.Write(payload);
            var hashes = new Dictionary<string, string> { [fileName] = hash ?? Convert.ToHexString(SHA256.HashData(payload)) };
            var manifest = new PluginPackageManifest
            {
                Id = id, Version = version, EntryAssembly = fileName, DataVersion = 1,
                FrontendVersion = version,
                Files = hashes,
                SigningKeyId = signer == null ? null : "test",
                Signature = signer == null ? null : Convert.ToBase64String(signer.SignData(
                    CanonicalPayload(id, version, hashes), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            };
            var manifestEntry = archive.CreateEntry("plugin.json");
            using var manifestStream = manifestEntry.Open();
            JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        stream.Position = 0;
        return stream;
    }

    private static byte[] CanonicalPayload(string id, string version, Dictionary<string, string> files)
    {
        var lines = new List<string>
        {
            $"id:{id}", $"version:{version}", "entry:plugin.dll", "sdk:1.0.0", "data:1", $"frontend:{version}"
        };
        lines.AddRange(files.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"file:{item.Key}:{item.Value.ToUpperInvariant()}"));
        return System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lywaf-package-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
