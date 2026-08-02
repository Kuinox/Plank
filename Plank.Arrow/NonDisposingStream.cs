namespace Plank.Arrow;

sealed class NonDisposingStream : Stream
{
    readonly Stream _inner;

    public NonDisposingStream(Stream inner)
        => _inner = inner;

    public override bool CanRead
        => _inner.CanRead;

    public override bool CanSeek
        => _inner.CanSeek;

    public override bool CanWrite
        => _inner.CanWrite;

    public override long Length
        => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush()
        => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer)
        => _inner.Read(buffer);

    public override long Seek(long offset, SeekOrigin origin)
        => _inner.Seek(offset, origin);

    public override void SetLength(long value)
        => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
        => _inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer)
        => _inner.Write(buffer);

    protected override void Dispose(bool disposing)
    {
    }
}
