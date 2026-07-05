namespace Plank.Reading.Logical;

public readonly struct ParquetFooter
{
    readonly ParquetReader _reader;

    internal ParquetFooter(ParquetReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
    }

    public ParquetFileMetadata Metadata
        => _reader.Metadata;

    public RowGroupTokenEnumerable EnumerateRowGroups()
        => _reader.EnumerateRowGroups();
}
