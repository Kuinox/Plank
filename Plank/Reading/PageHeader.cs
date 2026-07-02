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
    EncodingKind DefinitionLevelEncoding);
