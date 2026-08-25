namespace Plank.Writing;

sealed class StreamParquetSource : IParquetReadWriteSource
{
    internal Stream? Stream;
    ulong _position;

    internal StreamParquetSource(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Stream = stream;
        _position = stream.CanSeek ? checked((ulong)stream.Position) : 0;
    }

    public ulong Length
        => checked((ulong)GetStream().Length);

    public void Open(ReadOnlySpan<byte> path, FileMode mode)
        => throw new NotSupportedException("A stream adapter cannot open a file path.");

    public void Close()
    {
        Stream?.Dispose();
        Stream = null;
        _position = 0;
    }

    public void ReadExactly(ulong offset, Span<byte> destination)
    {
        var stream = GetStream();
        if (!stream.CanRead || !stream.CanSeek)
            throw new NotSupportedException("The stream does not support random-access reads.");
        stream.Position = checked((long)offset);
        stream.ReadExactly(destination);
    }

    public void Write(ulong offset, ReadOnlySpan<byte> source)
    {
        var stream = GetStream();
        if (!stream.CanWrite)
            throw new NotSupportedException("The stream does not support writes.");
        if (stream.CanSeek)
            stream.Position = checked((long)offset);
        else if (offset != _position)
            throw new NotSupportedException("The stream does not support non-sequential writes.");
        stream.Write(source);
        _position = checked(offset + (uint)source.Length);
    }

    public void SetLength(ulong length)
    {
        var stream = GetStream();
        if (!stream.CanWrite || !stream.CanSeek)
            throw new NotSupportedException("The stream does not support length changes.");
        stream.SetLength(checked((long)length));
    }

    public void Flush()
        => GetStream().Flush();

    public void Dispose()
        => Close();

    Stream GetStream()
        => Stream ?? throw new ObjectDisposedException(nameof(StreamParquetSource));
}
