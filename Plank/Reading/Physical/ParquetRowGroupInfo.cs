namespace Plank.Reading.Physical;

public readonly struct ParquetRowGroupInfo
{
    internal readonly int ColumnStart;
    internal readonly int SortingColumnStart;
    internal readonly int MetadataLength;

    internal ParquetRowGroupInfo(int ordinal, ulong metadataOffset, ulong columnChunkOffset, ulong rowCount,
        int columnStart, int columnCount, int sortingColumnStart, int sortingColumnCount, int metadataLength)
    {
        Ordinal = ordinal;
        MetadataOffset = metadataOffset;
        ColumnChunkOffset = columnChunkOffset;
        RowCount = rowCount;
        ColumnStart = columnStart;
        ColumnCount = columnCount;
        SortingColumnStart = sortingColumnStart;
        SortingColumnCount = sortingColumnCount;
        MetadataLength = metadataLength;
    }

    public int Ordinal { get; }
    public ulong MetadataOffset { get; }
    public ulong ColumnChunkOffset { get; }
    public ulong RowCount { get; }
    public int ColumnCount { get; }
    public int SortingColumnCount { get; }
}
