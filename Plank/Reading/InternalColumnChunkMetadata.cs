using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading;

readonly struct InternalColumnChunkMetadata
{
    internal InternalColumnChunkMetadata(ulong dataPageOffset, ulong dictionaryPageOffset, ulong totalCompressedSize,
        ulong totalUncompressedSize, CompressionKind compression, EncodingKind[] encodings, string path,
        ParquetPhysicalType physicalType,
        ulong columnIndexOffset = 0, uint columnIndexLength = 0, ulong offsetIndexOffset = 0,
        uint offsetIndexLength = 0)
    {
        Path = path;
        PhysicalType = physicalType;
        DataPageOffset = dataPageOffset;
        DictionaryPageOffset = dictionaryPageOffset;
        TotalCompressedSize = totalCompressedSize;
        TotalUncompressedSize = totalUncompressedSize;
        Compression = compression;
        Encodings = encodings ?? [];
        ColumnIndexOffset = columnIndexOffset;
        ColumnIndexLength = columnIndexLength;
        OffsetIndexOffset = offsetIndexOffset;
        OffsetIndexLength = offsetIndexLength;
    }

    internal string Path { get; }

    internal ParquetPhysicalType PhysicalType { get; }

    internal ulong DataPageOffset { get; }

    internal ulong DictionaryPageOffset { get; }

    internal ulong TotalCompressedSize { get; }

    internal ulong TotalUncompressedSize { get; }

    internal CompressionKind Compression { get; }

    internal EncodingKind[] Encodings { get; }

    internal ulong ColumnIndexOffset { get; }

    internal uint ColumnIndexLength { get; }

    internal ulong OffsetIndexOffset { get; }

    internal uint OffsetIndexLength { get; }

    internal ulong ChunkOffset
        => DictionaryPageOffset > 0 && DictionaryPageOffset < DataPageOffset ? DictionaryPageOffset : DataPageOffset;
}
