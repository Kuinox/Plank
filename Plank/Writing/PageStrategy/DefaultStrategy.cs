using System.Collections.Generic;
using Plank.Schema;

namespace Plank.Writing;

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
