using System.Collections.Generic;
using Plank.Schema;

namespace Plank.Writing;

public sealed class SerializedColumn
{
    static readonly IPageStrategy _defaultPageStrategy = new DefaultStrategy();

    readonly ParquetWriter _owner;
    
    internal readonly PageList Pages;
    internal int ColumnOrdinal;
    internal int RowCount;
    bool _isWritten;

    internal SerializedColumn(ParquetWriter owner, int initialPageCapacity)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Pages = new PageList(initialPageCapacity);
        _owner = owner;
        ColumnOrdinal = -1;
        RowCount = 0;
        _isWritten = false;
    }

    public void Serialize<T>(Column column, ReadOnlySpan<T> values)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!_isWritten && ColumnOrdinal >= 0)
            throw new InvalidOperationException(
                "SerializedColumn already contains pending data. Call RowGroupWriter.Write(serialized) before Serialize(...) again.");

        var columnOrdinal = _owner.GetColumnOrdinal(column);
        Pages.Clear();
        ColumnOrdinal = columnOrdinal;
        RowCount = values.Length;
        _isWritten = false;

        Encoding.Encode(_owner.BufferWriters, column, values, _defaultPageStrategy, Pages);
    }

    /// <summary>
    /// Invalidates the current prepared payload to avoid missuses.
    /// </summary>
    internal void Consume()
    {
        ColumnOrdinal = -1;
        _isWritten = true;
    }

    sealed class DefaultStrategy : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode(Column column)
        {
            var encodings = column.Options.Encodings;
            for (var i = 0; i < encodings.Length; i++)
                if (encodings[i] is EncodingKind.PlainDictionary or EncodingKind.RleDictionary)
                    return DictionaryMode.Forced;

            return DictionaryMode.Disabled;
        }

        public bool ShouldDropDictionary<T>(Column column, IReadOnlyDictionary<T, int> dictionary, int totalRowCount,
            int rowsSeen)
            where T : notnull
            => false;

        public bool ShouldStartNewDataPage(Column column, int totalRowCount, int rowsWritten, int currentPageRowCount)
            => false;
    }
}
