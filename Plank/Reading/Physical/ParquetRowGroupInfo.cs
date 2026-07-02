using Plank.Reading.Physical.Internal;

namespace Plank.Reading.Physical;

public readonly struct ParquetRowGroupInfo
{
    readonly ParquetFileReader _owner;
    readonly int _version;

    internal ParquetRowGroupInfo(ParquetFileReader owner, int version, int ordinal)
    {
        _owner = owner;
        _version = version;
        Ordinal = ordinal;
    }

    public int Ordinal { get; }
    public ulong RowCount => RowGroup.RowCount;
    public ulong MetadataOffset => RowGroup.MetadataOffset;
    public ulong ColumnChunkOffset => RowGroup.ColumnChunkOffset;
    public int ColumnCount => RowGroup.ColumnCount;

    public ParquetColumnChunkInfo ColumnChunk(int columnOrdinal)
        => _owner.GetColumnChunk(_version, Ordinal, columnOrdinal);

    public ParquetPageCursor OpenPages(int columnOrdinal)
        => new(_owner, _version, Ordinal, columnOrdinal);

    PhysicalRowGroup RowGroup
        => _owner.GetRowGroup(_version, Ordinal);
}
