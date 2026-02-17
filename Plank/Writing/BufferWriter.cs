using System.Buffers;

namespace Plank.Writing;

public struct BufferWriter : IBufferWriter<byte>
{
    Segment[]? _segments;
    IParquetBufferPool? _pool;
    int _chunkSizeBytes;
    int _segmentCount;
    int _currentSegmentIndex;
    int _currentSegmentWritten;
    int _writtenLength;

    internal BufferWriter(IParquetBufferPool pool, int chunkSizeBytes, int initialBufferBytes)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (chunkSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes), chunkSizeBytes,
                "Buffer chunk size must be greater than zero.");
        if (initialBufferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialBufferBytes), initialBufferBytes,
                "Initial buffer size must be greater than zero.");

        _pool = pool;
        _chunkSizeBytes = chunkSizeBytes;
        _segments = new Segment[GetInitialSegmentCapacity(initialBufferBytes, chunkSizeBytes)];
        _segmentCount = 0;
        _currentSegmentIndex = 0;
        _currentSegmentWritten = 0;
        _writtenLength = 0;
    }

    public void Advance(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Advance count must be non-negative.");
        if (_segments is null || _segmentCount == 0)
            throw new InvalidOperationException("BufferWriter is not initialized.");

        ref var segment = ref _segments[_currentSegmentIndex];
        if (count > segment.Buffer.Length - _currentSegmentWritten)
            throw new InvalidOperationException("Advance exceeds available buffer capacity.");

        _currentSegmentWritten += count;
        segment.Written = _currentSegmentWritten;
        _writtenLength += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureWritable(sizeHint);
        return _segments![_currentSegmentIndex].Buffer.AsMemory(_currentSegmentWritten);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureWritable(sizeHint);
        return _segments![_currentSegmentIndex].Buffer.AsSpan(_currentSegmentWritten);
    }

    internal bool IsInitialized
        => _segments is not null;

    internal int WrittenLength
        => _writtenLength;

    internal bool TryGetSingleWrittenSpan(out ReadOnlySpan<byte> span)
    {
        if (_segments is null || _writtenLength == 0)
        {
            span = [];
            return true;
        }

        var segmentWithData = -1;
        for (var i = 0; i < _segmentCount; i++)
        {
            if (_segments[i].Written == 0)
                continue;
            if (segmentWithData >= 0)
            {
                span = default;
                return false;
            }

            segmentWithData = i;
        }

        if (segmentWithData < 0)
        {
            span = [];
            return true;
        }

        span = _segments[segmentWithData].Buffer.AsSpan(0, _segments[segmentWithData].Written);
        return true;
    }

    internal void Reset()
    {
        if (_segments is null)
            return;

        for (var i = 0; i < _segmentCount; i++)
            _segments[i].Written = 0;

        _currentSegmentIndex = 0;
        _currentSegmentWritten = 0;
        _writtenLength = 0;
    }

    internal void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (_segments is null || _writtenLength == 0)
            return;

        for (var i = 0; i < _segmentCount; i++)
        {
            var written = _segments[i].Written;
            if (written == 0)
                continue;
            stream.Write(_segments[i].Buffer, 0, written);
        }
    }

    internal void CopyTo(Span<byte> destination)
    {
        if (destination.Length < _writtenLength)
            throw new ArgumentException("Destination span is smaller than written content.", nameof(destination));
        if (_segments is null || _writtenLength == 0)
            return;

        var offset = 0;
        for (var i = 0; i < _segmentCount; i++)
        {
            var written = _segments[i].Written;
            if (written == 0)
                continue;

            _segments[i].Buffer.AsSpan(0, written).CopyTo(destination[offset..]);
            offset += written;
        }
    }

    void EnsureWritable(int sizeHint)
    {
        if (sizeHint < 0)
            throw new ArgumentOutOfRangeException(nameof(sizeHint), sizeHint, "Size hint must be non-negative.");
        if (_segments is null || _pool is null)
            throw new InvalidOperationException("BufferWriter is not initialized.");

        if (sizeHint == 0)
            sizeHint = 1;

        if (_segmentCount == 0)
        {
            EnsureCurrentSegment(sizeHint);
            return;
        }

        ref var current = ref _segments[_currentSegmentIndex];
        if (sizeHint <= current.Buffer.Length - _currentSegmentWritten)
            return;

        current.Written = _currentSegmentWritten;

        var nextIndex = _currentSegmentIndex + 1;
        while (nextIndex < _segmentCount)
        {
            if (sizeHint <= _segments[nextIndex].Buffer.Length)
            {
                _currentSegmentIndex = nextIndex;
                _currentSegmentWritten = 0;
                _segments[nextIndex].Written = 0;
                return;
            }

            _segments[nextIndex].Written = 0;
            nextIndex++;
        }

        _currentSegmentIndex = _segmentCount;
        _currentSegmentWritten = 0;
        EnsureCurrentSegment(sizeHint);
    }

    void EnsureCurrentSegment(int sizeHint)
    {
        if (_segments is null || _pool is null)
            throw new InvalidOperationException("BufferWriter is not initialized.");

        EnsureSegmentCapacity(_currentSegmentIndex + 1);
        if (_currentSegmentIndex < _segmentCount)
            return;

        var minimumSize = Math.Max(_chunkSizeBytes, sizeHint);
        var buffer = _pool.Rent(minimumSize);
        _segments[_currentSegmentIndex] = new Segment(buffer);
        _segmentCount = _currentSegmentIndex + 1;
    }

    void EnsureSegmentCapacity(int required)
    {
        if (_segments is null)
            throw new InvalidOperationException("BufferWriter is not initialized.");
        if (required <= _segments.Length)
            return;

        var newCapacity = _segments.Length == 0 ? 4 : _segments.Length;
        while (newCapacity < required)
            newCapacity = checked(newCapacity * 2);

        Array.Resize(ref _segments, newCapacity);
        ParquetMetrics.BufferWriterSegmentTableAllocations.Add(1);
    }

    static int GetInitialSegmentCapacity(int initialBufferBytes, int chunkSizeBytes)
    {
        var count = checked((initialBufferBytes + chunkSizeBytes - 1) / chunkSizeBytes);
        return count <= 0 ? 1 : count;
    }

    struct Segment
    {
        internal readonly byte[] Buffer;
        internal int Written;

        internal Segment(byte[] buffer)
        {
            Buffer = buffer;
            Written = 0;
        }
    }
}
