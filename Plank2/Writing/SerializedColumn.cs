using System.Collections.Generic;
using Plank.Schema;

namespace Plank2.Writing;

public sealed class SerializedColumn
{
    static readonly IPageStrategy DefaultPageStrategy = new DefaultStrategy();

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
        where T : notnull
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

        Encoding.Encode(_owner.BufferWriters, column, values, DefaultPageStrategy, Pages);
    }

    /// <summary>
    /// Invalidates the current prepared payload to avoid missuses.
    /// </summary>
    internal void Consume()
    {
        ColumnOrdinal = -1;
        IsWritten = true;
    }

    sealed class DefaultStrategy : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode(Column column)
            => DictionaryMode.Maybe;

        public bool ShouldDropDictionary<T>(Column column, IReadOnlyDictionary<T, int> dictionary, int totalRowCount,
            int rowsSeen)
            where T : notnull
            => false;

        public bool ShouldStartNewDataPage(Column column, int totalRowCount, int rowsWritten, int currentPageRowCount)
            => false;
    }
}
