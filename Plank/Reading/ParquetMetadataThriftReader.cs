using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading;

static class ParquetMetadataThriftReader
{
    internal static InternalParquetFooter Read(ReadOnlySpan<byte> buffer, long footerOffset)
    {
        var reader = new CompactProtocolReader(buffer);
        var previousFieldId = 0;
        var version = 0;
        InternalRowGroupMetadata[] rowGroups = [];

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    version = reader.ReadI32();
                    break;
                case 4:
                    rowGroups = ReadRowGroups(ref reader, footerOffset);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new InternalParquetFooter(version, rowGroups);
    }

    internal static CompressionKind ReadCompression(int compression)
        => compression switch
        {
            0 => CompressionKind.None,
            1 => CompressionKind.Snappy,
            2 => CompressionKind.Gzip,
            4 => CompressionKind.Brotli,
            5 => CompressionKind.Lz4,
            6 => CompressionKind.Zstd,
            _ => throw new NotSupportedException($"Compression codec '{compression}' is not supported.")
        };

    internal static EncodingKind ReadEncoding(int encoding)
        => encoding switch
        {
            0 => EncodingKind.Plain,
            2 => EncodingKind.PlainDictionary,
            3 => EncodingKind.Rle,
            4 => EncodingKind.BitPacked,
            5 => EncodingKind.DeltaBinaryPacked,
            6 => EncodingKind.DeltaLengthByteArray,
            7 => EncodingKind.DeltaByteArray,
            8 => EncodingKind.RleDictionary,
            9 => EncodingKind.ByteStreamSplit,
            _ => throw new NotSupportedException($"Encoding '{encoding}' is not supported.")
        };

    static InternalRowGroupMetadata[] ReadRowGroups(ref CompactProtocolReader reader, long footerOffset)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new InvalidDataException("Expected row_groups to be encoded as a list of structs.");

        var rowGroups = new InternalRowGroupMetadata[count];
        for (var i = 0; i < count; i++)
            rowGroups[i] = ReadRowGroup(ref reader, i, footerOffset + reader.Offset);
        return rowGroups;
    }

    static InternalRowGroupMetadata ReadRowGroup(ref CompactProtocolReader reader, int rowGroupOrdinal, long metadataOffset)
    {
        var previousFieldId = 0;
        var columnChunkOffset = 0L;
        InternalColumnChunkMetadata[] columns = [];

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    columns = ReadColumns(ref reader);
                    break;
                case 5:
                    columnChunkOffset = reader.ReadI64();
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        if (columnChunkOffset == 0 && columns.Length != 0)
            columnChunkOffset = columns[0].ChunkOffset;

        return new InternalRowGroupMetadata(rowGroupOrdinal, metadataOffset, columnChunkOffset, columns);
    }

    static InternalColumnChunkMetadata[] ReadColumns(ref CompactProtocolReader reader)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new InvalidDataException("Expected row group columns to be encoded as a list of structs.");

        var columns = new InternalColumnChunkMetadata[count];
        for (var i = 0; i < count; i++)
            columns[i] = ReadColumn(ref reader);
        return columns;
    }

    static InternalColumnChunkMetadata ReadColumn(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        var dataPageOffset = 0L;
        var dictionaryPageOffset = 0L;
        var totalCompressedSize = 0L;
        var compression = CompressionKind.None;
        EncodingKind[] encodings = [];

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 2:
                    dataPageOffset = reader.ReadI64();
                    break;
                case 3:
                    ReadColumnMetadata(ref reader, ref dictionaryPageOffset, ref totalCompressedSize, ref compression,
                        ref encodings);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new InternalColumnChunkMetadata(dataPageOffset, dictionaryPageOffset, totalCompressedSize, compression,
            encodings);
    }

    static void ReadColumnMetadata(ref CompactProtocolReader reader, ref long dictionaryPageOffset,
        ref long totalCompressedSize, ref CompressionKind compression, ref EncodingKind[] encodings)
    {
        var previousFieldId = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 2:
                    encodings = ReadEncodings(ref reader);
                    break;
                case 4:
                    compression = ReadCompression(reader.ReadI32());
                    break;
                case 7:
                    totalCompressedSize = reader.ReadI64();
                    break;
                case 11:
                    dictionaryPageOffset = reader.ReadI64();
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }
    }

    static EncodingKind[] ReadEncodings(ref CompactProtocolReader reader)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.I32)
            throw new InvalidDataException("Expected encoding ids to be encoded as I32 list elements.");

        var encodings = new EncodingKind[count];
        for (var i = 0; i < count; i++)
            encodings[i] = ReadEncoding(reader.ReadI32());
        return encodings;
    }
}
