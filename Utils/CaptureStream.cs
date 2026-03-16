using System.Text;

namespace LyWaf.Utils;

/// <summary>
/// 透传捕获流：所有读取/写入同时转发到原始流并拷贝到内部缓冲区。
/// 用于 WAF 拦截路径捕获请求体和响应体，不影响正常的流读写。
/// </summary>
public class CaptureStream : Stream
{
    private readonly Stream _inner;
    private readonly MemoryStream _buffer = new();
    private readonly int _maxCapture;
    private bool _captureFull;

    public CaptureStream(Stream inner, int maxCapture = 64 * 1024)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxCapture = maxCapture;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    // ── Write 捕获 ──

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        AppendToBuffer(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer, offset, count, cancellationToken);
        AppendToBuffer(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken);
        if (!_captureFull && _buffer.Length < _maxCapture)
        {
            var bytesToWrite = (int)Math.Min(buffer.Length, _maxCapture - _buffer.Length);
            _buffer.Write(buffer.Span[..bytesToWrite]);
            if (_buffer.Length >= _maxCapture) _captureFull = true;
        }
    }

    // ── Read 捕获 ──

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _inner.Read(buffer, offset, count);
        if (bytesRead > 0)
            AppendToBuffer(buffer, offset, bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (bytesRead > 0)
            AppendToBuffer(buffer, offset, bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
        if (bytesRead > 0 && !_captureFull && _buffer.Length < _maxCapture)
        {
            var bytesToCapture = (int)Math.Min(bytesRead, _maxCapture - _buffer.Length);
            _buffer.Write(buffer.Span[..bytesToCapture]);
            if (_buffer.Length >= _maxCapture) _captureFull = true;
        }
        return bytesRead;
    }

    // ── 公共方法 ──

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <summary>
    /// 获取已捕获的内容
    /// </summary>
    public string? GetCapturedBody(int maxBytes = 0)
    {
        if (_buffer.Length == 0) return null;
        if (maxBytes <= 0) maxBytes = _maxCapture;

        _buffer.Position = 0;
        var len = (int)Math.Min(_buffer.Length, maxBytes);
        var bytes = new byte[len];
        _ = _buffer.Read(bytes, 0, len);
        var content = Encoding.UTF8.GetString(bytes);

        if (_buffer.Length > maxBytes)
            content += $"\n[... 截断，原始大小: {_buffer.Length} 字节 ...]";

        return content;
    }

    // ── 内部 ──

    private void AppendToBuffer(byte[] buffer, int offset, int count)
    {
        if (_captureFull) return;
        var bytesToWrite = (int)Math.Min(count, _maxCapture - _buffer.Length);
        if (bytesToWrite > 0)
            _buffer.Write(buffer, offset, bytesToWrite);
        if (_buffer.Length >= _maxCapture) _captureFull = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer.Dispose();
        }
        // 不 dispose _inner，由调用方管理
        base.Dispose(disposing);
    }
}
