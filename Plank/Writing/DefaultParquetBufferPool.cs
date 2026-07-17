using System.Runtime.InteropServices;

namespace Plank.Writing;

public sealed unsafe class DefaultParquetBufferPool : IParquetBufferPool
{
    const int Alignment = 64;
    const int MinimumBucketPower = 4;
    const int BucketCount = 27;
    const int MaximumRetainedPerBucket = 64;

    static readonly Action<nint> s_returnAllocation = ReturnAllocation;

    public static readonly DefaultParquetBufferPool Shared = new();

    readonly Bucket[] _buckets = CreateBuckets();

    DefaultParquetBufferPool()
    {
    }

    public ParquetBuffer Rent(uint minimumByteLength)
    {
        if (minimumByteLength == 0)
            return default;
        if (minimumByteLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(minimumByteLength), minimumByteLength,
                $"Buffer length must be <= {int.MaxValue}.");

        var bucketIndex = GetBucketIndex(minimumByteLength);
        var capacity = bucketIndex < BucketCount
            ? GetBucketLength(bucketIndex)
            : checked((int)minimumByteLength);
        var header = bucketIndex < BucketCount ? _buckets[bucketIndex].Rent() : null;
        if (header != null)
        {
            header->ReferenceCount = 1;
            header->NextFree = null;
            var data = (byte*)header + sizeof(ParquetBufferHeader);
            return new ParquetBuffer(header, data, header->Capacity, s_returnAllocation);
        }

        var payloadOffset = Alignment;
        var required = checked(payloadOffset + capacity);
        var allocationByteLength = Align(required, Alignment);
        var allocation = (nint)NativeMemory.AlignedAlloc((nuint)allocationByteLength, Alignment);
        if (allocation == 0)
            throw new OutOfMemoryException();

        return ParquetBuffer.CreatePooled(allocation, allocationByteLength, payloadOffset, capacity,
            bucketIndex < BucketCount ? bucketIndex : -1, s_returnAllocation);
    }

    static Bucket[] CreateBuckets()
    {
        var buckets = new Bucket[BucketCount];
        for (var i = 0; i < buckets.Length; i++)
            buckets[i] = new Bucket();
        return buckets;
    }

    static void ReturnAllocation(nint allocation)
    {
        var data = (byte*)allocation + Alignment;
        var header = (ParquetBufferHeader*)(data - sizeof(ParquetBufferHeader));
        var bucketIndex = header->BucketIndex;
        if ((uint)bucketIndex < BucketCount && Shared._buckets[bucketIndex].Return(header))
            return;

        NativeMemory.AlignedFree((void*)allocation);
    }

    static int GetBucketIndex(uint minimumLength)
    {
        var bucketLength = 1U << MinimumBucketPower;
        var index = 0;
        while (bucketLength < minimumLength && index < BucketCount - 1)
        {
            bucketLength <<= 1;
            index++;
        }
        return bucketLength >= minimumLength ? index : BucketCount;
    }

    static int GetBucketLength(int bucketIndex)
        => 1 << (bucketIndex + MinimumBucketPower);

    static int Align(int value, int alignment)
        => checked((value + alignment - 1) & -alignment);

    sealed class Bucket
    {
        readonly object _gate = new();
        ParquetBufferHeader* _head;
        int _count;

        internal ParquetBufferHeader* Rent()
        {
            lock (_gate)
            {
                if (_head is null)
                    return null;

                var header = _head;
                _head = header->NextFree;
                _count--;
                return header;
            }
        }

        internal bool Return(ParquetBufferHeader* header)
        {
            lock (_gate)
            {
                if (_count == MaximumRetainedPerBucket)
                    return false;

                header->NextFree = _head;
                _head = header;
                _count++;
                return true;
            }
        }
    }
}
