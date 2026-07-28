using Plank.Reading.Internal;
using Plank.Schema;

namespace Plank.Reading.Logical;

/// <summary>Provides raw encoded bounds and counts from Parquet statistics.</summary>
public readonly ref struct ParquetStatistics
{
    readonly ReadOnlySpan<byte> _storage;
    readonly EncodedStatistics _statistics;

    internal ParquetStatistics(ReadOnlySpan<byte> storage, EncodedStatistics statistics, LeafColumn definition)
    {
        _storage = storage;
        _statistics = statistics;
        Definition = definition;
    }

    public LeafColumn Definition { get; }

    public bool HasMinimum
        => _statistics.HasMinimum;

    public bool HasMaximum
        => _statistics.HasMaximum;

    public ReadOnlySpan<byte> Minimum
        => HasMinimum ? _storage.Slice(_statistics.MinimumOffset, _statistics.MinimumLength) : [];

    public ReadOnlySpan<byte> Maximum
        => HasMaximum ? _storage.Slice(_statistics.MaximumOffset, _statistics.MaximumLength) : [];

    public long? NullCount
        => _statistics.HasNullCount ? _statistics.NullCount : null;

    public long? DistinctCount
        => _statistics.HasDistinctCount ? _statistics.DistinctCount : null;

    public bool IsMinimumExact
        => _statistics.IsMinimumExact;

    public bool IsMaximumExact
        => _statistics.IsMaximumExact;
}
