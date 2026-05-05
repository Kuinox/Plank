namespace Plank.Writing;

sealed class ArrayRenter<T>
{
    const int MinimumBucketPower = 4;
    const int BucketCount = 27;
    const int MaximumRetainedPerBucket = 64;

    internal static readonly ArrayRenter<T> Shared = new();

    readonly Bucket[] _buckets = CreateBuckets();

    ArrayRenter()
    {
    }

    internal T[] Rent(int minimumLength)
    {
        if (minimumLength < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumLength), minimumLength,
                "Minimum length must be non-negative.");
        if (minimumLength == 0)
            return [];

        var bucketIndex = GetBucketIndex(minimumLength);
        if ((uint)bucketIndex >= _buckets.Length)
            return new T[minimumLength];

        var buffer = _buckets[bucketIndex].Rent();
        return buffer ?? new T[GetBucketLength(bucketIndex)];
    }

    internal void Return(T[] buffer, bool clearArray = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0)
            return;
        if (clearArray)
            buffer.AsSpan().Clear();

        var bucketIndex = GetBucketIndex(buffer.Length);
        if ((uint)bucketIndex >= _buckets.Length || GetBucketLength(bucketIndex) != buffer.Length)
            return;

        _buckets[bucketIndex].Return(buffer);
    }

    static Bucket[] CreateBuckets()
    {
        var buckets = new Bucket[BucketCount];
        for (var i = 0; i < buckets.Length; i++)
            buckets[i] = new Bucket();
        return buckets;
    }

    static int GetBucketIndex(int minimumLength)
    {
        var bucketLength = 1 << MinimumBucketPower;
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

    sealed class Bucket
    {
        readonly object _gate = new();
        T[][] _buffers = new T[4][];
        int _count;

        internal T[]? Rent()
        {
            lock (_gate)
            {
                if (_count == 0)
                    return null;

                _count--;
                var buffer = _buffers[_count];
                _buffers[_count] = null!;
                return buffer;
            }
        }

        internal void Return(T[] buffer)
        {
            lock (_gate)
            {
                if (_count == MaximumRetainedPerBucket)
                    return;
                if (_count == _buffers.Length)
                    Array.Resize(ref _buffers, Math.Min(MaximumRetainedPerBucket, _buffers.Length * 2));

                _buffers[_count] = buffer;
                _count++;
            }
        }
    }
}
