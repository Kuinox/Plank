using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Plank.Reading.Logical.Internal;

sealed class RowGroupReadContext
{
    object?[] _columnPageStates;
    ParquetReader _reader;
    InternalRowGroupMetadata _rowGroup;
    RowGroupToken _token;
    bool _disposed;

    internal RowGroupReadContext(int columnCount)
    {
        var arr = ArrayPool<byte>.Shared.Rent(1024);

        Unsafe.As<int[]>(arr);



        if (columnCount < 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount), columnCount,
                "Column count must be non-negative.");

        _columnPageStates = columnCount == 0 ? [] : new object?[columnCount];
        _reader = null!;
        _rowGroup = default;
        _token = default;
        _disposed = true;
    }

    internal InternalColumnChunkMetadata[] PreviousColumns
        => _rowGroup.Columns ?? [];

    internal RowGroupToken Token
        => _token;

    internal ulong RowCount
        => _rowGroup.RowCount;

    internal void Reset(ParquetReader reader, InternalRowGroupMetadata rowGroup)
    {
        ArgumentNullException.ThrowIfNull(reader);

        EnsureColumnCapacity(reader.Schema.Columns.Length);
        _reader = reader;
        _rowGroup = rowGroup;
        _token = new RowGroupToken(reader, rowGroup);
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

    internal Column GetColumn(int columnOrdinal)
    {
        var columns = _reader.Schema.Columns;
        if ((uint)columnOrdinal >= (uint)columns.Length)
            throw new ArgumentOutOfRangeException(nameof(columnOrdinal), columnOrdinal,
                "Column ordinal is outside the reader schema.");
        return columns[columnOrdinal];
    }

    internal ColumnPageEnumerable<T> EnumeratePages<T>(Column column, int columnOrdinal)
    {
        if ((uint)columnOrdinal >= (uint)_rowGroup.Columns.Length)
            throw new CorruptParquetException(
                $"Column '{column.Name}' (ordinal {columnOrdinal}) is not present in this row group ({_rowGroup.Columns.Length} columns).");
        var columnChunk = _rowGroup.Columns[columnOrdinal];
        var physicalColumnOrdinal = columnChunk.PhysicalColumnOrdinal >= 0
            ? columnChunk.PhysicalColumnOrdinal
            : columnOrdinal;
        return new(_reader.PhysicalReader, _rowGroup.RowGroupOrdinal, physicalColumnOrdinal, column,
            columnChunk, GetPageReadState<T>(columnOrdinal), _reader.Options.BufferPool, _rowGroup.RowCount);
    }

    ColumnPageReadState<T> GetPageReadState<T>(int columnOrdinal)
    {
        if (_columnPageStates[columnOrdinal] is ColumnPageReadState<T> state)
            return state;

        state = new ColumnPageReadState<T>();
        _columnPageStates[columnOrdinal] = state;
        return state;
    }

    void EnsureColumnCapacity(int columnCount)
    {
        if (_columnPageStates.Length == columnCount)
            return;

        if (!_disposed)
            for (var i = 0; i < _columnPageStates.Length; i++)
                if (_columnPageStates[i] is IColumnPageReadState state)
                    state.ReleaseAll(_reader.Options.BufferPool);

        _columnPageStates = columnCount == 0 ? [] : new object?[columnCount];
    }
}
