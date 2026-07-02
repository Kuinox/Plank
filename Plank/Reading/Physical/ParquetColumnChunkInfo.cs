using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading.Physical;

public readonly record struct ParquetColumnChunkInfo(
    int RowGroupOrdinal,
    int ColumnOrdinal,
    ParquetPhysicalType PhysicalType,
    CompressionKind Compression,
    ulong DataPageOffset,
    ulong DictionaryPageOffset,
    ulong TotalCompressedSize,
    ulong TotalUncompressedSize,
    ulong ColumnIndexOffset,
    uint ColumnIndexLength,
    ulong OffsetIndexOffset,
    uint OffsetIndexLength,
    ParquetColumnChunkEncodings Encodings)
{
    public ulong ChunkOffset
        => DictionaryPageOffset > 0 && DictionaryPageOffset < DataPageOffset ? DictionaryPageOffset : DataPageOffset;
}
