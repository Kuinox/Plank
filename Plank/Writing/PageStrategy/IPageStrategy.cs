namespace Plank.Writing.PageStrategy;

public interface IPageStrategy
{
    DictionaryMode GetDictionaryMode();
    DictionarySortOrder GetDictionarySortOrder();
    void SetDictionarySortOrder(DictionarySortOrder sortOrder);

    bool ShouldDropDictionary(int uniqueCount, int totalRowCount, int rowsSeen);

    bool ShouldStartNewDataPage(int totalRowCount, int rowsWritten, int currentPageRowCount);
}
