namespace Plank.Reading;

readonly struct InternalRowGroupMetadata
{
    internal InternalRowGroupMetadata(int rowGroupOrdinal, long metadataOffset, long columnChunkOffset,
        long rowCount, InternalColumnChunkMetadata[] columns)
    {
        RowGroupOrdinal = rowGroupOrdinal;
        MetadataOffset = metadataOffset;
        ColumnChunkOffset = columnChunkOffset;
        RowCount = rowCount;
        Columns = columns ?? [];
    }

    internal int RowGroupOrdinal { get; }

    internal long MetadataOffset { get; }

    internal long ColumnChunkOffset { get; }

    internal long RowCount { get; }

    internal InternalColumnChunkMetadata[] Columns { get; }
}
