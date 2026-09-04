namespace LyWaf.Services.ResponseCaching;

/// <summary>
/// 先把响应流式写入临时文件；超过缓存上限后把已写部分复制到客户端并切换为直通，避免大响应驻留内存或无限占用临时磁盘。
/// </summary>
internal sealed class BoundedCaptureStream(Stream file, Stream downstream, long limit) : Stream
{
    public bool Overflowed { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => file.Length;
    public override long Position { get => file.Position; set => throw new NotSupportedException(); }

    public override void Flush() => throw new NotSupportedException("Use FlushAsync");
    public override Task FlushAsync(CancellationToken cancellationToken) => Overflowed ? downstream.FlushAsync(cancellationToken) : file.FlushAsync(cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Overflowed && file.Length + buffer.Length <= limit)
        {
            await file.WriteAsync(buffer, cancellationToken);
            return;
        }
        if (!Overflowed)
        {
            Overflowed = true;
            await file.FlushAsync(cancellationToken);
            file.Position = 0;
            await file.CopyToAsync(downstream, cancellationToken);
        }
        await downstream.WriteAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
