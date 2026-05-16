namespace Plank.Writing.PageStrategy;

public interface IPageStrategy
{
    DictionaryMode GetDictionaryMode();
    DictionarySortOrder GetDictionarySortOrder();
    void SetDictionarySortOrder(DictionarySortOrder sortOrder);

    bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen);

    bool TryGetTargetDataPageSizeBytes(out uint sizeBytes)
    {
        sizeBytes = 0;
        return false;
    }

    bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount);
}
