using Plank.Reading.Internal;
using Plank.Schema;

namespace Plank.Reading;

static class PageHeaderReader
{
    internal static PageHeader Read(ReadOnlySpan<byte> buffer, uint maxUncompressedPageSize = uint.MaxValue)
        => Read(buffer, maxUncompressedPageSize, bufferMayBeTruncated: false);

    internal static int GetRequiredBufferLength(int length, int missingBytes, int maximumLength)
    {
        if (missingBytes <= 0 || missingBytes > maximumLength - length)
            throw new CorruptParquetException("Page header exceeds its remaining column chunk or indexed page length.");
        return length + missingBytes;
    }

    /// <summary>
    /// Parses a header out of a buffer that may not hold all of it yet, reporting
    /// how many more bytes are needed instead of failing.
    /// </summary>
    /// <remarks>
    /// A page header does not carry its own length, so a caller scanning headers
    /// has to grow its window until one parses. <paramref name="missingBytes"/>
    /// is a lower bound on the shortfall — the field the parse stopped on needs
    /// at least that many more — which lets the caller extend by exactly that and
    /// never read a byte beyond the header it is after.
    /// </remarks>
    internal static bool TryRead(ReadOnlySpan<byte> buffer, uint maxUncompressedPageSize,
        out PageHeader header, out int missingBytes)
    {
        try
        {
            header = Read(buffer, maxUncompressedPageSize, bufferMayBeTruncated: true);
            missingBytes = 0;
            return true;
        }
        catch (CompactProtocolTruncatedException truncated)
        {
            header = default;
            missingBytes = truncated.MissingBytes;
            return false;
        }
    }

