namespace Plank.Writing;

sealed class BufferWriterOutputStream : Stream
{
    BufferWriter _writer;

    internal BufferWriterOutputStream(BufferWriter writer)
        => _writer = writer;

    internal BufferWriter Consume()
        => _writer;

    public override bool CanRead
        => false;

    public override bool CanSeek
        => false;

    public override bool CanWrite
        => true;

    public override long Length
        => _writer.WrittenLength;

    public override long Position
    {
        get => _writer.WrittenLength;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        var destination = _writer.GetSpan(count);
        buffer.AsSpan(offset, count).CopyTo(destination);
        _writer.Advance(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var destination = _writer.GetSpan(buffer.Length);
        buffer.CopyTo(destination);
        _writer.Advance(buffer.Length);
    }
}
