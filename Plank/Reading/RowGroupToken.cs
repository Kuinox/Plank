namespace Plank.Reading;

public readonly struct RowGroupToken
{
    readonly ParquetReader? _reader;
    readonly InternalRowGroupMetadata _metadata;

    internal RowGroupToken(ParquetReader reader, InternalRowGroupMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rowGroupOrdinal = metadata.RowGroupOrdinal;
        if (rowGroupOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(rowGroupOrdinal), rowGroupOrdinal,
                "Row group ordinal must be non-negative.");

        _reader = reader;
        _metadata = metadata;
    }

    public int RowGroupOrdinal
        => _metadata.RowGroupOrdinal;

    public ulong MetadataOffset
        => _metadata.MetadataOffset;

    public ulong ColumnChunkOffset
        => _metadata.ColumnChunkOffset;

    public ulong RowCount
        => _metadata.RowCount;

    internal ParquetReader? Reader
        => _reader;

    internal InternalRowGroupMetadata Metadata
        => _metadata;

    internal int FooterVersion
        => _metadata.FooterVersion;
}
