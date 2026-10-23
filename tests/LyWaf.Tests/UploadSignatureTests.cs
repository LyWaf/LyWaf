using System.Security.Cryptography;
using System.Text;
using LyWaf.Plugins.Sys.HttpUpload;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class UploadSignatureTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static string Sign(string text) => Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(Key), Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void ValidSignatureAndInclusiveWindow()
    {
        var signature = Sign($"test.txt\n5\n1000\n{Nonce}");
        foreach (var now in new long[] { 700, 1000, 1300 })
        {
            Assert.True(UploadSignature.Verify(Key, "test.txt", "5", "1000", Nonce, signature, now, 300, out var bytes));
            Assert.Equal(5, bytes);
        }
        Assert.False(UploadSignature.Verify(Key, "test.txt", "5", "1000", Nonce, signature, 1301, 300, out _));
        Assert.False(UploadSignature.Verify(Key, "test.txt", "5", "1000", Nonce, signature, 699, 300, out _));
    }

    [Theory]
    [InlineData("other.txt", "5", "1000", Nonce)]
    [InlineData("test.txt", "6", "1000", Nonce)]
    [InlineData("test.txt", "05", "1000", Nonce)]
    [InlineData("test.txt", "5", "1001", Nonce)]
    [InlineData("test.txt", "5", "1000", "short")]
    public void MetadataTamperingRejected(string filename, string size, string timestamp, string nonce)
    {
        Assert.False(UploadSignature.Verify(Key, filename, size, timestamp, nonce,
            Sign($"test.txt\n5\n1000\n{Nonce}"), 1000, 300, out _));
    }

    [Fact]
    public async Task NonceIsAtomicPersistentAndExpires()
    {
        var directory = Path.Combine(Path.GetTempPath(), "upload-nonce-test-" + Guid.NewGuid());
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                UploadSignature.Consume(directory, Nonce, 1000))));
            Assert.Single(results, x => x);
            Assert.Single(Directory.GetFiles(directory, "*.nonce"));
            Assert.False(UploadSignature.Consume(directory, Nonce, 1001));
            Assert.True(UploadSignature.Consume(directory, Nonce, 8201));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MultipartMetadataPrecedesStreamingFile()
    {
        using var body = new MultipartFormDataContent();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["filename"] = "test.txt", ["size"] = "5", ["timestamp"] = "1000",
            ["nonce"] = Nonce, ["signature"] = Sign($"test.txt\n5\n1000\n{Nonce}"),
        }) body.Add(new StringContent(value), key);
        body.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("hello")), "file", "test.txt");
        var context = new DefaultHttpContext();
        context.Request.ContentType = body.Headers.ContentType!.ToString();
        context.Request.Body = await body.ReadAsStreamAsync();
        var form = await UploadForm.ReadAsync(context.Request, 1024, default);
        using var reader = new StreamReader(form.File);
        Assert.Equal("hello", await reader.ReadToEndAsync());
        Assert.Null(await form.Reader.ReadNextSectionAsync());
    }
}
