using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Plank.Writing;

/// <summary>Owns a slice of reference-counted unmanaged storage.</summary>
/// <remarks>Copying this struct creates an alias. Call <see cref="Retain"/> for independent ownership.</remarks>
public unsafe struct ParquetBuffer : IDisposable
{
    ParquetBufferHeader* _header;
    byte* _data;
    int _length;
    Action<nint>? _returnAllocation;

    internal ParquetBuffer(ParquetBufferHeader* header, byte* data, int length,
        Action<nint> returnAllocation)
    {
        _header = header;
        _data = data;
        _length = length;
        _returnAllocation = returnAllocation;
    }

    public int Length
        => _length;

    public Span<byte> Span
        => _header is null ? [] : new Span<byte>(_data, _length);

    public bool IsEmpty
        => _length == 0;

    public static ParquetBuffer Create(nint allocation, int allocationByteLength, int payloadOffset,
        int payloadByteLength, Action<nint> returnAllocation)
        => CreatePooled(allocation, allocationByteLength, payloadOffset, payloadByteLength, -1,
            returnAllocation);

    public Span<T> AsSpan<T>()
    {
        ThrowIfDisposed();
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"{typeof(T)} cannot be projected over unmanaged storage.");
        var elementSize = Unsafe.SizeOf<T>();
        if (_length % elementSize != 0)
            throw new InvalidOperationException(
                $"Buffer length {_length} is not divisible by the size of {typeof(T)} ({elementSize} bytes)."
            );
        return MemoryMarshal.CreateSpan(ref Unsafe.AsRef<T>(_data), _length / elementSize);
    }

    public ParquetBuffer Retain()
    {
        ThrowIfDisposed();
        RetainHeader();
        return new ParquetBuffer(_header, _data, _length, _returnAllocation!);
    }

    public ParquetBuffer RetainSlice(int offset, int length)
    {
        ThrowIfDisposed();
        if ((uint)offset > (uint)_length)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset is outside the buffer.");
        if ((uint)length > (uint)(_length - offset))
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length exceeds the remaining buffer.");

        RetainHeader();
        return new ParquetBuffer(_header, _data + offset, length, _returnAllocation!);
    }

    public nint DangerousGetAddress()
    {
        ThrowIfDisposed();
        return (nint)_data;
    }

    public void Dispose()
    {
        var header = _header;
        var returnAllocation = _returnAllocation;
        this = default;

        if (header is null || Interlocked.Decrement(ref header->ReferenceCount) != 0)
            return;

        returnAllocation!((nint)header->Allocation);
    }

    internal static ParquetBuffer CreatePooled(nint allocation, int allocationByteLength, int payloadOffset,
        int payloadByteLength, int bucketIndex, Action<nint> returnAllocation)
    {
        if (allocation == 0)
            throw new ArgumentOutOfRangeException(nameof(allocation));
        if (allocationByteLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(allocationByteLength));
        if (payloadOffset < sizeof(ParquetBufferHeader) || payloadOffset > allocationByteLength)
            throw new ArgumentOutOfRangeException(nameof(payloadOffset));
        if (payloadByteLength < 0 || payloadByteLength > allocationByteLength - payloadOffset)
            throw new ArgumentOutOfRangeException(nameof(payloadByteLength));
        ArgumentNullException.ThrowIfNull(returnAllocation);

        var data = (byte*)allocation + payloadOffset;
        var header = (ParquetBufferHeader*)(data - sizeof(ParquetBufferHeader));
        header->ReferenceCount = 1;
        header->Capacity = payloadByteLength;
        header->BucketIndex = bucketIndex;
        header->AllocationByteLength = allocationByteLength;
        header->Allocation = allocation;
        header->NextFree = null;
        return new ParquetBuffer(header, data, payloadByteLength, returnAllocation);
    }

    internal static ReadOnlySpan<T> AsReadOnlySpan<T>(ParquetBuffer buffer, int count)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"{typeof(T)} cannot be projected over unmanaged storage.");
        if (count < 0 || checked(count * Unsafe.SizeOf<T>()) > buffer._length)
            throw new ArgumentOutOfRangeException(nameof(count));
        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef<T>(buffer._data), count);
    }

    internal static Span<T> AsSpan<T>(ParquetBuffer buffer, int count)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new InvalidOperationException($"{typeof(T)} cannot be projected over unmanaged storage.");
        if (count < 0 || checked(count * Unsafe.SizeOf<T>()) > buffer._length)
            throw new ArgumentOutOfRangeException(nameof(count));
        return MemoryMarshal.CreateSpan(ref Unsafe.AsRef<T>(buffer._data), count);
    }

    void RetainHeader()
    {
        var count = Interlocked.Increment(ref _header->ReferenceCount);
        if (count <= 1)
        {
            Interlocked.Decrement(ref _header->ReferenceCount);
            throw new ObjectDisposedException(nameof(ParquetBuffer));
        }
    }

    void ThrowIfDisposed()
    {
        if (_header is null || Volatile.Read(ref _header->ReferenceCount) <= 0)
            throw new ObjectDisposedException(nameof(ParquetBuffer));
    }
}
