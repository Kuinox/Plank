namespace Plank.Reading.Logical;

/// <summary>Provides a collection- or callback-scoped view of one data page's metadata.</summary>
public readonly ref struct ParquetDataPageMetadata
{
    internal ParquetDataPageMetadata(int rowGroupIndex, int pageOrdinal, ulong offset, uint compressedSize,
        ulong? firstRowIndex, ulong? rowCount, bool? isNullPage, ParquetStatistics statistics)
    {
        RowGroupIndex = rowGroupIndex;
        PageOrdinal = pageOrdinal;
        Offset = offset;
        CompressedSize = compressedSize;
        FirstRowIndex = firstRowIndex;
        RowCount = rowCount;
        IsNullPage = isNullPage;
        Statistics = statistics;
    }

    public int RowGroupIndex { get; }

    public int PageOrdinal { get; }

    public ulong Offset { get; }

    public uint CompressedSize { get; }

    public ulong? FirstRowIndex { get; }

    public ulong? RowCount { get; }

    public bool? IsNullPage { get; }

    public ParquetStatistics Statistics { get; }
}
