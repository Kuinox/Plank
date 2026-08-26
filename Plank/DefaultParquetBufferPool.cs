using System.Runtime.InteropServices;

namespace Plank;

/// <summary>Pools aligned native buffers and learns how many buffers of each size the process reuses.</summary>
public sealed unsafe class DefaultParquetBufferPool : IParquetBufferPool, IDisposable
{
    const int Alignment = 64;
    const int MinimumBucketPower = 4;
    const int BucketCount = 27;
    const int AdaptiveSampleCount = 100;
    const long MinimumRetainedBytesForPressureCheck = 64L * 1024 * 1024;
    const long MemoryPressureCheckIntervalMilliseconds = 1000;

    /// <summary>Gets the process-wide pool, using adaptive p99 retention.</summary>
    public static readonly DefaultParquetBufferPool Shared = new();

    readonly Bucket[] _buckets;
    readonly Action<nint> _returnAllocation;
    readonly Func<bool> _isUnderMemoryPressure;
    readonly long _minimumRetainedBytesForPressureCheck;
    readonly long _memoryPressureCheckIntervalMilliseconds;
    long _retainedBytes;
    long _nativeAllocationCount;
    long _lastMemoryPressureCheck = long.MinValue;
    long _rejectRetentionUntil;
    int _disposed;

    public DefaultParquetBufferPool()
        : this(ParquetBufferRetentionPolicy.Adaptive)
    {
    }

    public DefaultParquetBufferPool(ParquetBufferRetentionPolicy retentionPolicy)
        : this(retentionPolicy, long.MaxValue)
    {
    }

    public DefaultParquetBufferPool(ParquetBufferRetentionPolicy retentionPolicy, long maximumRetainedBytes)
        : this(retentionPolicy, maximumRetainedBytes, IsProcessUnderMemoryPressure,
            MinimumRetainedBytesForPressureCheck, MemoryPressureCheckIntervalMilliseconds)
    {
    }

