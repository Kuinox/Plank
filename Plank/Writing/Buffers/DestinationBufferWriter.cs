using System.Buffers;

namespace Plank.Writing;

internal struct DestinationBufferWriter : IBufferWriter<byte>
{
    readonly Memory<byte> _buffer;
    int _written;

    internal DestinationBufferWriter(Memory<byte> buffer)
    {
        _buffer = buffer;
        _written = 0;
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var next = checked(_written + count);
        if ((uint)next > (uint)_buffer.Length)
            throw new InvalidOperationException("Encoded buffer capacity exceeded.");
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
            throw new InvalidOperationException("Encoded buffer capacity exceeded.");

        return _buffer.Slice(_written, remaining);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
        => GetMemory(sizeHint).Span;
}
