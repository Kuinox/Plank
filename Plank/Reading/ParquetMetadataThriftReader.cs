using System.Text;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading;

static class ParquetMetadataThriftReader
{
    internal static InternalParquetFooter Read(ReadOnlySpan<byte> buffer, ulong footerOffset)
        => Read(buffer, footerOffset, InternalParquetFooter.Empty, null);

    internal static InternalParquetFooter Read(ReadOnlySpan<byte> buffer, ulong footerOffset, ParquetSchema requestedSchema)
        => Read(buffer, footerOffset, InternalParquetFooter.Empty, requestedSchema);

    internal static InternalParquetFooter Read(ReadOnlySpan<byte> buffer, ulong footerOffset, InternalParquetFooter previous)
        => Read(buffer, footerOffset, previous, null);

    internal static InternalParquetFooter Read(ReadOnlySpan<byte> buffer, ulong footerOffset, InternalParquetFooter previous,
        ParquetSchema? requestedSchema)
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
                    rowGroups = ReadRowGroups(ref reader, footerOffset, previous.RowGroups, requestedSchema);
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
        InternalRowGroupMetadata[] previous, ParquetSchema? requestedSchema)
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
            rowGroups[i] = ReadRowGroup(ref reader, checked((int)i), footerOffset + (ulong)reader.Offset, previousColumns,
                requestedSchema);
        }
        return rowGroups;
    }

    static InternalRowGroupMetadata ReadRowGroup(ref CompactProtocolReader reader, int rowGroupOrdinal, ulong metadataOffset,
        InternalColumnChunkMetadata[] previousColumns, ParquetSchema? requestedSchema)
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
                    columns = ReadColumns(ref reader, previousColumns, rowGroupOrdinal == 0 ? requestedSchema : null);
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

    static InternalColumnChunkMetadata[] ReadColumns(ref CompactProtocolReader reader, InternalColumnChunkMetadata[] previous,
        ParquetSchema? requestedSchema)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new CorruptParquetException("Expected row group columns to be encoded as a list of structs.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Column count {count} exceeds remaining input bytes.");
        if (requestedSchema is not null && requestedSchema.Columns.Length != count)
            throw new InvalidOperationException(
                $"Requested schema has {requestedSchema.Columns.Length} column(s), but file schema has {count}.");

        var columns = (uint)previous.Length == count ? previous : new InternalColumnChunkMetadata[(int)count];
        for (var i = 0U; i < count; i++)
        {
            var previousEncodings = columns[i].Encodings ?? [];
            var requestedColumn = requestedSchema is null ? null : requestedSchema.Columns[checked((int)i)];
            columns[i] = ReadColumn(ref reader, previousEncodings, requestedColumn);
        }
        return columns;
    }

    static InternalColumnChunkMetadata ReadColumn(ref CompactProtocolReader reader, EncodingKind[] previousEncodings,
        Column? requestedColumn)
    {
        var previousFieldId = 0;
        var dataPageOffset = 0UL;
        var dictionaryPageOffset = 0UL;
        var totalCompressedSize = 0UL;
        var totalUncompressedSize = 0UL;
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
                    ReadColumnMetadata(ref reader, ref dictionaryPageOffset, ref totalCompressedSize,
                        ref totalUncompressedSize, ref compression, ref encodings, previousEncodings, requestedColumn);
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

        return new InternalColumnChunkMetadata(dataPageOffset, dictionaryPageOffset, totalCompressedSize,
            totalUncompressedSize, compression, encodings, columnIndexOffset, columnIndexLength,
            offsetIndexOffset, offsetIndexLength);
    }

    static void ReadColumnMetadata(ref CompactProtocolReader reader, ref ulong dictionaryPageOffset,
        ref ulong totalCompressedSize, ref ulong totalUncompressedSize, ref CompressionKind compression,
        ref EncodingKind[] encodings, EncodingKind[] previousEncodings, Column? requestedColumn)
    {
        var previousFieldId = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    var physicalType = ReadPhysicalType(reader.ReadI32());
                    if (requestedColumn is not null && requestedColumn.PhysicalType != physicalType)
                        throw new InvalidOperationException(
                            $"Requested schema column '{requestedColumn.Name}' has physical type {requestedColumn.PhysicalType}, but file schema has {physicalType}.");
                    break;
                case 2:
                    encodings = ReadEncodings(ref reader, previousEncodings);
                    break;
                case 3:
                    ValidateOrSkipPath(ref reader, requestedColumn);
                    break;
                case 4:
                    compression = ReadCompression(reader.ReadI32());
                    break;
                case 6:
                    totalUncompressedSize = reader.ReadI64AsU64();
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

    static ParquetPhysicalType ReadPhysicalType(int type)
        => type switch
        {
            0 => ParquetPhysicalType.Boolean,
            1 => ParquetPhysicalType.Int32,
            2 => ParquetPhysicalType.Int64,
            3 => ParquetPhysicalType.Int96,
            4 => ParquetPhysicalType.Float,
            5 => ParquetPhysicalType.Double,
            6 => ParquetPhysicalType.ByteArray,
            7 => ParquetPhysicalType.FixedLenByteArray,
            _ => throw new NotSupportedException($"Physical type '{type}' is not supported.")
        };

    static void ValidateOrSkipPath(ref CompactProtocolReader reader, Column? requestedColumn)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Binary)
            throw new CorruptParquetException("Expected path_in_schema to be encoded as binary list elements.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Path segment count {count} exceeds remaining input bytes.");

        if (requestedColumn is null)
        {
            for (var i = 0U; i < count; i++)
                reader.Skip(CompactProtocolType.Binary);
            return;
        }

        var name = requestedColumn.Name.AsSpan();
        var nameOffset = 0;
        for (var i = 0U; i < count; i++)
        {
            if (i > 0)
            {
                if ((uint)nameOffset >= (uint)name.Length || name[nameOffset] != '.')
                    throw CreatePathMismatch(requestedColumn);
                nameOffset++;
            }

            var segment = reader.ReadBinary();
            var nextSeparator = name[nameOffset..].IndexOf('.');
            var expectedSegment = nextSeparator < 0 ? name[nameOffset..] : name.Slice(nameOffset, nextSeparator);
            if (!Utf8Equals(segment, expectedSegment))
                throw CreatePathMismatch(requestedColumn);

            nameOffset += expectedSegment.Length;
        }

        if (nameOffset != name.Length)
            throw CreatePathMismatch(requestedColumn);
    }

    static InvalidOperationException CreatePathMismatch(Column requestedColumn)
        => new($"Requested schema column '{requestedColumn.Name}' does not match file column path.");

    static bool Utf8Equals(ReadOnlySpan<byte> actual, ReadOnlySpan<char> expected)
    {
        var byteCount = Encoding.UTF8.GetByteCount(expected);
        if (actual.Length != byteCount)
            return false;
        if (byteCount > 1024)
            throw new NotSupportedException("Column path segments longer than 1024 UTF-8 bytes are not supported.");

        Span<byte> expectedBytes = stackalloc byte[byteCount];
        Encoding.UTF8.GetBytes(expected, expectedBytes);
        return actual.SequenceEqual(expectedBytes);
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
