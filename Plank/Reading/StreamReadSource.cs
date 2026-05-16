namespace Plank.Reading;

public sealed class StreamReadSource : IParquetReadSource
{
    Stream _stream;
    readonly object _gate = new();

    public StreamReadSource(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new InvalidOperationException("Reader stream must be readable.");
        if (!stream.CanSeek)
            throw new InvalidOperationException("Reader stream must be seekable.");

        _stream = stream;
    }

    public void Reset(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new InvalidOperationException("Reader stream must be readable.");
        if (!stream.CanSeek)
            throw new InvalidOperationException("Reader stream must be seekable.");

        _stream = stream;
    }

    public ulong Length
        => (ulong)_stream.Length;

    public void ReadExactly(ulong offset, Span<byte> destination)
    {
        lock (_gate)
        {
            _stream.Position = (long)offset;
            _stream.ReadExactly(destination);
        }
    }
}
