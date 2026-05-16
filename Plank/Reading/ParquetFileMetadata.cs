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

    internal ParquetFileMetadata(ParquetSchema schema, ulong footerOffset, uint footerLength, int version)
    {
        ArgumentNullException.ThrowIfNull(schema);

        Schema = schema;
        FooterOffset = footerOffset;
        FooterLength = footerLength;
        Version = version;
    }

    public ParquetSchema Schema { get; }

    public ulong FooterOffset { get; }

    public uint FooterLength { get; }

    public int Version { get; }
}
