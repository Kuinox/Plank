using System.Runtime.InteropServices;

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
        ValidateRange(offset, destination.Length);
        _bytes.Span.Slice((int)offset, destination.Length).CopyTo(destination);
    }

    internal bool TryBorrow(ulong offset, int length, out ReadOnlyMemory<byte> bytes)
    {
        ValidateRange(offset, length);
        if (!MemoryMarshal.TryGetArray(_bytes, out var segment) || segment.Array is null)
        {
            bytes = default;
            return false;
        }

        // Construct from the array so the borrowed slice does not depend on a custom MemoryManager's lifetime.
        bytes = new ReadOnlyMemory<byte>(segment.Array,
            checked(segment.Offset + (int)offset), length);
        return true;
    }

    void ValidateRange(ulong offset, int length)
    {
        if (length < 0 || offset > int.MaxValue || (long)(int)offset + length > _bytes.Length)
            throw new CorruptParquetException(
                $"Attempted to read {length} bytes at offset {offset} but the source is only {_bytes.Length} bytes long.");
    }
}
