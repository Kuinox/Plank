using Plank.Schema;

namespace Plank.Reading;

public readonly struct ParquetFileMetadata
{
    public ParquetFileMetadata(ParquetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        Schema = schema;
        FooterOffset = 0;
        FooterLength = 0;
        Version = 0;
    }

    internal ParquetFileMetadata(ParquetSchema schema, long footerOffset, int footerLength, int version)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (footerOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(footerOffset), footerOffset, "Footer offset must be non-negative.");
        if (footerLength < 0)
            throw new ArgumentOutOfRangeException(nameof(footerLength), footerLength, "Footer length must be non-negative.");

        Schema = schema;
        FooterOffset = footerOffset;
        FooterLength = footerLength;
        Version = version;
    }

    public ParquetSchema Schema { get; }

    public long FooterOffset { get; }

    public int FooterLength { get; }

    public int Version { get; }
}
