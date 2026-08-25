using Plank.Reading.Internal;
using Plank.Schema;

namespace Plank.Reading;

static class PageHeaderReader
{
    internal static PageHeader Read(ReadOnlySpan<byte> buffer, uint maxUncompressedPageSize = uint.MaxValue)
        => Read(buffer, maxUncompressedPageSize, bufferMayBeTruncated: false);

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
                case 4:
                    crc = unchecked((uint)reader.ReadI32());
                    break;
                case 5:
                    (valueCount, encoding, repetitionLevelEncoding, definitionLevelEncoding, statistics)
                        = ReadDataPageHeader(ref reader);
                    break;
                case 7:
                    valueCount = ReadDictionaryHeader(ref reader);
                    break;
                case 8:
                    (valueCount, encoding, nullCount, rowCount, repetitionLevelsByteLength,
                        definitionLevelsByteLength, isCompressed, statistics)
                        = ReadDataPageV2Header(ref reader);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        return new PageHeader(type, uncompressedPageSize, compressedPageSize, valueCount, encoding, reader.Offset,
            repetitionLevelsByteLength, definitionLevelsByteLength, nullCount, isCompressed, repetitionLevelEncoding,
            definitionLevelEncoding, rowCount, statistics, crc);
    }

    static (uint ValueCount, EncodingKind Encoding, EncodingKind RepetitionLevelEncoding,
        EncodingKind DefinitionLevelEncoding, EncodedStatistics Statistics)
        ReadDataPageHeader(ref CompactProtocolReader reader)
    {
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var repetitionLevelEncoding = EncodingKind.Rle;
        var definitionLevelEncoding = EncodingKind.Rle;
        var statistics = default(EncodedStatistics);

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
                case 5:
                    statistics = StatisticsThriftReader.Read(ref reader);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        return (valueCount, encoding, repetitionLevelEncoding, definitionLevelEncoding, statistics);
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

    static (uint ValueCount, EncodingKind Encoding, uint NullCount, uint RowCount, uint RepetitionLevelsByteLength,
        uint DefinitionLevelsByteLength, bool IsCompressed, EncodedStatistics Statistics)
        ReadDataPageV2Header(ref CompactProtocolReader reader)
    {
        var valueCount = 0U;
        var encoding = EncodingKind.Plain;
        var nullCount = 0U;
        var rowCount = 0U;
        var repetitionLevelsByteLength = 0U;
        var definitionLevelsByteLength = 0U;
        var isCompressed = true; // spec default
        var statistics = default(EncodedStatistics);

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
                case 3:
                    rowCount = reader.ReadI32AsU32();
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
                case 8:
                    statistics = StatisticsThriftReader.Read(ref reader);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        return (valueCount, encoding, nullCount, rowCount, repetitionLevelsByteLength, definitionLevelsByteLength,
            isCompressed, statistics);
    }
}
