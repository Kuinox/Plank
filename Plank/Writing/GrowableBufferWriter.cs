using System.Buffers;

namespace Plank.Writing;

sealed class GrowableBufferWriter : IBufferWriter<byte>, IDisposable
{
    byte[] _buffer;
    int _written;
    int _maxLength;
    bool _disposed;

    internal GrowableBufferWriter()
    {
        _buffer = [];
        _written = 0;
        _maxLength = int.MaxValue;
        _disposed = false;
    }

    internal int WrittenCount
        => _written;

    internal ReadOnlySpan<byte> WrittenSpan
        => _written == 0 ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, _written);

    internal void Reset(int maxLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maxLength < 0)
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "Max length must be non-negative.");

        _written = 0;
        _maxLength = maxLength == 0 ? int.MaxValue : maxLength;
    }

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var next = checked(_written + count);
        if (next > _buffer.Length)
            throw new InvalidOperationException("Cannot advance past current buffer length.");
        _written = next;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    void EnsureCapacity(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint), sizeHint, "Size hint must be non-negative.");
        if (sizeHint == 0)
            sizeHint = 1;

        var required = checked(_written + sizeHint);
        if (required > _maxLength)
            throw new InvalidOperationException("Compressed payload exceeds MaxCompressedBytes.");
        if (required <= _buffer.Length)
            return;

        var nextSize = _buffer.Length == 0 ? 4096 : _buffer.Length;
        while (nextSize < required)
        {
            var doubled = checked(nextSize * 2);
            nextSize = doubled > _maxLength ? _maxLength : doubled;
            if (nextSize < required)
                throw new InvalidOperationException("Compressed payload exceeds MaxCompressedBytes.");
        }

        var next = ArrayPool<byte>.Shared.Rent(nextSize);
        if (_written > 0)
            _buffer.AsSpan(0, _written).CopyTo(next);
        if (_buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _written = 0;
        _disposed = true;
    }
}
