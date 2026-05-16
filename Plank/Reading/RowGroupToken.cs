namespace Plank.Reading;

public readonly struct RowGroupToken
{
    public RowGroupToken(int rowGroupOrdinal, ulong metadataOffset, ulong columnChunkOffset)
    {
        if (rowGroupOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(rowGroupOrdinal), rowGroupOrdinal,
                "Row group ordinal must be non-negative.");

        RowGroupOrdinal = rowGroupOrdinal;
        MetadataOffset = metadataOffset;
        ColumnChunkOffset = columnChunkOffset;
    }

    public int RowGroupOrdinal { get; }

    public ulong MetadataOffset { get; }

    public ulong ColumnChunkOffset { get; }
}
