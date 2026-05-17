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

    internal ulong RowCount
        => _rowGroup.RowCount;

    internal void Reset(ParquetReader reader, IParquetReadSource source, InternalRowGroupMetadata rowGroup)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(source);

        _reader = reader;
        _source = source;
        _rowGroup = rowGroup;
        _token = new RowGroupToken(rowGroup.RowGroupOrdinal, rowGroup.MetadataOffset, rowGroup.ColumnChunkOffset);
        _disposed = false;
    }

    internal void Dispose()
    {
        if (_disposed)
            return;

        for (var i = 0; i < _columnPageStates.Length; i++)
            if (_columnPageStates[i] is IColumnPageReadState state)
                state.ReleaseAll(_reader.Options.BufferPool);

        _disposed = true;
    }

    internal void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RowGroupReader));
    }

    internal int GetColumnOrdinal(Column column)
        => _reader.GetColumnOrdinal(column);

    internal ColumnPageEnumerable<T> EnumeratePages<T>(Column column, int columnOrdinal)
    {
        if ((uint)columnOrdinal >= (uint)_rowGroup.Columns.Length)
            throw new CorruptParquetException(
                $"Column '{column.Name}' (ordinal {columnOrdinal}) is not present in this row group ({_rowGroup.Columns.Length} columns).");
        return new(_source, column, _rowGroup.Columns[columnOrdinal],
            GetPageReadState<T>(columnOrdinal), _reader.Options.BufferPool, _rowGroup.RowCount);
    }

    ColumnPageReadState<T> GetPageReadState<T>(int columnOrdinal)
    {
        if (_columnPageStates[columnOrdinal] is ColumnPageReadState<T> state)
            return state;

        state = new ColumnPageReadState<T>();
        _columnPageStates[columnOrdinal] = state;
        return state;
    }
}
