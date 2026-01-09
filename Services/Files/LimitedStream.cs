namespace LyWaf.Services.Files;

public class LimitedStream(Stream baseStream, long start, long length) : Stream
{
    private readonly Stream _baseStream = baseStream;
    private readonly long _start = start;
    private readonly long _length = length;
    private long _position = 0;

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;
        
        if (count > remaining) 
            count = (int)remaining;
            
        _baseStream.Position = _start + _position;
        var bytesRead = _baseStream.Read(buffer, offset, count);
        _position += bytesRead;
        
        return bytesRead;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _baseStream.Dispose();
        }
        base.Dispose(disposing);
    }
}