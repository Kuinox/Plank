using Plank.Schema;

namespace Plank.Reading;

static class PageHeaderReader
{
    internal static PageHeader Read(ReadOnlySpan<byte> buffer)
    {
        var reader = new CompactProtocolReader(buffer);
        var previousFieldId = 0;
        var type = PageHeaderType.DataPage;
        var uncompressedPageSize = 0;
        var compressedPageSize = 0;
        var valueCount = 0;
        var encoding = EncodingKind.Plain;

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var fieldType, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    type = (PageHeaderType)reader.ReadI32();
                    break;
                case 2:
                    uncompressedPageSize = reader.ReadI32();
                    break;
                case 3:
                    compressedPageSize = reader.ReadI32();
                    break;
                case 7:
                    valueCount = ReadDictionaryHeader(ref reader);
                    break;
                case 8:
                    (valueCount, encoding) = ReadDataPageV2Header(ref reader);
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        return new PageHeader(type, uncompressedPageSize, compressedPageSize, valueCount, encoding, reader.Offset);
    }

    static int ReadDictionaryHeader(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        var valueCount = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var fieldType, out var inlineBool))
        {
            if (fieldId == 1)
                valueCount = reader.ReadI32();
            else
                reader.Skip(fieldType, inlineBool);
        }

        return valueCount;
    }

    static (int ValueCount, EncodingKind Encoding) ReadDataPageV2Header(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        var valueCount = 0;
        var encoding = EncodingKind.Plain;

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var fieldType, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    valueCount = reader.ReadI32();
                    break;
                case 4:
                    encoding = ParquetMetadataThriftReader.ReadEncoding(reader.ReadI32());
                    break;
                default:
                    reader.Skip(fieldType, inlineBool);
                    break;
            }
        }

        return (valueCount, encoding);
    }
}
