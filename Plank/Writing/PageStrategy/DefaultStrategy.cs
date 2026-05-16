using System.Threading;
using Plank.Schema;

namespace Plank.Writing.PageStrategy;

sealed class DefaultStrategy : IPageStrategy
{
    readonly DictionaryMode _dictionaryMode;
    readonly uint _targetDataPageSizeBytes;
    int _dictionarySortOrder = (int)DictionarySortOrder.Unknown;

    public DefaultStrategy(Column column, uint targetDataPageSizeBytes)
    {
        _targetDataPageSizeBytes = targetDataPageSizeBytes;
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

    public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
    {
        if (rowsSeen <= 0 || totalRowCount <= 0)
            return false;

        var minRowsForDecision = Math.Max(16_384U, totalRowCount / 8U);
        if (rowsSeen < minRowsForDecision && rowsSeen < totalRowCount)
            return false;

        if (uniqueCount <= 1)
            return false;

        if ((long)uniqueCount * 100 >= (long)rowsSeen * 98)
            return true;

        var projectedUniqueCount = (uint)Math.Min(totalRowCount,
            ((ulong)uniqueCount * totalRowCount + rowsSeen - 1U) / rowsSeen);
        return (long)projectedUniqueCount * 4 >= (long)totalRowCount * 3
               && (long)uniqueCount * 100 >= (long)rowsSeen * 85;
    }

    public bool TryGetTargetDataPageSizeBytes(out uint sizeBytes)
    {
        sizeBytes = _targetDataPageSizeBytes;
        return true;
    }

    public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
        => false;
}
