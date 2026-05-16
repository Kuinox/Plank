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
        if (offset > int.MaxValue || (long)(int)offset + destination.Length > _bytes.Length)
            throw new CorruptParquetException($"Attempted to read {destination.Length} bytes at offset {offset} but the source is only {_bytes.Length} bytes long.");

        _bytes.Span.Slice((int)offset, destination.Length).CopyTo(destination);
    }
}
