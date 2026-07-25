namespace Plank.Writing.PageStrategy;

public sealed class ForceDictionaryPageStrategy : IPageStrategy
{
    public static ForceDictionaryPageStrategy Shared
        { get; } = new();

    ForceDictionaryPageStrategy()
    {
    }

    public DictionaryMode GetDictionaryMode()
        => DictionaryMode.Forced;

    public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
        => false;

    public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
        => false;
}
