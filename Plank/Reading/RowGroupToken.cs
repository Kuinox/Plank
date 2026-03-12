namespace Plank.Reading;

public readonly struct RowGroupToken
{
    public RowGroupToken(int rowGroupOrdinal, long metadataOffset, long columnChunkOffset)
    {
        if (rowGroupOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(rowGroupOrdinal), rowGroupOrdinal,
                "Row group ordinal must be non-negative.");
        if (metadataOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(metadataOffset), metadataOffset,
                "Metadata offset must be non-negative.");
        if (columnChunkOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(columnChunkOffset), columnChunkOffset,
                "Column chunk offset must be non-negative.");

        RowGroupOrdinal = rowGroupOrdinal;
        MetadataOffset = metadataOffset;
        ColumnChunkOffset = columnChunkOffset;
    }

    public int RowGroupOrdinal { get; }

    public long MetadataOffset { get; }

    public long ColumnChunkOffset { get; }
}
