using System.Threading;
using Plank.Schema;

namespace Plank.Writing.PageStrategy;

sealed class DefaultStrategy : IPageStrategy
{
    readonly DictionaryMode _dictionaryMode;
    int _dictionarySortOrder = (int)DictionarySortOrder.Unknown;

    public DefaultStrategy(Column column)
    {
        var encodings = column.Options.Encodings;
        for (var i = 0; i < encodings.Length; i++)
            if (encodings[i] is EncodingKind.PlainDictionary or EncodingKind.RleDictionary)
            {
                _dictionaryMode = DictionaryMode.Maybe;
                return;
            }

        _dictionaryMode = DictionaryMode.Disabled;
    }

    public DictionaryMode GetDictionaryMode()
        => _dictionaryMode;

    public DictionarySortOrder GetDictionarySortOrder()
        => (DictionarySortOrder)Volatile.Read(ref _dictionarySortOrder);

    public void SetDictionarySortOrder(DictionarySortOrder sortOrder)
    {
        if (sortOrder is < DictionarySortOrder.Unknown or > DictionarySortOrder.Unsorted)
            throw new ArgumentOutOfRangeException(nameof(sortOrder), sortOrder, "Unknown dictionary sort order.");
        Volatile.Write(ref _dictionarySortOrder, (int)sortOrder);
    }

    public bool ShouldDropDictionary(int uniqueCount, int totalRowCount, int rowsSeen)
    {
        if (rowsSeen <= 0 || totalRowCount <= 0)
            return false;

        var minRowsForDecision = Math.Max(16_384, totalRowCount / 8);
        if (rowsSeen < minRowsForDecision && rowsSeen < totalRowCount)
            return false;

        if (uniqueCount <= 1)
            return false;

        if ((long)uniqueCount * 100 >= (long)rowsSeen * 98)
            return true;

        var projectedUniqueCount = (int)Math.Min(totalRowCount,
            ((long)uniqueCount * totalRowCount + rowsSeen - 1) / rowsSeen);
        return (long)projectedUniqueCount * 4 >= (long)totalRowCount * 3
               && (long)uniqueCount * 100 >= (long)rowsSeen * 85;
    }

    public bool ShouldStartNewDataPage(int totalRowCount, int rowsWritten, int currentPageRowCount)
        => false;
}
