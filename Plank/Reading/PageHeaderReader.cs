using Plank.Schema;

namespace Plank.Reading;

static class PageHeaderReader
{
    internal static PageHeader Read(ReadOnlySpan<byte> buffer)
    {
        var reader = new CompactProtocolReader(buffer);
        var previousFieldId = 0;
        var type = PageHeaderType.DataPage;
        var uncompressedPageSize = 0U;
        var compressedPageSize = 0U;
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var repetitionLevelsByteLength = 0U;
        var definitionLevelsByteLength = 0U;
        var nullCount = 0U;
        var isCompressed = false;

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var fieldType, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    type = (PageHeaderType)reader.ReadI32();
                    break;
                case 2:
                    uncompressedPageSize = reader.ReadI32AsU32(max: reader.Remaining);
                    break;
                case 3:
                    compressedPageSize = reader.ReadI32AsU32(max: reader.Remaining);
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
            repetitionLevelsByteLength, definitionLevelsByteLength, nullCount, isCompressed);
    }

    static uint ReadDictionaryHeader(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        var valueCount = 0U;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var fieldType, out var inlineBool))
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
        var previousFieldId = 0;
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var nullCount = 0U;
        var repetitionLevelsByteLength = 0U;
        var definitionLevelsByteLength = 0U;
        var isCompressed = true; // spec default

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var fieldType, out var inlineBool))
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
                    encoding = ParquetMetadataThriftReader.ReadEncoding(reader.ReadI32());
                    break;
                case 5:
                    definitionLevelsByteLength = reader.ReadI32AsU32(max: reader.Remaining);
                    break;
                case 6:
                    repetitionLevelsByteLength = reader.ReadI32AsU32(max: reader.Remaining);
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
