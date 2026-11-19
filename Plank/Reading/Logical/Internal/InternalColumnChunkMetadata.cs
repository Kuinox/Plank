using Plank.Reading.Physical;
using Plank.Reading.Internal;
using Plank.Schema;

namespace Plank.Reading.Logical.Internal;

readonly struct InternalColumnChunkMetadata
{
    internal InternalColumnChunkMetadata(ParquetColumnChunkInfo chunk, EncodingKind[] encodings)
    {
        PhysicalType = chunk.PhysicalType;
        PhysicalColumnOrdinal = chunk.ColumnOrdinal;
        DataPageOffset = chunk.DataPageOffset;
        DictionaryPageOffset = chunk.DictionaryPageOffset;
        TotalCompressedSize = chunk.TotalCompressedSize;
        TotalUncompressedSize = chunk.TotalUncompressedSize;
        ValueCount = chunk.ValueCount;
        Compression = chunk.Compression;
        Encodings = encodings;
        ColumnIndexOffset = chunk.ColumnIndexOffset;
        ColumnIndexLength = chunk.ColumnIndexLength;
        OffsetIndexOffset = chunk.OffsetIndexOffset;
        OffsetIndexLength = chunk.OffsetIndexLength;
        BloomFilterOffset = chunk.BloomFilterOffset;
        BloomFilterLength = chunk.BloomFilterLength;
        Statistics = chunk.Statistics;
    }

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
        => DictionaryPageOffset > 0 && (DataPageOffset == 0 || DictionaryPageOffset < DataPageOffset)
            ? DictionaryPageOffset
            : DataPageOffset;
}
