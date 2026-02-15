using System.Buffers;
using System.Buffers.Binary;

namespace Plank.Writing;

internal sealed class VariableSizeBuffer : IBufferWriter<byte>
{
    readonly Memory<byte> _buffer;
    readonly string _columnName;
    internal int _written;

    internal VariableSizeBuffer(Memory<byte> buffer, string columnName)
    {
        _buffer = buffer;
        _columnName = columnName;
        _written = 0;
    }

    internal void OverwriteInt32(int offset, int value)
    {
        if ((uint)offset > (uint)(_written - sizeof(int)))
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset is outside written content.");

        BinaryPrimitives.WriteInt32LittleEndian(_buffer.Span.Slice(offset, sizeof(int)), value);
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var next = checked(_written + count);
        if ((uint)next > (uint)_buffer.Length)
            throw new InvalidOperationException(
                $"Column '{_columnName}' requires more than {_buffer.Length} bytes but encoded buffer capacity is {_buffer.Length}.");
        _written = next;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint), sizeHint, "Size hint must be non-negative.");

        if (sizeHint == 0)
            sizeHint = 1;

        var remaining = _buffer.Length - _written;
        if (sizeHint > remaining)
            throw new InvalidOperationException(
                $"Column '{_columnName}' requires more than {_buffer.Length} bytes but encoded buffer capacity is {_buffer.Length}.");

        return _buffer.Slice(_written, remaining);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
        => GetMemory(sizeHint).Span;
}
