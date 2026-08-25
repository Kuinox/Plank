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
        ulong offsetIndexOffset, uint offsetIndexLength, ulong bloomFilterOffset, uint bloomFilterLength,
        ParquetColumnChunkEncodings encodings,
        EncodedStatistics statistics)
        : this(rowGroupOrdinal, columnOrdinal, physicalType, compression, dataPageOffset, dictionaryPageOffset,
            totalCompressedSize, totalUncompressedSize, columnIndexOffset, columnIndexLength, offsetIndexOffset,
            offsetIndexLength, encodings)
    {
        ValueCount = valueCount;
        BloomFilterOffset = bloomFilterOffset;
        BloomFilterLength = bloomFilterLength;
        Statistics = statistics;
    }

    public ulong ValueCount { get; init; }

    /// <summary>Gets the absolute byte offset of this chunk's Bloom-filter header, or zero when absent.</summary>
    public ulong BloomFilterOffset { get; init; }

    /// <summary>Gets the serialized Bloom-filter header and bitset length, or zero when unavailable.</summary>
    public uint BloomFilterLength { get; init; }

    /// <summary>Gets whether this column chunk advertises a Bloom filter.</summary>
    public bool HasBloomFilter
        => BloomFilterOffset != 0;

    public ulong ChunkOffset
        => DictionaryPageOffset > 0 && DictionaryPageOffset < DataPageOffset ? DictionaryPageOffset : DataPageOffset;
}
