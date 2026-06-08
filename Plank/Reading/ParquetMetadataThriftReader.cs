using System.Collections.Immutable;
using System.Text;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading;

static class ParquetMetadataThriftReader
{
    internal static InternalParquetFooter Read(ReadOnlySpan<byte> buffer)
    {
        var reader = new CompactProtocolReader(buffer);
        var previousFieldId = 0;
        var version = 0;
        var rowGroupCount = 0U;
        var rowGroupsOffset = 0;
        var rowGroupsEndOffset = 0;
        ParquetSchema? schema = null;

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    version = reader.ReadI32();
                    break;
                case 2:
                    schema = ReadSchema(ref reader);
                    break;
                case 4:
                    (rowGroupCount, rowGroupsOffset, rowGroupsEndOffset) = ReadRowGroupListPosition(ref reader);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new InternalParquetFooter(version,
            schema ?? throw new CorruptParquetException("File metadata is missing schema."), rowGroupCount,
            rowGroupsOffset, rowGroupsEndOffset);
    }

    internal static bool TryReadNextRowGroup(ReadOnlySpan<byte> rowGroups, ulong rowGroupsMetadataOffset,
        int rowGroupsEndOffset, int ordinal, ref int rowGroupOffset, InternalColumnChunkMetadata[] previousColumns,
        ParquetSchema schema, int footerVersion, out InternalRowGroupMetadata rowGroup)
    {
        if ((uint)rowGroupOffset >= (uint)rowGroupsEndOffset)
        {
            rowGroup = default;
            return false;
        }

        var footerRowGroupOffset = rowGroupOffset;
        var reader = new CompactProtocolReader(rowGroups[rowGroupOffset..]);
        var metadataOffset = rowGroupsMetadataOffset + (ulong)rowGroupOffset;
        rowGroup = ReadRowGroup(ref reader, ordinal, metadataOffset, previousColumns, schema);
        rowGroup = new InternalRowGroupMetadata(rowGroup.RowGroupOrdinal, rowGroup.MetadataOffset,
            rowGroup.ColumnChunkOffset, rowGroup.RowCount, rowGroup.Columns, footerRowGroupOffset, footerVersion);
        rowGroupOffset += reader.Offset;
        return true;
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

    static (uint Count, int Offset, int EndOffset) ReadRowGroupListPosition(ref CompactProtocolReader reader)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new CorruptParquetException("Expected row_groups to be encoded as a list of structs.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Row group count {count} exceeds remaining input bytes.");

        var offset = reader.Offset;
        for (var i = 0U; i < count; i++)
            reader.Skip(elementType);
        return (count, offset, reader.Offset);
    }

    static ParquetSchema ReadSchema(ref CompactProtocolReader reader)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new CorruptParquetException("Expected schema to be encoded as a list of structs.");
        if (count == 0)
            throw new CorruptParquetException("Parquet schema must contain a root node.");

        var nodes = ImmutableArray.CreateBuilder<SchemaNode>(checked((int)count));
        for (var i = 0U; i < count; i++)
            nodes.Add(ReadSchemaNode(ref reader));

        var root = nodes[0];
        if (root.Children < 0)
            throw new CorruptParquetException("Parquet schema root must define a non-negative child count.");

        var index = 1;
        var definitions = ImmutableArray.CreateBuilder<ColumnDefinition>(root.Children);
        var immutableNodes = nodes.MoveToImmutable();
        for (var i = 0; i < root.Children; i++)
            definitions.Add(BuildDefinition(immutableNodes, ref index));

        if (index != immutableNodes.Length)
            throw new CorruptParquetException("Parquet schema contains unreferenced nodes.");

