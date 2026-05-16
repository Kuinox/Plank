using System.Threading;

namespace Plank.Writing.PageStrategy;

public sealed class ForceDictionaryPageStrategy : IPageStrategy
{
    public static ForceDictionaryPageStrategy Shared
        => new();
    int _dictionarySortOrder = (int)DictionarySortOrder.Unknown;

    ForceDictionaryPageStrategy()
    {
    }

    public DictionaryMode GetDictionaryMode()
        => DictionaryMode.Forced;

    public DictionarySortOrder GetDictionarySortOrder()
        => (DictionarySortOrder)Volatile.Read(ref _dictionarySortOrder);

    public void SetDictionarySortOrder(DictionarySortOrder sortOrder)
    {
        if (sortOrder is < DictionarySortOrder.Unknown or > DictionarySortOrder.Unsorted)
            throw new ArgumentOutOfRangeException(nameof(sortOrder), sortOrder, "Unknown dictionary sort order.");
        Volatile.Write(ref _dictionarySortOrder, (int)sortOrder);
    }

    public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
        => false;

    public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
        => false;
}
