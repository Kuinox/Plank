namespace Plank.Reading;

public sealed class MemoryReadSource : IParquetReadSource
{
    readonly ReadOnlyMemory<byte> _bytes;

    public MemoryReadSource(ReadOnlyMemory<byte> bytes)
        => _bytes = bytes;

    public ulong Length
        => (ulong)_bytes.Length;

    public void ReadExactly(ulong offset, Span<byte> destination)
    {
        if (offset > (ulong)_bytes.Length - (ulong)destination.Length)
            throw new CorruptParquetException($"Attempted to read {destination.Length} bytes at offset {offset} but the source is only {_bytes.Length} bytes long.");

        _bytes.Span.Slice(checked((int)offset), destination.Length).CopyTo(destination);
    }
}
