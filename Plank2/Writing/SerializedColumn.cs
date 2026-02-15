using Plank.Schema;
using Plank2;

namespace Plank2.Writing;

public sealed class SerializedColumn
{
    readonly ParquetWriter _owner;
    
    internal readonly PageList Pages;
    internal int ColumnOrdinal;
    internal int _rowCount;
    public bool IsWritten;

    internal SerializedColumn(ParquetWriter owner, int initialPageCapacity, IParquetLog log)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Pages = new PageList(initialPageCapacity, log);
        _owner = owner;
        ColumnOrdinal = -1;
        _rowCount = 0;
        IsWritten = false;
    }

    public void Serialize<T>(Column column, ReadOnlySpan<T> values)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!IsWritten && ColumnOrdinal >= 0)
            throw new InvalidOperationException("SerializedColumn is already prepared and not yet consumed.");

        var columnOrdinal = _owner.GetColumnOrdinal(column);
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        _rowCount = values.Length;
        IsWritten = false;

        TODO;
    }

    internal void EnsureOwnedBy(ParquetWriter owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (IsWritten || ColumnOrdinal < 0)
            throw new InvalidOperationException("SerializedColumn is not prepared.");
        if (!ReferenceEquals(_owner, owner))
            throw new InvalidOperationException("SerializedColumn is owned by a different ParquetWriter.");
    }

    internal void Consume(ParquetWriter owner)
    {
        EnsureOwnedBy(owner);
        ColumnOrdinal = -1;
        IsWritten = true;
    }
}
