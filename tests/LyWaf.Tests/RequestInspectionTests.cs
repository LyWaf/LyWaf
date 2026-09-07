using System.Text;
using System.IO.Compression;
using LyWaf.Services.Protect;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LyWaf.Tests;

public sealed class RequestInspectionTests
{
    [Fact]
    public async Task RecursivelyDecodesQueryCandidatesWithinConfiguredDepth()
    {
        var service = CreateService(new ProtectOptions { InspectionDecodeDepth = 3 });
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?sql=%2527%2520OR%25201%253D1&html=%26lt%3Bscript%26gt%3B");

        var snapshot = await service.InspectAsync(context);

        Assert.Contains("' OR 1=1", snapshot.QueryValues);
        Assert.Contains("<script>", snapshot.QueryValues);
    }

    [Fact]
    public async Task ExtractsNestedJsonAndDecodedBase64AndHexValues()
    {
        var body = """{"outer":{"payload":"PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==","hex":"554e494f4e2053454c454354"}}""";
        var context = CreateBodyContext(body, "application/json");
        var service = CreateService(new ProtectOptions());

        var snapshot = await service.InspectAsync(context);

        Assert.Contains("outer", snapshot.BodyValues);
        Assert.Contains("payload", snapshot.BodyValues);
        Assert.Contains("<script>alert(1)</script>", snapshot.BodyValues);
        Assert.Contains("UNION SELECT", snapshot.BodyValues);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task StopsReadingAtInspectionLimitAndMarksSnapshotTruncated()
    {
        var context = CreateBodyContext(new string('a', 4096), "text/plain");
        var service = CreateService(new ProtectOptions
        {
            InspectionMaxBodyBytes = 1024,
            InspectionMaxValueLength = 1024
        });

        var snapshot = await service.InspectAsync(context);

        Assert.True(snapshot.BodyTruncated);
        Assert.Contains(snapshot.BodyValues, value => value.Length == 1024);
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task ReusesOneSnapshotForTheSameRequest()
    {
        var context = CreateBodyContext("first", "text/plain");
        var service = CreateService(new ProtectOptions());
        var first = await service.InspectAsync(context);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("second"));

        var second = await service.InspectAsync(context);

        Assert.Same(first, second);
        Assert.Contains("first", second.BodyValues);
        Assert.DoesNotContain("second", second.BodyValues);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task InspectsSupportedCompressedBodies(string encoding)
    {
        var content = Encoding.UTF8.GetBytes("payload=UNION%20SELECT");
        var compressed = Compress(content, encoding);
        var context = CreateBodyContext(compressed, "application/x-www-form-urlencoded");
        context.Request.Headers.ContentEncoding = encoding;

        var snapshot = await CreateService(new ProtectOptions()).InspectAsync(context);

        Assert.Contains("UNION SELECT", snapshot.BodyValues);
        Assert.Empty(snapshot.ProtocolAnomalies);
    }

    [Fact]
    public async Task MarksDecompressionBombAsTruncated()
    {
        var compressed = Compress(Encoding.UTF8.GetBytes(new string('x', 8192)), "gzip");
        var context = CreateBodyContext(compressed, "text/plain");
        context.Request.Headers.ContentEncoding = "gzip";

        var snapshot = await CreateService(new ProtectOptions { InspectionMaxBodyBytes = 1024 })
            .InspectAsync(context);

        Assert.True(snapshot.BodyTruncated);
        Assert.Contains(snapshot.ProtocolAnomalies, value => value.Contains("超过", StringComparison.Ordinal));
        Assert.All(snapshot.BodyValues, value => Assert.True(value.Length <= 1024));
    }

    [Fact]
    public async Task ExtractsXmlAndMultipartFieldsWithoutResolvingEntities()
    {
        var xml = CreateBodyContext("<root token=\"secret\"><item>SELECT</item></root>", "application/xml");
        var xmlSnapshot = await CreateService(new ProtectOptions()).InspectAsync(xml);
        Assert.Contains("token", xmlSnapshot.BodyValues);
        Assert.Contains("secret", xmlSnapshot.BodyValues);
        Assert.Contains("SELECT", xmlSnapshot.BodyValues);

        const string boundary = "test-boundary";
        var multipartBody = $"--{boundary}\r\nContent-Disposition: form-data; name=\"field\"\r\n\r\n<script>\r\n--{boundary}--\r\n";
        var multipart = CreateBodyContext(multipartBody, $"multipart/form-data; boundary={boundary}");
        var multipartSnapshot = await CreateService(new ProtectOptions()).InspectAsync(multipart);
        Assert.Contains("field", multipartSnapshot.BodyValues);
        Assert.Contains(multipartSnapshot.BodyValues, value => value.Contains("<script>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportsConflictingFramingAndNormalizesUnicode()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.TransferEncoding = "chunked";
        context.Request.Headers["Content-Length"] = "10, 11";
        context.Request.QueryString = new QueryString("?value=%EF%BC%B5%EF%BC%AE%EF%BC%A9%EF%BC%AF%EF%BC%AE");

        var snapshot = await CreateService(new ProtectOptions()).InspectAsync(context);

        Assert.Contains("UNION", snapshot.QueryValues);
        Assert.Contains(snapshot.ProtocolAnomalies, value => value.Contains("Transfer-Encoding", StringComparison.Ordinal));
        Assert.Contains(snapshot.ProtocolAnomalies, value => value.Contains("Content-Length", StringComparison.Ordinal));
    }

    private static RequestInspectionService CreateService(ProtectOptions options) =>
        new(new StaticOptionsMonitor<ProtectOptions>(options));

    private static DefaultHttpContext CreateBodyContext(string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = contentType;
        return context;
    }

    private static DefaultHttpContext CreateBodyContext(byte[] body, string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = contentType;
        return context;
    }

    private static byte[] Compress(byte[] content, string encoding)
    {
        using var output = new MemoryStream();
        using (Stream compressor = encoding switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        })
            compressor.Write(content);
        return output.ToArray();
    }
}
