using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading;

readonly struct InternalColumnChunkMetadata
{
    internal InternalColumnChunkMetadata(long dataPageOffset, long dictionaryPageOffset, long totalCompressedSize,
        CompressionKind compression, EncodingKind[] encodings)
    {
        DataPageOffset = dataPageOffset;
        DictionaryPageOffset = dictionaryPageOffset;
        TotalCompressedSize = totalCompressedSize;
        Compression = compression;
        Encodings = encodings ?? [];
    }

    internal long DataPageOffset { get; }

    internal long DictionaryPageOffset { get; }

    internal long TotalCompressedSize { get; }

    internal CompressionKind Compression { get; }

    internal EncodingKind[] Encodings { get; }

    internal long ChunkOffset
        => DictionaryPageOffset > 0 && DictionaryPageOffset < DataPageOffset ? DictionaryPageOffset : DataPageOffset;
}
