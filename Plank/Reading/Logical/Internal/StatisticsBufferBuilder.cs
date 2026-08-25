using Plank.Reading.Internal;

namespace Plank.Reading.Logical.Internal;

struct StatisticsBufferBuilder : IDisposable
{
    readonly IParquetBufferPool _pool;
    ParquetBuffer _buffer;
    int _length;

    internal StatisticsBufferBuilder(IParquetBufferPool pool)
    {
        _pool = pool;
        _buffer = default;
        _length = 0;
    }

    internal EncodedStatistics Copy(ReadOnlySpan<byte> source, EncodedStatistics statistics)
    {
        var minimumOffset = _length;
        if (statistics.HasMinimum)
            Append(source.Slice(statistics.MinimumOffset, statistics.MinimumLength));
        var maximumOffset = _length;
        if (statistics.HasMaximum)
            Append(source.Slice(statistics.MaximumOffset, statistics.MaximumLength));

        return new EncodedStatistics(minimumOffset, statistics.MinimumLength, maximumOffset,
            statistics.MaximumLength, statistics.NullCount, statistics.DistinctCount,
            statistics.HasMinimum, statistics.HasMaximum, statistics.HasNullCount,
            statistics.HasDistinctCount, statistics.IsMinimumExact, statistics.IsMaximumExact);
    }

    internal ParquetBuffer Detach()
    {
        var result = _buffer;
        _buffer = default;
        _length = 0;
        return result;
    }

    public void Dispose()
    {
        _buffer.Dispose();
        _buffer = default;
        _length = 0;
    }

    void Append(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(checked(_length + value.Length));
        value.CopyTo(_buffer.Span[_length..]);
        _length += value.Length;
    }

    void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required)
            return;
        var capacity = Math.Max(required, Math.Max(64, checked(_buffer.Length * 2)));
        var next = _pool.Rent(checked((uint)capacity));
        if (_length != 0)
            _buffer.Span[.._length].CopyTo(next.Span);
        _buffer.Dispose();
        _buffer = next;
    }
}
