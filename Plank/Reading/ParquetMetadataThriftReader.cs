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
                    columns = ReadColumns(ref reader, previousColumns, requestedSchema);
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
        var columns = (uint)previous.Length == count ? previous : new InternalColumnChunkMetadata[(int)count];
        for (var i = 0U; i < count; i++)
        {
            var previousEncodings = columns[i].Encodings ?? [];
            columns[i] = ReadColumn(ref reader, previousEncodings, columns[i].Path);
        }
        return requestedSchema is null ? columns : AlignRequestedColumns(columns, requestedSchema);
    }

    static InternalColumnChunkMetadata ReadColumn(ref CompactProtocolReader reader, EncodingKind[] previousEncodings,
        string? previousPath)
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
        string? path = null;
        ParquetPhysicalType? physicalType = null;

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 2:
                    dataPageOffset = reader.ReadI64AsU64();
                    break;
                case 3:
                    ReadColumnMetadata(ref reader, ref dataPageOffset, ref dictionaryPageOffset, ref totalCompressedSize,
                        ref totalUncompressedSize, ref compression, ref encodings, previousEncodings, previousPath,
                        ref path, ref physicalType);
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

        if (path is null)
            throw new CorruptParquetException("Column chunk metadata is missing path_in_schema.");
        if (physicalType is null)
            throw new CorruptParquetException($"Column chunk '{path}' is missing a physical type.");

        return new InternalColumnChunkMetadata(dataPageOffset, dictionaryPageOffset, totalCompressedSize,
            totalUncompressedSize, compression, encodings, path, physicalType.Value, columnIndexOffset, columnIndexLength,
            offsetIndexOffset, offsetIndexLength);
    }

    static void ReadColumnMetadata(ref CompactProtocolReader reader, ref ulong dataPageOffset,
        ref ulong dictionaryPageOffset, ref ulong totalCompressedSize, ref ulong totalUncompressedSize,
        ref CompressionKind compression, ref EncodingKind[] encodings, EncodingKind[] previousEncodings,
        string? previousPath, ref string? path, ref ParquetPhysicalType? physicalType)
    {
        var previousFieldId = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    physicalType = ReadPhysicalType(reader.ReadI32());
                    break;
                case 2:
                    encodings = ReadEncodings(ref reader, previousEncodings);
                    break;
                case 3:
                    path = ReadPath(ref reader, previousPath);
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
                case 9:
                    dataPageOffset = reader.ReadI64AsU64();
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

    static InternalColumnChunkMetadata[] AlignRequestedColumns(InternalColumnChunkMetadata[] fileColumns,
        ParquetSchema requestedSchema)
    {
        var requestedColumns = requestedSchema.Columns;
        if (requestedColumns.Length == fileColumns.Length)
        {
            var matchesInOrder = true;
            for (var i = 0; i < requestedColumns.Length; i++)
            {
                if (requestedColumns[i].Name == fileColumns[i].Path &&
                    requestedColumns[i].PhysicalType == fileColumns[i].PhysicalType)
                    continue;

                matchesInOrder = false;
                break;
            }

            if (matchesInOrder)
                return fileColumns;
        }

        var fileOrdinalByPath = new Dictionary<string, int>(fileColumns.Length, StringComparer.Ordinal);
        for (var i = 0; i < fileColumns.Length; i++)
        {
            var path = fileColumns[i].Path;
            if (!fileOrdinalByPath.TryAdd(path, i))
                throw new CorruptParquetException($"File schema contains duplicate column path '{path}'.");
        }

        var projected = requestedColumns.Length == 0 ? [] : new InternalColumnChunkMetadata[requestedColumns.Length];
        for (var i = 0; i < requestedColumns.Length; i++)
        {
            var requestedColumn = requestedColumns[i];
            if (!fileOrdinalByPath.TryGetValue(requestedColumn.Name, out var fileOrdinal))
            {
                if (requestedColumn.Options.AllowMissing)
                {
                    projected[i] = InternalColumnChunkMetadata.Missing(requestedColumn);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Requested schema column '{requestedColumn.Name}' is not present in the file schema.");
            }

            var fileColumn = fileColumns[fileOrdinal];
            if (requestedColumn.PhysicalType != fileColumn.PhysicalType)
                throw new InvalidOperationException(
                    $"Requested schema column '{requestedColumn.Name}' has physical type {requestedColumn.PhysicalType}, but file schema has {fileColumn.PhysicalType}.");

            projected[i] = fileColumn;
        }

        return projected;
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

    static string ReadPath(ref CompactProtocolReader reader, string? previousPath)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Binary)
            throw new CorruptParquetException("Expected path_in_schema to be encoded as binary list elements.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Path segment count {count} exceeds remaining input bytes.");

        StringBuilder? builder = null;
        var previous = previousPath.AsSpan();
        var previousOffset = 0;
        var previousMatches = previousPath is not null;
        for (var i = 0U; i < count; i++)
        {
            var segment = reader.ReadBinary();
            if (previousMatches)
            {
                if (i > 0)
                {
                    if ((uint)previousOffset >= (uint)previous.Length || previous[previousOffset] != '.')
                        previousMatches = false;
                    else
                        previousOffset++;
                }

                var segmentStart = previousOffset;
                var expectedSegment = ReadOnlySpan<char>.Empty;
                if (previousMatches)
                {
                    var nextSeparator = previous[previousOffset..].IndexOf('.');
                    expectedSegment = nextSeparator < 0 ? previous[previousOffset..] : previous.Slice(previousOffset, nextSeparator);
                }

                if (previousMatches && Utf8Equals(segment, expectedSegment))
                {
                    previousOffset += expectedSegment.Length;
                    continue;
                }

                previousMatches = false;
                builder = new StringBuilder();
                if (i > 0)
                {
                    builder.Append(previous[..(segmentStart - 1)]);
                    builder.Append('.');
                }
            }
            else if (builder is not null && i > 0)
            {
                builder.Append('.');
            }

            builder ??= new StringBuilder();
            builder.Append(ReadUtf8PathSegment(segment));
        }

        if (previousMatches && previousOffset == previous.Length)
            return previousPath!;

        return builder?.ToString() ?? string.Empty;
    }

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

    static string ReadUtf8PathSegment(ReadOnlySpan<byte> segment)
    {
        if (segment.Length > 1024)
            throw new NotSupportedException("Column path segments longer than 1024 UTF-8 bytes are not supported.");

        return Encoding.UTF8.GetString(segment);
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
