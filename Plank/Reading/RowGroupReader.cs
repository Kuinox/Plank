using Plank.Schema;

namespace Plank.Reading;

public sealed class RowGroupReader : IDisposable
{
    readonly ParquetReader _reader;
    readonly IParquetReadSource _source;
    readonly InternalRowGroupMetadata _rowGroup;
    readonly RowGroupToken _token;
    readonly object?[] _columnPageStates;
    bool _disposed;

    internal RowGroupReader(ParquetReader reader, IParquetReadSource source, InternalRowGroupMetadata rowGroup)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(source);

        _reader = reader;
        _source = source;
        _rowGroup = rowGroup;
        _token = new RowGroupToken(rowGroup.RowGroupOrdinal, rowGroup.MetadataOffset, rowGroup.ColumnChunkOffset);
        _columnPageStates = new object?[rowGroup.Columns.Length];
        _disposed = false;
    }

    internal IParquetReadSource Source
        => _source;

    public RowGroupToken Token
        => _token;

    public long RowCount
        => _rowGroup.RowCount;

    public RowGroupColumn<T> Column<T>(Column column)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(column);
        return new RowGroupColumn<T>(this, column, _reader.GetColumnOrdinal(column));
    }

    public void Dispose()
        => _disposed = true;

    internal void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RowGroupReader));
    }

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
