namespace Plank.Benchmarks.Published;

/// <summary>
/// A rewindable in-memory sink whose <see cref="Dispose(bool)"/> does nothing, so a writer that
/// closes its output stream leaves the buffer usable for the next iteration.
/// </summary>
/// <remarks>
/// This forwards to a <see cref="MemoryStream"/> rather than deriving from one, and that is the
/// whole point of the type. <see cref="MemoryStream"/> only takes its copy-straight-into-the-buffer
/// path in <c>Write(ReadOnlySpan&lt;byte&gt;)</c>, <c>WriteAsync(ReadOnlyMemory&lt;byte&gt;)</c>,
/// <c>Read(Span&lt;byte&gt;)</c> and <c>CopyTo</c> when <c>GetType() == typeof(MemoryStream)</c>,
/// because a subclass may have overridden the array overloads. A subclass therefore falls back to
/// <see cref="Stream.Write(ReadOnlySpan{byte})"/>, which rents an array from the pool, copies the
/// whole span into it, and only then calls <c>Write(byte[], int, int)</c>. That extra copy lands
/// inside the timed region of every write benchmark - on the 869 MiB synthetic case it measured
/// about 12 ms of a 68 ms <c>RowGroup.Write</c>, charged to the library under test rather than to
/// the harness. The field below is exactly a <see cref="MemoryStream"/>, so every forwarded call
/// takes the fast path.
/// </remarks>
sealed class NonClosingMemoryStream : Stream
{
    readonly MemoryStream _inner = new();

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public void Reset()
    {
        _inner.Position = 0;
        _inner.SetLength(0);
    }

    public byte[] ToArray() => _inner.ToArray();

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override int ReadByte() => _inner.ReadByte();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => _inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    public override void WriteByte(byte value) => _inner.WriteByte(value);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    public override void CopyTo(Stream destination, int bufferSize) => _inner.CopyTo(destination, bufferSize);

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        => _inner.CopyToAsync(destination, bufferSize, cancellationToken);

    protected override void Dispose(bool disposing)
    {
    }
}
