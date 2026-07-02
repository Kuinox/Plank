using Plank.Schema;

namespace Plank.Reading;

static class PageHeaderReader
{
    internal static PageHeader Read(ReadOnlySpan<byte> buffer, uint maxUncompressedPageSize = uint.MaxValue)
    {
        var reader = new CompactProtocolReader(buffer);
        var type = PageHeaderType.DataPage;
        var uncompressedPageSize = 0U;
        var compressedPageSize = 0U;
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var repetitionLevelsByteLength = 0U;
        var definitionLevelsByteLength = 0U;
        var nullCount = 0U;
        var isCompressed = false;
        var repetitionLevelEncoding = EncodingKind.Rle;
        var definitionLevelEncoding = EncodingKind.Rle;

        reader.BeginStruct();

        while (reader.TryReadFieldHeader(out var fieldId, out var fieldType, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    type = (PageHeaderType)reader.ReadI32();
                    break;
                case 2:
                    uncompressedPageSize = reader.ReadI32AsU32(max: maxUncompressedPageSize);
                    break;
                case 3:
                    compressedPageSize = reader.ReadI32AsU32();
                    break;
                case 5:
                    (valueCount, encoding, repetitionLevelEncoding, definitionLevelEncoding)
                        = ReadDataPageHeader(ref reader);
                    break;
                case 7:
                    valueCount = ReadDictionaryHeader(ref reader);
                    break;
                case 8:
                    (valueCount, encoding, nullCount, repetitionLevelsByteLength, definitionLevelsByteLength, isCompressed)
                        = ReadDataPageV2Header(ref reader);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        return new PageHeader(type, uncompressedPageSize, compressedPageSize, valueCount, encoding, reader.Offset,
            repetitionLevelsByteLength, definitionLevelsByteLength, nullCount, isCompressed, repetitionLevelEncoding,
            definitionLevelEncoding);
    }

    static (uint ValueCount, EncodingKind Encoding, EncodingKind RepetitionLevelEncoding,
        EncodingKind DefinitionLevelEncoding) ReadDataPageHeader(ref CompactProtocolReader reader)
    {
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var repetitionLevelEncoding = EncodingKind.Rle;
        var definitionLevelEncoding = EncodingKind.Rle;

        reader.BeginStruct();

        while (reader.TryReadFieldHeader(out var fieldId, out var fieldType, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    valueCount = reader.ReadI32AsU32();
                    break;
                case 2:
                    encoding = ParquetThriftConversions.ReadEncoding(reader.ReadI32());
                    break;
                case 3:
                    if (fieldType == CompactProtocolType.I32)
                        definitionLevelEncoding = ParquetThriftConversions.ReadEncoding(reader.ReadI32());
                    else
                        reader.Skip(fieldType, inlineBool);
                    break;
                case 4:
                    if (fieldType == CompactProtocolType.I32)
                        repetitionLevelEncoding = ParquetThriftConversions.ReadEncoding(reader.ReadI32());
                    else
                        reader.Skip(fieldType, inlineBool);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        return (valueCount, encoding, repetitionLevelEncoding, definitionLevelEncoding);
    }

    static uint ReadDictionaryHeader(ref CompactProtocolReader reader)
    {
        var valueCount = 0U;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var fieldType, out var inlineBool))
        {
            if (fieldId == 1)
                valueCount = reader.ReadI32AsU32();
            else
                reader.Skip(fieldType, inlineBool);
        }

        return valueCount;
    }

    static (uint ValueCount, EncodingKind Encoding, uint NullCount, uint RepetitionLevelsByteLength,
        uint DefinitionLevelsByteLength, bool IsCompressed) ReadDataPageV2Header(ref CompactProtocolReader reader)
    {
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var nullCount = 0U;
        var repetitionLevelsByteLength = 0U;
        var definitionLevelsByteLength = 0U;
        var isCompressed = true; // spec default

        reader.BeginStruct();

        while (reader.TryReadFieldHeader(out var fieldId, out var fieldType, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    valueCount = reader.ReadI32AsU32();
                    break;
                case 2:
                    nullCount = reader.ReadI32AsU32();
                    break;
                case 4:
                    encoding = ParquetThriftConversions.ReadEncoding(reader.ReadI32());
                    break;
                case 5:
                    definitionLevelsByteLength = reader.ReadI32AsU32();
                    break;
                case 6:
                    repetitionLevelsByteLength = reader.ReadI32AsU32();
                    break;
                case 7:
                    isCompressed = reader.ReadBool(inlineBool);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        return (valueCount, encoding, nullCount, repetitionLevelsByteLength, definitionLevelsByteLength, isCompressed);
    }
}
