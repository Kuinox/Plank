using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading;

static class ParquetMetadataThriftReader
{
    internal static InternalParquetFooter Read(ReadOnlySpan<byte> buffer, ulong footerOffset)
        => Read(buffer, footerOffset, InternalParquetFooter.Empty);

    internal static InternalParquetFooter Read(ReadOnlySpan<byte> buffer, ulong footerOffset, InternalParquetFooter previous)
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
                    rowGroups = ReadRowGroups(ref reader, footerOffset, previous.RowGroups);
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

    static InternalRowGroupMetadata[] ReadRowGroups(ref CompactProtocolReader reader, ulong footerOffset,
        InternalRowGroupMetadata[] previous)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new CorruptParquetException("Expected row_groups to be encoded as a list of structs.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Row group count {count} exceeds remaining input bytes.");

        var rowGroups = (uint)previous.Length == count ? previous : new InternalRowGroupMetadata[(int)count];
        for (var i = 0U; i < count; i++)
        {
            var previousColumns = rowGroups[i].Columns ?? [];
            rowGroups[i] = ReadRowGroup(ref reader, checked((int)i), footerOffset + (ulong)reader.Offset, previousColumns);
        }
        return rowGroups;
    }

    static InternalRowGroupMetadata ReadRowGroup(ref CompactProtocolReader reader, int rowGroupOrdinal, ulong metadataOffset,
        InternalColumnChunkMetadata[] previousColumns)
    {
        var previousFieldId = 0;
        var columnChunkOffset = 0UL;
        var rowCount = 0UL;
        InternalColumnChunkMetadata[] columns = [];

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    columns = ReadColumns(ref reader, previousColumns);
                    break;
                case 3:
                    rowCount = reader.ReadI64AsU64();
                    break;
                case 5:
                    columnChunkOffset = reader.ReadI64AsU64();
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        if (columnChunkOffset == 0 && columns.Length != 0)
            columnChunkOffset = columns[0].ChunkOffset;

        return new InternalRowGroupMetadata(rowGroupOrdinal, metadataOffset, columnChunkOffset, rowCount, columns);
    }

    static InternalColumnChunkMetadata[] ReadColumns(ref CompactProtocolReader reader, InternalColumnChunkMetadata[] previous)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new CorruptParquetException("Expected row group columns to be encoded as a list of structs.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Column count {count} exceeds remaining input bytes.");

        var columns = (uint)previous.Length == count ? previous : new InternalColumnChunkMetadata[(int)count];
        for (var i = 0U; i < count; i++)
        {
            var previousEncodings = columns[i].Encodings ?? [];
            columns[i] = ReadColumn(ref reader, previousEncodings);
        }
        return columns;
    }

    static InternalColumnChunkMetadata ReadColumn(ref CompactProtocolReader reader, EncodingKind[] previousEncodings)
    {
        var previousFieldId = 0;
        var dataPageOffset = 0UL;
        var dictionaryPageOffset = 0UL;
        var totalCompressedSize = 0UL;
        var columnIndexOffset = 0UL;
        var columnIndexLength = 0U;
        var offsetIndexOffset = 0UL;
        var offsetIndexLength = 0U;
        var compression = CompressionKind.None;
        EncodingKind[] encodings = [];

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 2:
                    dataPageOffset = reader.ReadI64AsU64();
                    break;
                case 3:
                    ReadColumnMetadata(ref reader, ref dictionaryPageOffset, ref totalCompressedSize, ref compression,
                        ref encodings, previousEncodings);
                    break;
                case 4:
                    offsetIndexOffset = reader.ReadI64AsU64();
                    break;
                case 5:
                    offsetIndexLength = reader.ReadI32AsU32();
                    break;
                case 6:
                    columnIndexOffset = reader.ReadI64AsU64();
                    break;
                case 7:
                    columnIndexLength = reader.ReadI32AsU32();
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new InternalColumnChunkMetadata(dataPageOffset, dictionaryPageOffset, totalCompressedSize, compression,
            encodings, columnIndexOffset, columnIndexLength, offsetIndexOffset, offsetIndexLength);
    }

    static void ReadColumnMetadata(ref CompactProtocolReader reader, ref ulong dictionaryPageOffset,
        ref ulong totalCompressedSize, ref CompressionKind compression, ref EncodingKind[] encodings,
        EncodingKind[] previousEncodings)
    {
        var previousFieldId = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 2:
                    encodings = ReadEncodings(ref reader, previousEncodings);
                    break;
                case 4:
                    compression = ReadCompression(reader.ReadI32());
                    break;
                case 7:
                    totalCompressedSize = reader.ReadI64AsU64();
                    break;
                case 11:
                    dictionaryPageOffset = reader.ReadI64AsU64();
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }
    }

    static EncodingKind[] ReadEncodings(ref CompactProtocolReader reader, EncodingKind[] previous)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.I32)
            throw new CorruptParquetException("Expected encoding ids to be encoded as I32 list elements.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Encoding count {count} exceeds remaining input bytes.");

        var encodings = (uint)previous.Length == count ? previous : new EncodingKind[(int)count];
        for (var i = 0U; i < count; i++)
            encodings[i] = ReadEncoding(reader.ReadI32());
        return encodings;
    }
}
