namespace Plank.Reading.Logical.Internal;

readonly struct InternalRowGroupMetadata
{
    internal InternalRowGroupMetadata(int rowGroupOrdinal, ulong metadataOffset, ulong columnChunkOffset,
        ulong rowCount, InternalColumnChunkMetadata[] columns, int footerVersion = 0)
    {
        RowGroupOrdinal = rowGroupOrdinal;
        MetadataOffset = metadataOffset;
        ColumnChunkOffset = columnChunkOffset;
        RowCount = rowCount;
        Columns = columns ?? [];
        FooterVersion = footerVersion;
    }

    internal int RowGroupOrdinal { get; }

    internal ulong MetadataOffset { get; }

    internal ulong ColumnChunkOffset { get; }

    internal ulong RowCount { get; }

    internal InternalColumnChunkMetadata[] Columns { get; }

    internal int FooterVersion { get; }
}
