using Plank.Schema;

namespace Plank.Reading;

public readonly struct ColumnReader<T>(RowGroupColumn<T> column)
{
    readonly RowGroupColumn<T> _column = column;

    public Column Column
        => _column.Definition;

    public ColumnPageEnumerable<T> EnumeratePages()
        => _column.Pages;
}
