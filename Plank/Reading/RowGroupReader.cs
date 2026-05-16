using Plank.Schema;

namespace Plank.Reading;

public sealed class RowGroupReader : IDisposable
{
    readonly RowGroupReadContext _context;

    internal RowGroupReader(ParquetReader reader, IParquetReadSource source, InternalRowGroupMetadata rowGroup)
        : this(reader.CreateRowGroupReadContext(), reader, source, rowGroup)
    {
    }

    internal RowGroupReader(RowGroupReadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    internal RowGroupReader(RowGroupReadContext context, ParquetReader reader, IParquetReadSource source,
        InternalRowGroupMetadata rowGroup)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        Reset(reader, source, rowGroup);
    }

    internal void Reset(ParquetReader reader, IParquetReadSource source, InternalRowGroupMetadata rowGroup)
        => _context.Reset(reader, source, rowGroup);

    internal IParquetReadSource Source
        => _context.Source;

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

    public void Dispose()
        => _context.Dispose();

    internal void ThrowIfDisposed()
        => _context.ThrowIfDisposed();

    internal ColumnPageEnumerable<T> EnumeratePages<T>(Column column, int columnOrdinal)
        => _context.EnumeratePages<T>(column, columnOrdinal);
}