    internal DefaultParquetBufferPool(ParquetBufferRetentionPolicy retentionPolicy, long maximumRetainedBytes,
        Func<bool> isUnderMemoryPressure, long minimumRetainedBytesForPressureCheck,
        long memoryPressureCheckIntervalMilliseconds)
    {
        if (!Enum.IsDefined(retentionPolicy))
            throw new ArgumentOutOfRangeException(nameof(retentionPolicy), retentionPolicy,
                "Buffer retention policy must be defined.");
        if (maximumRetainedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedBytes), maximumRetainedBytes,
                "Maximum retained bytes must be non-negative.");
        ArgumentNullException.ThrowIfNull(isUnderMemoryPressure);
        if (minimumRetainedBytesForPressureCheck < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumRetainedBytesForPressureCheck));
        if (memoryPressureCheckIntervalMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(memoryPressureCheckIntervalMilliseconds));

        RetentionPolicy = retentionPolicy;
        MaximumRetainedBytes = maximumRetainedBytes;
        _isUnderMemoryPressure = isUnderMemoryPressure;
        _minimumRetainedBytesForPressureCheck = minimumRetainedBytesForPressureCheck;
        _memoryPressureCheckIntervalMilliseconds = memoryPressureCheckIntervalMilliseconds;
        _buckets = CreateBuckets();
        _returnAllocation = ReturnAllocation;
    }

    ~DefaultParquetBufferPool()
        => Dispose(false);

    /// <summary>Gets the policy used to choose how many idle buffers to retain.</summary>
    public ParquetBufferRetentionPolicy RetentionPolicy { get; }

    /// <summary>Gets the hard limit on idle native bytes retained by this pool.</summary>
    public long MaximumRetainedBytes { get; }

    /// <summary>Gets the number of idle native bytes currently retained by this pool.</summary>
    public long RetainedBytes
        => Interlocked.Read(ref _retainedBytes);

    internal long NativeAllocationCount
        => Interlocked.Read(ref _nativeAllocationCount);

    public ParquetBuffer Rent(uint minimumByteLength)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (minimumByteLength == 0)
            return default;
        if (minimumByteLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(minimumByteLength), minimumByteLength,
                $"Buffer length must be <= {int.MaxValue}.");

        var bucketIndex = GetBucketIndex(minimumByteLength);
        var capacity = bucketIndex < BucketCount
            ? GetBucketLength(bucketIndex)
            : checked((int)minimumByteLength);
        var bucket = bucketIndex < BucketCount ? _buckets[bucketIndex] : null;
        ParquetBufferHeader* header = null;
        if (bucket is not null)
            header = bucket.BeginRent(this);
        try
        {
            if (header != null)
            {
                header->ReferenceCount = 1;
                header->NextFree = null;
                var data = (byte*)header + sizeof(ParquetBufferHeader);
                return new ParquetBuffer(header, data, header->Capacity, _returnAllocation);
            }

            var payloadOffset = Alignment;
            var required = checked(payloadOffset + capacity);
            var allocationByteLength = Align(required, Alignment);
            var allocation = (nint)NativeMemory.AlignedAlloc((nuint)allocationByteLength, Alignment);
            if (allocation == 0)
                throw new OutOfMemoryException();
            Interlocked.Increment(ref _nativeAllocationCount);

            return ParquetBuffer.CreatePooled(allocation, allocationByteLength, payloadOffset, capacity,
                bucketIndex < BucketCount ? bucketIndex : -1, _returnAllocation);
        }
        catch
        {
            bucket?.CancelRent();
            throw;
        }
    }

    static Bucket[] CreateBuckets()
    {
        var buckets = new Bucket[BucketCount];
        for (var i = 0; i < buckets.Length; i++)
            buckets[i] = new Bucket();
        return buckets;
    }

    void ReturnAllocation(nint allocation)
    {
        var data = (byte*)allocation + Alignment;
        var header = (ParquetBufferHeader*)(data - sizeof(ParquetBufferHeader));
        var bucketIndex = header->BucketIndex;
        var completedDemandCycle = false;
        if ((uint)bucketIndex < BucketCount &&
            _buckets[bucketIndex].Return(header, this, out completedDemandCycle))
        {
            if (completedDemandCycle)
                MaybeTrimUnderMemoryPressure();
            return;
        }

        NativeMemory.AlignedFree((void*)allocation);
        if ((uint)bucketIndex < BucketCount && completedDemandCycle)
            MaybeTrimUnderMemoryPressure();
    }

    /// <summary>Releases every idle buffer without resetting learned demand.</summary>
    public void Trim()
    {
        for (var i = 0; i < _buckets.Length; i++)
            _buckets[i].Trim(this, 0);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Trim();
    }

    bool TryReserveRetainedBytes(int allocationByteLength)
    {
        var rejectRetentionUntil = Interlocked.Read(ref _rejectRetentionUntil);
        if (rejectRetentionUntil != 0 && Environment.TickCount64 < rejectRetentionUntil)
            return false;

        while (true)
        {
            var retainedBytes = Interlocked.Read(ref _retainedBytes);
            if (allocationByteLength > MaximumRetainedBytes - retainedBytes)
                return false;
            if (Interlocked.CompareExchange(ref _retainedBytes,
                    retainedBytes + allocationByteLength, retainedBytes) == retainedBytes)
                return true;
        }
    }

    void ReleaseRetainedBytes(int allocationByteLength)
        => Interlocked.Add(ref _retainedBytes, -allocationByteLength);

    void MaybeTrimUnderMemoryPressure()
    {
        if (RetainedBytes < _minimumRetainedBytesForPressureCheck)
            return;

        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastMemoryPressureCheck);
        if ((previous != long.MinValue && now - previous < _memoryPressureCheckIntervalMilliseconds) ||
            Interlocked.CompareExchange(ref _lastMemoryPressureCheck, now, previous) != previous)
            return;

        if (_isUnderMemoryPressure())
        {
            Interlocked.Exchange(ref _rejectRetentionUntil,
                checked(now + _memoryPressureCheckIntervalMilliseconds));
            Trim();
        }
    }

    static bool IsProcessUnderMemoryPressure()
    {
        var memory = GC.GetGCMemoryInfo();
        return memory.HighMemoryLoadThresholdBytes > 0 &&
               memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes;
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
        readonly int[] _adaptiveSamples = new int[AdaptiveSampleCount];
        ParquetBufferHeader* _head;
        int _retainedCount;
        int _rentedCount;
        int _currentPeak;
        int _highWaterMark;
        int _adaptiveTarget;
        int _sampleCount;
        int _nextSample;

        internal ParquetBufferHeader* BeginRent(DefaultParquetBufferPool owner)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
                _rentedCount++;
                if (_rentedCount > _currentPeak)
                    _currentPeak = _rentedCount;
                if (_currentPeak > _highWaterMark)
                    _highWaterMark = _currentPeak;

                var header = _head;
                if (header is null)
                    return null;

                _head = header->NextFree;
                _retainedCount--;
                owner.ReleaseRetainedBytes(header->AllocationByteLength);
                return header;
            }
        }

        internal void CancelRent()
        {
            lock (_gate)
            {
                _rentedCount--;
                if (_rentedCount == 0)
                    _currentPeak = 0;
            }
        }

        internal bool Return(ParquetBufferHeader* header, DefaultParquetBufferPool owner,
            out bool completedDemandCycle)
        {
            lock (_gate)
            {
                _rentedCount--;
                completedDemandCycle = _rentedCount == 0;

                var target = owner.RetentionPolicy == ParquetBufferRetentionPolicy.ZeroAllocation
                    ? _highWaterMark
                    : Math.Max(_adaptiveTarget, _currentPeak);
                var retained = Volatile.Read(ref owner._disposed) == 0 &&
                               _retainedCount < target &&
                               owner.TryReserveRetainedBytes(header->AllocationByteLength);
                if (retained)
                {
                    header->NextFree = _head;
                    _head = header;
                    _retainedCount++;
                }

                if (!completedDemandCycle)
                    return retained;

                if (owner.RetentionPolicy == ParquetBufferRetentionPolicy.Adaptive)
                {
                    // A cycle starts when this bucket goes from zero rented buffers to one and ends here,
                    // when it returns to zero. Concurrent writers therefore contribute their combined demand.
                    RecordAdaptiveSample(_currentPeak);
                    _adaptiveTarget = GetAdaptiveTarget();
                    TrimCore(owner, _adaptiveTarget);
                }

                _currentPeak = 0;
                return retained;
            }
        }

        internal void Trim(DefaultParquetBufferPool owner, int targetCount)
        {
            lock (_gate)
                TrimCore(owner, targetCount);
        }

        void TrimCore(DefaultParquetBufferPool owner, int targetCount)
        {
            while (_retainedCount > targetCount)
            {
                var header = _head;
                _head = header->NextFree;
                _retainedCount--;
                owner.ReleaseRetainedBytes(header->AllocationByteLength);
                NativeMemory.AlignedFree((void*)header->Allocation);
            }
        }

        void RecordAdaptiveSample(int peak)
        {
            _adaptiveSamples[_nextSample] = peak;
            _nextSample = (_nextSample + 1) % AdaptiveSampleCount;
            if (_sampleCount < AdaptiveSampleCount)
                _sampleCount++;
        }

        int GetAdaptiveTarget()
        {
            Span<int> sorted = stackalloc int[AdaptiveSampleCount];
            _adaptiveSamples.AsSpan(0, _sampleCount).CopyTo(sorted);
            sorted = sorted[.._sampleCount];
            sorted.Sort();

            // Nearest-rank p99. With a full 100-cycle window this discards one exceptional peak.
            var rank = checked((99 * _sampleCount + 99) / 100);
            return sorted[rank - 1];
        }
    }
}
