using Plank.Schema;
using Plank.Reading.Internal;

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
    internal readonly EncodedStatistics Statistics;

    internal ParquetColumnChunkInfo(int rowGroupOrdinal, int columnOrdinal, ParquetPhysicalType physicalType,
        CompressionKind compression, ulong valueCount, ulong dataPageOffset, ulong dictionaryPageOffset,
        ulong totalCompressedSize, ulong totalUncompressedSize, ulong columnIndexOffset, uint columnIndexLength,
        ulong offsetIndexOffset, uint offsetIndexLength, ParquetColumnChunkEncodings encodings,
        EncodedStatistics statistics)
        : this(rowGroupOrdinal, columnOrdinal, physicalType, compression, dataPageOffset, dictionaryPageOffset,
            totalCompressedSize, totalUncompressedSize, columnIndexOffset, columnIndexLength, offsetIndexOffset,
            offsetIndexLength, encodings)
    {
        ValueCount = valueCount;
        Statistics = statistics;
    }

    public ulong ValueCount { get; init; }

    public ulong ChunkOffset
        => DictionaryPageOffset > 0 && DictionaryPageOffset < DataPageOffset ? DictionaryPageOffset : DataPageOffset;
}
