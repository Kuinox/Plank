using Plank.Schema;

namespace Plank2.Writing;

public sealed class SerializedColumn
{
    readonly ParquetWriter _owner;
    
    internal readonly PageList Pages;
    internal int ColumnOrdinal;
    internal int RowCount;
    bool IsWritten;

    internal SerializedColumn(ParquetWriter owner, int initialPageCapacity)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Pages = new PageList(initialPageCapacity);
        _owner = owner;
        ColumnOrdinal = -1;
        RowCount = 0;
        IsWritten = false;
    }

    public void Serialize<T>(Column column, ReadOnlySpan<T> values)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!IsWritten && ColumnOrdinal >= 0)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");

        var columnOrdinal = _owner.GetColumnOrdinal(column);
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = values.Length;
        IsWritten = false;

        TODO;
    }

    /// <summary>
    /// Invalidates the current prepared payload to avoid missuses.
    /// </summary>
    internal void Consume()
    {
        ColumnOrdinal = -1;
        IsWritten = true;
    }
}
