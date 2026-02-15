using System.Buffers;

namespace Plank2.Writing;

public struct BufferWriter : IBufferWriter<byte>
{
    byte[]? _buffer;
    int _written;

    public void Advance(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Advance count must be non-negative.");
        if (_buffer is null || count > _buffer.Length - _written)
            throw new InvalidOperationException("Advance exceeds available buffer capacity.");

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    internal ReadOnlySpan<byte> WrittenSpan
        => _buffer is null || _written == 0 ? [] : _buffer.AsSpan(0, _written);

    internal void Reset()
        => _written = 0;

    void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint), sizeHint, "Size hint must be non-negative.");

        if (sizeHint == 0)
            sizeHint = 1;

        if (_buffer is not null && sizeHint <= _buffer.Length - _written)
            return;

        var required = checked(_written + sizeHint);
        var newCapacity = _buffer is null || _buffer.Length == 0 ? 256 : _buffer.Length;
        while (newCapacity < required)
            newCapacity = checked(newCapacity * 2);

        Array.Resize(ref _buffer, newCapacity);
    }
}