    static PageHeader Read(ReadOnlySpan<byte> buffer, uint maxUncompressedPageSize, bool bufferMayBeTruncated)
    {
        var reader = new CompactProtocolReader(buffer, bufferMayBeTruncated);
        var requiredFields = new RequiredFields();
        var type = PageHeaderType.DataPage;
        var uncompressedPageSize = 0U;
        var compressedPageSize = 0U;
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var repetitionLevelsByteLength = 0U;
        var definitionLevelsByteLength = 0U;
        var nullCount = 0U;
        var rowCount = 0U;
        var isCompressed = false;
        uint? crc = null;
        var repetitionLevelEncoding = EncodingKind.Rle;
        var definitionLevelEncoding = EncodingKind.Rle;
        var statistics = default(EncodedStatistics);

        reader.BeginRootStruct();

        while (reader.TryReadFieldHeader(out var fieldId, out var fieldType, out var inlineBool))
        {
            requiredFields.Add(fieldId);
            switch (fieldId)
            {
                case 1:
                    type = (PageHeaderType)reader.ReadI32(fieldType);
                    break;
                case 2:
                    uncompressedPageSize = reader.ReadI32AsU32(fieldType, max: maxUncompressedPageSize);
                    break;
                case 3:
                    compressedPageSize = reader.ReadI32AsU32(fieldType);
                    break;
                case 4:
                    crc = unchecked((uint)reader.ReadI32(fieldType));
                    break;
                case 5:
                    (valueCount, encoding, repetitionLevelEncoding, definitionLevelEncoding, statistics)
                        = ReadDataPageHeader(ref reader, fieldType);
                    break;
                case 6:
                    reader.SkipStruct(fieldType);
                    break;
                case 7:
                    valueCount = ReadDictionaryHeader(ref reader, fieldType);
                    break;
                case 8:
                    (valueCount, encoding, nullCount, rowCount, repetitionLevelsByteLength,
                        definitionLevelsByteLength, isCompressed, statistics)
                        = ReadDataPageV2Header(ref reader, fieldType);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        requiredFields.Validate(ParquetRequiredFields.PageHeader);
        const uint pageHeaderMask = (1u << 5) | (1u << 6) | (1u << 7) | (1u << 8);
        var expectedHeader = type switch
        {
            PageHeaderType.DataPage => 1u << 5,
            PageHeaderType.IndexPage => 1u << 6,
            PageHeaderType.DictionaryPage => 1u << 7,
            PageHeaderType.DataPageV2 => 1u << 8,
            _ => 0u
        };
        if (expectedHeader != 0 && (requiredFields.Seen & pageHeaderMask) != expectedHeader)
            throw new CorruptParquetException($"Page type '{type}' does not match its page header.");

        return new PageHeader(type, uncompressedPageSize, compressedPageSize, valueCount, encoding, reader.Offset,
            repetitionLevelsByteLength, definitionLevelsByteLength, nullCount, isCompressed, repetitionLevelEncoding,
            definitionLevelEncoding, rowCount, statistics, crc);
    }

    static (uint ValueCount, EncodingKind Encoding, EncodingKind RepetitionLevelEncoding,
        EncodingKind DefinitionLevelEncoding, EncodedStatistics Statistics)
        ReadDataPageHeader(ref CompactProtocolReader reader, CompactProtocolType structType)
    {
        var requiredFields = new RequiredFields();
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var repetitionLevelEncoding = EncodingKind.Rle;
        var definitionLevelEncoding = EncodingKind.Rle;
        var statistics = default(EncodedStatistics);

        reader.BeginStruct(structType);

        while (reader.TryReadFieldHeader(out var fieldId, out var fieldType, out var inlineBool))
        {
            requiredFields.Add(fieldId);
            switch (fieldId)
            {
                case 1:
                    valueCount = reader.ReadI32AsU32(fieldType);
                    break;
                case 2:
                    encoding = ParquetThriftConversions.ReadEncoding(reader.ReadI32(fieldType));
                    break;
                case 3:
                    definitionLevelEncoding = ParquetThriftConversions.ReadEncoding(reader.ReadI32(fieldType));
                    break;
                case 4:
                    repetitionLevelEncoding = ParquetThriftConversions.ReadEncoding(reader.ReadI32(fieldType));
                    break;
                case 5:
                    statistics = StatisticsThriftReader.Read(ref reader, fieldType);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        requiredFields.Validate(ParquetRequiredFields.DataPageHeader);
        return (valueCount, encoding, repetitionLevelEncoding, definitionLevelEncoding, statistics);
    }

    static uint ReadDictionaryHeader(ref CompactProtocolReader reader, CompactProtocolType structType)
    {
        var valueCount = 0U;
        var requiredFields = new RequiredFields();
        reader.BeginStruct(structType);
        while (reader.TryReadFieldHeader(out var fieldId, out var fieldType, out var inlineBool))
        {
            if (fieldId is 1 or 2)
            {
                requiredFields.Add(fieldId);
                if (fieldId == 1)
                    valueCount = reader.ReadI32AsU32(fieldType);
                else
                    _ = reader.ReadI32(fieldType);
            }
            else
                reader.Skip(fieldType, inlineBool);
        }

        requiredFields.Validate(ParquetRequiredFields.DictionaryPageHeader);
        return valueCount;
    }

    static (uint ValueCount, EncodingKind Encoding, uint NullCount, uint RowCount, uint RepetitionLevelsByteLength,
        uint DefinitionLevelsByteLength, bool IsCompressed, EncodedStatistics Statistics)
        ReadDataPageV2Header(ref CompactProtocolReader reader, CompactProtocolType structType)
    {
        var requiredFields = new RequiredFields();
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var nullCount = 0U;
        var rowCount = 0U;
        var repetitionLevelsByteLength = 0U;
        var definitionLevelsByteLength = 0U;
        var isCompressed = true; // spec default
        var statistics = default(EncodedStatistics);

        reader.BeginStruct(structType);

        while (reader.TryReadFieldHeader(out var fieldId, out var fieldType, out var inlineBool))
        {
            requiredFields.Add(fieldId);
            switch (fieldId)
            {
                case 1:
                    valueCount = reader.ReadI32AsU32(fieldType);
                    break;
                case 2:
                    nullCount = reader.ReadI32AsU32(fieldType);
                    break;
                case 3:
                    rowCount = reader.ReadI32AsU32(fieldType);
                    break;
                case 4:
                    encoding = ParquetThriftConversions.ReadEncoding(reader.ReadI32(fieldType));
                    break;
                case 5:
                    definitionLevelsByteLength = reader.ReadI32AsU32(fieldType);
                    break;
                case 6:
                    repetitionLevelsByteLength = reader.ReadI32AsU32(fieldType);
                    break;
                case 7:
                    isCompressed = reader.ReadBool(fieldType, inlineBool);
                    break;
                case 8:
                    statistics = StatisticsThriftReader.Read(ref reader, fieldType);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        requiredFields.Validate(ParquetRequiredFields.DataPageHeaderV2);
        return (valueCount, encoding, nullCount, rowCount, repetitionLevelsByteLength, definitionLevelsByteLength,
            isCompressed, statistics);
    }

}
