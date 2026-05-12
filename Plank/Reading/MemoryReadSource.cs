namespace Plank.Reading;

public sealed class MemoryReadSource : IParquetReadSource
{
    readonly ReadOnlyMemory<byte> _bytes;

    public MemoryReadSource(ReadOnlyMemory<byte> bytes)
        => _bytes = bytes;

    public long Length
        => _bytes.Length;

    public void ReadExactly(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > _bytes.Length - destination.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Read range is outside the source.");

        _bytes.Span.Slice(checked((int)offset), destination.Length).CopyTo(destination);
    }
}
