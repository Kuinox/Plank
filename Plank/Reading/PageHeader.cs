using Plank.Schema;

namespace Plank.Reading;

readonly struct PageHeader
{
    internal PageHeader(PageHeaderType type, int uncompressedPageSize, int compressedPageSize, int valueCount,
        EncodingKind encoding, int headerLength)
    {
        Type = type;
        UncompressedPageSize = uncompressedPageSize;
        CompressedPageSize = compressedPageSize;
        ValueCount = valueCount;
        Encoding = encoding;
        HeaderLength = headerLength;
    }

    internal PageHeaderType Type { get; }

    internal int UncompressedPageSize { get; }

    internal int CompressedPageSize { get; }

    internal int ValueCount { get; }

    internal EncodingKind Encoding { get; }

    internal int HeaderLength { get; }
}
