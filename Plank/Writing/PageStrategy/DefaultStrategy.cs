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
                return DictionaryMode.Maybe;

        return DictionaryMode.Disabled;
    }

    public bool ShouldDropDictionary<T>(Column column, IReadOnlyDictionary<T, int> dictionary, int totalRowCount,
        int rowsSeen)
        where T : notnull
    {
        if (rowsSeen <= 0 || totalRowCount <= 0)
            return false;

        var minRowsForDecision = Math.Max(16_384, totalRowCount / 8);
        if (rowsSeen < minRowsForDecision && rowsSeen < totalRowCount)
            return false;

        var uniqueCount = dictionary.Count;
        if (uniqueCount <= 1)
            return false;

        if ((long)uniqueCount * 100 >= (long)rowsSeen * 98)
            return true;

        var projectedUniqueCount = (int)Math.Min(totalRowCount,
            ((long)uniqueCount * totalRowCount + rowsSeen - 1) / rowsSeen);
        return (long)projectedUniqueCount * 4 >= (long)totalRowCount * 3
               && (long)uniqueCount * 100 >= (long)rowsSeen * 85;
    }

    public bool ShouldStartNewDataPage(Column column, int totalRowCount, int rowsWritten, int currentPageRowCount)
        => false;
}
