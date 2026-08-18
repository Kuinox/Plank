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

    /// <summary>Gets the number of rows to write to the next data page.</summary>
    /// <remarks>
    /// The result must be greater than zero and no greater than <paramref name="totalRowCount"/> minus
    /// <paramref name="rowsWritten"/>.
    /// </remarks>
    uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten);
}
