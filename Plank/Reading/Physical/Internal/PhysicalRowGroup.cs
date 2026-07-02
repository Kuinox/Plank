namespace Plank.Reading.Physical.Internal;

readonly struct PhysicalRowGroup
{
    internal PhysicalRowGroup(int ordinal, ulong metadataOffset, ulong columnChunkOffset, ulong rowCount,
        int columnStart, int columnCount)
    {
        Ordinal = ordinal;
        MetadataOffset = metadataOffset;
        ColumnChunkOffset = columnChunkOffset;
        RowCount = rowCount;
        ColumnStart = columnStart;
        ColumnCount = columnCount;
    }

    internal int Ordinal { get; }
    internal ulong MetadataOffset { get; }
    internal ulong ColumnChunkOffset { get; }
    internal ulong RowCount { get; }
    internal int ColumnStart { get; }
    internal int ColumnCount { get; }
}