        return new ParquetSchema(definitions.MoveToImmutable());
    }

    static SchemaNode ReadSchemaNode(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        ParquetPhysicalType? physicalType = null;
        ParquetRepetition repetition = ParquetRepetition.Required;
        string? name = null;
        var children = -1;
        var typeLength = 0U;
        LogicalType? logicalType = null;
        int? convertedType = null;
        int? scale = null;
        int? precision = null;

        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    physicalType = ReadPhysicalType(reader.ReadI32());
                    break;
                case 2:
                    typeLength = reader.ReadI32AsU32();
                    break;
                case 3:
                    repetition = ReadRepetition(reader.ReadI32());
                    break;
                case 4:
                    name = ReadUtf8PathSegment(reader.ReadBinary());
                    break;
                case 5:
                    children = reader.ReadI32();
                    break;
                case 6:
                    convertedType = reader.ReadI32();
                    break;
                case 7:
                    scale = reader.ReadI32();
                    break;
                case 8:
                    precision = reader.ReadI32();
                    break;
                case 10:
                    logicalType = ReadLogicalType(ref reader);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        if (name is null)
            throw new CorruptParquetException("Schema node is missing a name.");

        logicalType ??= ReadConvertedLogicalType(convertedType, precision, scale);
        return new SchemaNode(name, physicalType, repetition, children, typeLength, logicalType, convertedType);
    }

    static ColumnDefinition BuildDefinition(ImmutableArray<SchemaNode> nodes, ref int index)
    {
        if ((uint)index >= (uint)nodes.Length)
            throw new CorruptParquetException("Parquet schema child count exceeds schema node count.");

        var node = nodes[index++];
        if (node.PhysicalType is { } physicalType)
        {
            var options = new ColumnOptions(node.Repetition, typeLength: node.TypeLength);
            return new ColumnDefinition
            {
                Name = node.Name,
                Kind = NodeKind.Leaf,
                Repetition = node.Repetition,
                PhysicalType = physicalType,
                LogicalType = node.LogicalType,
                Options = options,
                Children = []
            };
        }

        if (node.Children < 0)
            throw new CorruptParquetException($"Group schema node '{node.Name}' is missing num_children.");

        if (IsListNode(node))
            return BuildListDefinition(nodes, ref index, node);
        if (IsMapNode(node))
            return BuildMapDefinition(nodes, ref index, node);

        var children = ImmutableArray.CreateBuilder<ColumnDefinition>(node.Children);
        for (var i = 0; i < node.Children; i++)
            children.Add(BuildDefinition(nodes, ref index));
        return new ColumnDefinition
        {
            Name = node.Name,
            Kind = NodeKind.Group,
            Repetition = node.Repetition,
            Children = children.MoveToImmutable()
        };
    }

    static ColumnDefinition BuildListDefinition(ImmutableArray<SchemaNode> nodes, ref int index, SchemaNode node)
    {
        if (node.Children != 1)
            throw new CorruptParquetException($"LIST schema node '{node.Name}' must contain exactly one repeated list child.");
        if ((uint)index >= (uint)nodes.Length)
            throw new CorruptParquetException($"LIST schema node '{node.Name}' is missing its repeated list child.");

        var repeated = nodes[index++];
        if (repeated.Children != 1)
            throw new CorruptParquetException($"LIST schema node '{node.Name}' repeated child must contain exactly one element.");

        var element = BuildDefinition(nodes, ref index) with { Name = "element" };
        return new ColumnDefinition
        {
            Name = node.Name,
            Kind = NodeKind.List,
            Repetition = node.Repetition,
            Children = [element]
        };
    }

    static ColumnDefinition BuildMapDefinition(ImmutableArray<SchemaNode> nodes, ref int index, SchemaNode node)
    {
        if (node.Children != 1)
            throw new CorruptParquetException($"MAP schema node '{node.Name}' must contain exactly one key_value child.");
        if ((uint)index >= (uint)nodes.Length)
            throw new CorruptParquetException($"MAP schema node '{node.Name}' is missing its key_value child.");

        var keyValue = nodes[index++];
        if (keyValue.Children != 2)
            throw new CorruptParquetException($"MAP schema node '{node.Name}' key_value child must contain key and value.");

        var key = BuildDefinition(nodes, ref index) with { Name = "key" };
        var value = BuildDefinition(nodes, ref index) with { Name = "value" };
        return new ColumnDefinition
        {
            Name = node.Name,
            Kind = NodeKind.Map,
            Repetition = node.Repetition,
            Children = [key, value]
        };
    }

    static InternalRowGroupMetadata ReadRowGroup(ref CompactProtocolReader reader, int rowGroupOrdinal, ulong metadataOffset,
        InternalColumnChunkMetadata[] previousColumns, ParquetSchema? schema)
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
                    columns = ReadColumns(ref reader, previousColumns, schema);
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
        ParquetSchema? schema)
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
        return schema is null ? columns : AlignColumns(columns, schema);
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

    static InternalColumnChunkMetadata[] AlignColumns(InternalColumnChunkMetadata[] fileColumns, ParquetSchema schema)
    {
        var columns = schema.Columns;
        if (columns.Length == fileColumns.Length)
        {
            var matches = true;
            for (var i = 0; i < columns.Length; i++)
            {
                if (columns[i].Name == fileColumns[i].Path && columns[i].PhysicalType == fileColumns[i].PhysicalType)
                    continue;

                matches = false;
                break;
            }

            if (matches)
                return fileColumns;
        }

        var projected = columns.Length == 0 ? [] : new InternalColumnChunkMetadata[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            var found = false;
            for (var j = 0; j < fileColumns.Length; j++)
            {
                if (columns[i].Name != fileColumns[j].Path)
                    continue;
                if (columns[i].PhysicalType != fileColumns[j].PhysicalType)
                    throw new CorruptParquetException(
                        $"File schema column '{columns[i].Name}' has physical type {columns[i].PhysicalType}, but row group metadata has {fileColumns[j].PhysicalType}.");

                projected[i] = fileColumns[j];
                found = true;
                break;
            }

            if (!found)
                throw new CorruptParquetException($"Row group metadata is missing schema column '{columns[i].Name}'.");
        }

        return projected;
    }

    static LogicalType? ReadLogicalType(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        LogicalType? logicalType = null;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    reader.Skip(type, inlineBool);
                    logicalType = new LogicalType.String();
                    break;
                case 5:
                    logicalType = ReadDecimalLogicalType(ref reader);
                    break;
                case 6:
                    reader.Skip(type, inlineBool);
                    logicalType = new LogicalType.Date();
                    break;
                case 7:
                    logicalType = ReadTimeLogicalType(ref reader);
                    break;
                case 8:
                    logicalType = ReadTimestampLogicalType(ref reader);
                    break;
                case 10:
                    logicalType = ReadIntegerLogicalType(ref reader);
                    break;
                case 12:
                    reader.Skip(type, inlineBool);
                    logicalType = new LogicalType.Json();
                    break;
                case 14:
                    reader.Skip(type, inlineBool);
                    logicalType = new LogicalType.Uuid();
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return logicalType;
    }

    static LogicalType.Decimal ReadDecimalLogicalType(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        var scale = 0;
        var precision = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    scale = reader.ReadI32();
                    break;
                case 2:
                    precision = reader.ReadI32();
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new LogicalType.Decimal(precision, scale);
    }

    static LogicalType.Int ReadIntegerLogicalType(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        var bitWidth = (byte)0;
        var isSigned = true;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    bitWidth = reader.ReadByte();
                    break;
                case 2:
                    isSigned = reader.ReadBool(inlineBool);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new LogicalType.Int(bitWidth, isSigned);
    }

    static LogicalType.Time ReadTimeLogicalType(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        var adjusted = false;
        var unit = TimeUnit.Millis;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    adjusted = reader.ReadBool(inlineBool);
                    break;
                case 2:
                    unit = ReadTimeUnit(ref reader);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new LogicalType.Time(unit, adjusted);
    }

    static LogicalType.Timestamp ReadTimestampLogicalType(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        var adjusted = false;
        var unit = TimeUnit.Millis;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    adjusted = reader.ReadBool(inlineBool);
                    break;
                case 2:
                    unit = ReadTimeUnit(ref reader);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        return new LogicalType.Timestamp(unit, adjusted);
    }

    static TimeUnit ReadTimeUnit(ref CompactProtocolReader reader)
    {
        var previousFieldId = 0;
        TimeUnit? unit = null;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            reader.Skip(type, inlineBool);
            unit = fieldId switch
            {
                1 => TimeUnit.Millis,
                2 => TimeUnit.Micros,
                3 => TimeUnit.Nanos,
                _ => throw new NotSupportedException($"Time unit field '{fieldId}' is not supported.")
            };
        }

        return unit ?? throw new CorruptParquetException("Logical time unit is missing.");
    }

    static LogicalType? ReadConvertedLogicalType(int? convertedType, int? precision, int? scale)
        => convertedType switch
        {
            0 => new LogicalType.String(),
            5 => new LogicalType.Decimal(precision.GetValueOrDefault(), scale.GetValueOrDefault()),
            6 => new LogicalType.Date(),
            7 => new LogicalType.Time(TimeUnit.Millis, false),
            8 => new LogicalType.Time(TimeUnit.Micros, false),
            9 => new LogicalType.Timestamp(TimeUnit.Millis, false),
            10 => new LogicalType.Timestamp(TimeUnit.Micros, false),
            11 => new LogicalType.Int(8, false),
            12 => new LogicalType.Int(16, false),
            13 => new LogicalType.Int(32, false),
            14 => new LogicalType.Int(64, false),
            19 => new LogicalType.Json(),
            _ => null
        };

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

    static ParquetRepetition ReadRepetition(int repetition)
        => repetition switch
        {
            0 => ParquetRepetition.Required,
            1 => ParquetRepetition.Optional,
            2 => ParquetRepetition.Repeated,
            _ => throw new NotSupportedException($"Repetition type '{repetition}' is not supported.")
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

    static bool IsListNode(SchemaNode node)
        => node.ConvertedType == 3 || node.LogicalType is null && false;

    static bool IsMapNode(SchemaNode node)
        => node.ConvertedType == 1;

    readonly record struct SchemaNode(string Name, ParquetPhysicalType? PhysicalType, ParquetRepetition Repetition,
        int Children, uint TypeLength, LogicalType? LogicalType, int? ConvertedType);
}
