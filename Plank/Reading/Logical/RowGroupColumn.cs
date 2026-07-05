using Plank.Schema;

namespace Plank.Reading.Logical;

public readonly struct RowGroupColumn<T>
{
    readonly RowGroupReader _rowGroupReader;
    readonly Column _column;
    readonly int _columnOrdinal;

    internal RowGroupColumn(RowGroupReader rowGroupReader, Column column, int columnOrdinal)
    {
        ArgumentNullException.ThrowIfNull(rowGroupReader);
        ArgumentNullException.ThrowIfNull(column);

        _rowGroupReader = rowGroupReader;
        _column = column;
        _columnOrdinal = columnOrdinal;
    }

    public Column Definition
        => _column;

    public ColumnPageEnumerable<T> Pages
    {
        get
        {
            _rowGroupReader.ThrowIfDisposed();
            return _rowGroupReader.EnumeratePages<T>(_column, _columnOrdinal);
        }
    }
}
