namespace Plank.Reading;

readonly struct InternalRowGroupMetadata
{
    internal InternalRowGroupMetadata(int rowGroupOrdinal, long metadataOffset, long columnChunkOffset,
        InternalColumnChunkMetadata[] columns)
    {
        RowGroupOrdinal = rowGroupOrdinal;
        MetadataOffset = metadataOffset;
        ColumnChunkOffset = columnChunkOffset;
        Columns = columns ?? [];
    }

    internal int RowGroupOrdinal { get; }

    internal long MetadataOffset { get; }

    internal long ColumnChunkOffset { get; }

    internal InternalColumnChunkMetadata[] Columns { get; }
}
