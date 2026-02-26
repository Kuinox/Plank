using System.Collections.Generic;
using Plank.Schema;
using Plank.Writing.Encoding;
using Plank.Writing.PageStrategy;

namespace Plank.Writing;

public sealed class SerializedColumn
{
    readonly ParquetWriter _owner;
    object? _dictionaryState;
    
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
    {
        var columnOrdinal = _owner.GetColumnOrdinal(column);
        SerializeCore(column, values, columnOrdinal, _owner.GetPageStrategy(columnOrdinal));
    }

    public void Serialize(Column column, ReadOnlyMemory<byte>[] values)
        => Serialize<ReadOnlyMemory<byte>>(column, values);

    public void Serialize(Column column, string[] values)
        => Serialize<string>(column, values);

    void SerializeCore<T>(Column column, ReadOnlySpan<T> values, uint columnOrdinal, IPageStrategy strategy)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(strategy);
        if (HasPendingData)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = values.Length;
        HasPendingData = true;

        Plank.Writing.Encoding.Encoding.Encode(_owner.BufferWriters, column, values, strategy, Pages,
            _owner.ColumnProjectionInfosByOrdinal[columnOrdinal], GetOrCreateDictionaryState<T>());
    }

    /// <summary>
    /// Invalidates the current prepared payload to avoid missuses.
    /// </summary>
    internal void Consume()
    {
        HasPendingData = false;
    }

    ReusableDictionaryState<T> GetOrCreateDictionaryState<T>()
        where T : notnull
    {
        if (_dictionaryState is ReusableDictionaryState<T> state)
            return state;

        state = new ReusableDictionaryState<T>();
        _dictionaryState = state;
        return state;
    }
}
