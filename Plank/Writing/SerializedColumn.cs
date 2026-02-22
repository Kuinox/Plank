using System.Collections.Generic;
using Plank.Schema;
using Plank.Writing.Encoding;
using Plank.Writing.PageStrategy;

namespace Plank.Writing;

public sealed class SerializedColumn
{
    static readonly IPageStrategy _defaultPageStrategy = new DefaultStrategy();

    readonly ParquetWriter _owner;
    
    internal readonly PageList Pages;
    internal uint ColumnOrdinal;
    internal int RowCount;
    internal bool HasPendingData;

    internal SerializedColumn(ParquetWriter owner, uint initialPageCapacity)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Pages = new PageList(initialPageCapacity);
        _owner = owner;
        ColumnOrdinal = 0;
        RowCount = 0;
        HasPendingData = false;
    }

    public void Serialize<T>(Column column, ReadOnlySpan<T> values)
        where T : notnull
        => Serialize(column, values, _defaultPageStrategy);

    public void Serialize<T>(Column column, ReadOnlySpan<T> values, IPageStrategy strategy)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategy);
        if (HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");

        var columnOrdinal = _owner.GetColumnOrdinal(column);
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = values.Length;
        HasPendingData = true;

        Plank.Writing.Encoding.Encoding.Encode(_owner.BufferWriters, column, values, strategy, Pages,
            _owner.ColumnProjectionInfosByOrdinal[columnOrdinal]);
    }

    /// <summary>
    /// Invalidates the current prepared payload to avoid missuses.
    /// </summary>
    internal void Consume()
    {
        HasPendingData = false;
    }
}
