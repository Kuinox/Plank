using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

struct PageMetadataHandle : IDisposable
{
    ParquetBuffer _entries;
    ParquetBuffer _statisticsStorage;
    readonly LeafColumn _definition;
    readonly int _rowGroupIndex;
    bool _disposed;

    internal PageMetadataHandle(ParquetBuffer entries, int count, ParquetBuffer statisticsStorage,
        LeafColumn definition, int rowGroupIndex, ParquetBoundaryOrder boundaryOrder)
    {
        _entries = entries;
        _statisticsStorage = statisticsStorage;
        _definition = definition;
        _rowGroupIndex = rowGroupIndex;
        _disposed = false;
        Count = count;
        BoundaryOrder = boundaryOrder;
    }

    internal int Count { get; }

    internal ParquetBoundaryOrder BoundaryOrder { get; }

    internal ReadOnlySpan<PageMetadataEntry> Entries
    {
        get
        {
            ThrowIfDisposed();
            return ParquetBuffer.AsReadOnlySpan<PageMetadataEntry>(_entries, Count);
        }
    }

    internal ParquetDataPageMetadata GetMetadata(int index)
    {
        ThrowIfDisposed();
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var entry = Entries[index];
        var statistics = new ParquetStatistics(_statisticsStorage.Span, entry.Statistics, _definition);
        return new ParquetDataPageMetadata(_rowGroupIndex, index, entry.Offset, entry.CompressedSize,
            entry.HasFirstRowIndex ? entry.FirstRowIndex : null,
            entry.HasRowCount ? entry.RowCount : null,
            entry.HasNullPage ? entry.IsNullPage : null,
            statistics);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _entries.Dispose();
        _statisticsStorage.Dispose();
    }

    internal void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ParquetDataPageMetadataCollection));
    }
}
