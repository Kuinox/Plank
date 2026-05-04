using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading;

readonly struct InternalColumnChunkMetadata
{
    internal InternalColumnChunkMetadata(long dataPageOffset, long dictionaryPageOffset, long totalCompressedSize,
        CompressionKind compression, EncodingKind[] encodings, long columnIndexOffset = 0, int columnIndexLength = 0,
        long offsetIndexOffset = 0, int offsetIndexLength = 0)
    {
        DataPageOffset = dataPageOffset;
        DictionaryPageOffset = dictionaryPageOffset;
        TotalCompressedSize = totalCompressedSize;
        Compression = compression;
        Encodings = encodings ?? [];
        ColumnIndexOffset = columnIndexOffset;
        ColumnIndexLength = columnIndexLength;
        OffsetIndexOffset = offsetIndexOffset;
        OffsetIndexLength = offsetIndexLength;
    }

    internal long DataPageOffset { get; }

    internal long DictionaryPageOffset { get; }

    internal long TotalCompressedSize { get; }

    internal CompressionKind Compression { get; }

    internal EncodingKind[] Encodings { get; }

    internal long ColumnIndexOffset { get; }

    internal int ColumnIndexLength { get; }

    internal long OffsetIndexOffset { get; }

    internal int OffsetIndexLength { get; }

    internal long ChunkOffset
        => DictionaryPageOffset > 0 && DictionaryPageOffset < DataPageOffset ? DictionaryPageOffset : DataPageOffset;
}
