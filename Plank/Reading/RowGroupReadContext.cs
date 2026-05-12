using Plank.Schema;

namespace Plank.Reading;

sealed class RowGroupReadContext
{
    readonly object?[] _columnPageStates;
    ParquetReader _reader;
    IParquetReadSource _source;
    InternalRowGroupMetadata _rowGroup;
    RowGroupToken _token;
    bool _disposed;

    internal RowGroupReadContext(int columnCount)
    {
        if (columnCount < 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount), columnCount,
                "Column count must be non-negative.");

        _columnPageStates = columnCount == 0 ? [] : new object?[columnCount];
        _reader = null!;
        _source = null!;
        _rowGroup = default;
        _token = default;
        _disposed = true;
    }

    internal IParquetReadSource Source
        => _source;

    internal RowGroupToken Token
        => _token;

    internal long RowCount
        => _rowGroup.RowCount;

    internal void Reset(ParquetReader reader, IParquetReadSource source, InternalRowGroupMetadata rowGroup)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(source);
        if (rowGroup.Columns.Length != _columnPageStates.Length)
            throw new InvalidOperationException("Row group column count does not match the reader schema.");

        _reader = reader;
        _source = source;
        _rowGroup = rowGroup;
        _token = new RowGroupToken(rowGroup.RowGroupOrdinal, rowGroup.MetadataOffset, rowGroup.ColumnChunkOffset);
        _disposed = false;
    }

    internal void Dispose()
        => _disposed = true;

    internal void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RowGroupReader));
    }

    internal int GetColumnOrdinal(Column column)
        => _reader.GetColumnOrdinal(column);

    internal ColumnPageEnumerable<T> EnumeratePages<T>(Column column, int columnOrdinal)
        => new(_source, column, _reader.GetColumnChunk(_rowGroup.RowGroupOrdinal, columnOrdinal),
            GetPageReadState<T>(columnOrdinal), _reader.Options.BufferPool);

    ColumnPageReadState<T> GetPageReadState<T>(int columnOrdinal)
    {
        if (_columnPageStates[columnOrdinal] is ColumnPageReadState<T> state)
            return state;

        state = new ColumnPageReadState<T>();
        _columnPageStates[columnOrdinal] = state;
        return state;
    }
}
