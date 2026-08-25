using Plank.Reading.Physical;
using Plank.Reading.Internal;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

readonly struct InternalColumnChunkMetadata
{
    internal InternalColumnChunkMetadata(ulong dataPageOffset, ulong dictionaryPageOffset, ulong totalCompressedSize,
        ulong totalUncompressedSize, ulong valueCount, CompressionKind compression, EncodingKind[] encodings, string path,
        ParquetPhysicalType physicalType,
        ulong columnIndexOffset = 0, uint columnIndexLength = 0, ulong offsetIndexOffset = 0,
        uint offsetIndexLength = 0, ulong bloomFilterOffset = 0, uint bloomFilterLength = 0,
        int physicalColumnOrdinal = -1,
        EncodedStatistics statistics = default)
    {
        Path = path;
        PhysicalType = physicalType;
        PhysicalColumnOrdinal = physicalColumnOrdinal;
        DataPageOffset = dataPageOffset;
        DictionaryPageOffset = dictionaryPageOffset;
        TotalCompressedSize = totalCompressedSize;
        TotalUncompressedSize = totalUncompressedSize;
        ValueCount = valueCount;
        Compression = compression;
        Encodings = encodings ?? [];
        ColumnIndexOffset = columnIndexOffset;
        ColumnIndexLength = columnIndexLength;
        OffsetIndexOffset = offsetIndexOffset;
        OffsetIndexLength = offsetIndexLength;
        BloomFilterOffset = bloomFilterOffset;
        BloomFilterLength = bloomFilterLength;
        Statistics = statistics;
    }

    internal InternalColumnChunkMetadata(ParquetColumnChunkInfo chunk, EncodingKind[] encodings, string path)
        : this(chunk.DataPageOffset, chunk.DictionaryPageOffset, chunk.TotalCompressedSize,
            chunk.TotalUncompressedSize, chunk.ValueCount, chunk.Compression, encodings, path, chunk.PhysicalType,
            chunk.ColumnIndexOffset, chunk.ColumnIndexLength, chunk.OffsetIndexOffset, chunk.OffsetIndexLength,
            chunk.BloomFilterOffset, chunk.BloomFilterLength, chunk.ColumnOrdinal, chunk.Statistics)
    {
    }

    internal string Path { get; }

    internal ParquetPhysicalType PhysicalType { get; }

    internal int PhysicalColumnOrdinal { get; }

    internal ulong DataPageOffset { get; }

    internal ulong DictionaryPageOffset { get; }

    internal ulong TotalCompressedSize { get; }

    internal ulong TotalUncompressedSize { get; }

    internal ulong ValueCount { get; }

    internal CompressionKind Compression { get; }

    internal EncodingKind[] Encodings { get; }

    internal ulong ColumnIndexOffset { get; }

    internal uint ColumnIndexLength { get; }

    internal ulong OffsetIndexOffset { get; }

    internal uint OffsetIndexLength { get; }

    internal ulong BloomFilterOffset { get; }

    internal uint BloomFilterLength { get; }

    internal EncodedStatistics Statistics { get; }

    internal ulong ChunkOffset
        => DictionaryPageOffset > 0 && DictionaryPageOffset < DataPageOffset ? DictionaryPageOffset : DataPageOffset;
}
