namespace LyWaf.Utils;

/// <summary>
/// 包装 Stream，精确统计写入/读取字节数
/// </summary>
public class CountingStream : Stream
{
    private readonly Stream _inner;
    private long _bytesWritten;
    private long _bytesRead;

    public CountingStream(Stream inner)
    {
        _inner = inner;
    }

    public long BytesWritten => _bytesWritten;
    public long BytesRead => _bytesRead;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Interlocked.Add(ref _bytesRead, n);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var n = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        Interlocked.Add(ref _bytesRead, n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken);
        Interlocked.Add(ref _bytesRead, n);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Interlocked.Add(ref _bytesWritten, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer, offset, count, cancellationToken);
        Interlocked.Add(ref _bytesWritten, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken);
        Interlocked.Add(ref _bytesWritten, buffer.Length);
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        // 不 dispose inner stream，由原始持有者管理
        base.Dispose(disposing);
    }
}
