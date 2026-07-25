namespace Plank.Writing.PageStrategy;

/// <summary>Defines the page-writing policy for a leaf column.</summary>
/// <remarks>
/// Implementations may be shared by multiple writers and must not store writer-specific observations. Plank keeps
/// mutable per-column state in the owning writer for the full writer lifetime.
/// </remarks>
public interface IPageStrategy
{
    DictionaryMode GetDictionaryMode();

    bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen);

    bool TryGetTargetDataPageSizeBytes(out uint sizeBytes)
    {
        sizeBytes = 0;
        return false;
    }

    bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount);
}
