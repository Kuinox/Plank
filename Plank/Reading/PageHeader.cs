using Plank.Schema;

namespace Plank.Reading;

readonly struct PageHeader
{
    internal PageHeader(PageHeaderType type, uint uncompressedPageSize, uint compressedPageSize, uint valueCount,
        EncodingKind encoding, int headerLength, uint repetitionLevelsByteLength, uint definitionLevelsByteLength,
        uint nullCount, bool isCompressed)
    {
        Type = type;
        UncompressedPageSize = uncompressedPageSize;
        CompressedPageSize = compressedPageSize;
        ValueCount = valueCount;
        Encoding = encoding;
        HeaderLength = headerLength;
        RepetitionLevelsByteLength = repetitionLevelsByteLength;
        DefinitionLevelsByteLength = definitionLevelsByteLength;
        NullCount = nullCount;
        IsCompressed = isCompressed;
    }

    internal PageHeaderType Type { get; }

    internal uint UncompressedPageSize { get; }

    internal uint CompressedPageSize { get; }

    internal uint ValueCount { get; }

    internal EncodingKind Encoding { get; }

    internal int HeaderLength { get; }

    internal uint RepetitionLevelsByteLength { get; }

    internal uint DefinitionLevelsByteLength { get; }

    internal uint NullCount { get; }

    // DataPageV2 only: whether the values portion (after levels) is compressed.
    internal bool IsCompressed { get; }
}
