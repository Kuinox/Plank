using Plank.Reading.Logical.Internal;
using Plank.Schema;

namespace Plank.Reading.Logical;

public sealed class RowGroupReader : IDisposable
{
    readonly RowGroupReadContext _context;

    internal RowGroupReader(RowGroupReadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    internal void Reset(ParquetReader reader, InternalRowGroupMetadata rowGroup)
        => _context.Reset(reader, rowGroup);

    internal InternalColumnChunkMetadata[] PreviousColumns
        => _context.PreviousColumns;

    public RowGroupToken Token
        => _context.Token;

    public ulong RowCount
        => _context.RowCount;

    public RowGroupColumn<T> Column<T>(Column column)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(column);
        return new RowGroupColumn<T>(this, column, _context.GetColumnOrdinal(column));
    }

    public RowGroupColumn<T> Column<T>(int columnOrdinal)
    {
        ThrowIfDisposed();
        return new RowGroupColumn<T>(this, _context.GetColumn(columnOrdinal), columnOrdinal);
    }

    public void Dispose()
        => _context.Dispose();

    internal void ThrowIfDisposed()
        => _context.ThrowIfDisposed();

    internal ColumnPageEnumerable<T> EnumeratePages<T>(Column column, int columnOrdinal)
        => _context.EnumeratePages<T>(column, columnOrdinal);
}
