using Plank.Schema;

namespace Plank.Writing;

internal struct ColumnChunkMetadata
{
    internal long DataPageOffset;
    internal long DictionaryPageOffset;
    internal uint ValueCount;
    internal long TotalUncompressedSize;
    internal long TotalCompressedSize;
    internal EncodingKind DataEncoding;
    internal CompressionKind Compression;
    internal ColumnStatistics Statistics;
    internal bool HasDictionaryPage;
    internal long ColumnIndexOffset;
    internal uint ColumnIndexLength;
    internal long OffsetIndexOffset;
    internal uint OffsetIndexLength;
    internal PageIndex PageIndex;
}
