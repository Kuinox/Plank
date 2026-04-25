using Plank.Schema;

namespace Plank.Reading;

readonly struct PageHeader
{
    internal PageHeader(PageHeaderType type, int uncompressedPageSize, int compressedPageSize, int valueCount,
        EncodingKind encoding, int headerLength, int repetitionLevelsByteLength, int definitionLevelsByteLength,
        int nullCount, bool isCompressed)
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

    internal int UncompressedPageSize { get; }

    internal int CompressedPageSize { get; }

    internal int ValueCount { get; }

    internal EncodingKind Encoding { get; }

    internal int HeaderLength { get; }

    internal int RepetitionLevelsByteLength { get; }

    internal int DefinitionLevelsByteLength { get; }

    internal int NullCount { get; }

    // DataPageV2 only: whether the values portion (after levels) is compressed.
    internal bool IsCompressed { get; }
}
