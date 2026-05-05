namespace Plank.Writing.PageStrategy;

public interface IPageStrategy
{
    DictionaryMode GetDictionaryMode();
    DictionarySortOrder GetDictionarySortOrder();
    void SetDictionarySortOrder(DictionarySortOrder sortOrder);

    bool ShouldDropDictionary(int uniqueCount, int totalRowCount, int rowsSeen);

    bool TryGetTargetDataPageSizeBytes(out int sizeBytes)
    {
        sizeBytes = 0;
        return false;
    }

    bool ShouldStartNewDataPage(int totalRowCount, int rowsWritten, int currentPageRowCount);
}
