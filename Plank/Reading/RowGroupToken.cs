namespace Plank.Reading;

public readonly struct RowGroupToken
{
    internal RowGroupToken(int rowGroupOrdinal, ulong metadataOffset, ulong columnChunkOffset, int footerRowGroupOffset,
        int footerVersion)
    {
        if (rowGroupOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(rowGroupOrdinal), rowGroupOrdinal,
                "Row group ordinal must be non-negative.");

        RowGroupOrdinal = rowGroupOrdinal;
        MetadataOffset = metadataOffset;
        ColumnChunkOffset = columnChunkOffset;
        FooterRowGroupOffset = footerRowGroupOffset;
        FooterVersion = footerVersion;
    }

    public int RowGroupOrdinal { get; }

    public ulong MetadataOffset { get; }

    public ulong ColumnChunkOffset { get; }

    internal int FooterRowGroupOffset { get; }

    internal int FooterVersion { get; }
}
