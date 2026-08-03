using Plank.Reading.Internal;
using Plank.Schema;

namespace Plank.Reading;

public readonly record struct PageHeader(
    PageHeaderType Type,
    uint UncompressedPageSize,
    uint CompressedPageSize,
    uint ValueCount,
    EncodingKind Encoding,
    int HeaderLength,
    uint RepetitionLevelsByteLength,
    uint DefinitionLevelsByteLength,
    uint NullCount,
    bool IsCompressed,
    EncodingKind RepetitionLevelEncoding,
    EncodingKind DefinitionLevelEncoding,
    uint RowCount)
{
    internal readonly EncodedStatistics Statistics;

    internal PageHeader(PageHeaderType type, uint uncompressedPageSize, uint compressedPageSize, uint valueCount,
        EncodingKind encoding, int headerLength, uint repetitionLevelsByteLength,
        uint definitionLevelsByteLength, uint nullCount, bool isCompressed,
        EncodingKind repetitionLevelEncoding, EncodingKind definitionLevelEncoding, uint rowCount,
        EncodedStatistics statistics, uint? crc)
        : this(type, uncompressedPageSize, compressedPageSize, valueCount, encoding, headerLength,
            repetitionLevelsByteLength, definitionLevelsByteLength, nullCount, isCompressed,
            repetitionLevelEncoding, definitionLevelEncoding, rowCount)
    {
        Statistics = statistics;
        Crc = crc;
    }

    public uint? Crc { get; init; }
}
