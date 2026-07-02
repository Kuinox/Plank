using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading.Physical.Internal;

static class PhysicalMetadataThriftReader
{
    internal static void Read(PhysicalMetadataStore store)
    {
        var reader = new CompactProtocolReader(store.FooterBytes.AsSpan(0, store.FooterByteCount));
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    store.FileVersion = reader.ReadI32();
                    break;
                case 2:
                    ReadSchema(ref reader, store);
                    break;
                case 4:
                    ReadRowGroups(ref reader, store);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }
    }

    static void ReadSchema(ref CompactProtocolReader reader, PhysicalMetadataStore store)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new CorruptParquetException("Expected schema to be encoded as a list of structs.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Schema node count {count} exceeds remaining input bytes.");

        var nodeCount = checked((int)count);
        store.EnsureSchemaNodes(nodeCount);
        store.EnsureColumns(nodeCount);
        var depth = 0;
        for (var ordinal = 0; ordinal < nodeCount; ordinal++)
        {
            while (depth > 0 && store.RemainingChildren[depth - 1] == 0)
                depth--;

            var parentOrdinal = depth == 0 ? -1 : store.ParentStack[depth - 1];
            if (depth > 0)
                store.RemainingChildren[depth - 1]--;

            var node = ReadSchemaNode(ref reader, store, parentOrdinal);
            store.SchemaNodes[ordinal] = node;
            if (node.PhysicalType.HasValue)
                store.Columns[store.ColumnCount++] = new PhysicalColumnSchema(ordinal, depth);

            if (node.ChildCount <= 0)
                continue;

            store.ParentStack[depth] = ordinal;
            store.RemainingChildren[depth] = node.ChildCount;
            depth++;
        }

        while (depth > 0 && store.RemainingChildren[depth - 1] == 0)
            depth--;
        if (depth != 0)
            throw new CorruptParquetException("Schema node list ended before all declared child nodes were read.");

        store.SchemaNodeCount = nodeCount;
        
    }

    static PhysicalSchemaNode ReadSchemaNode(ref CompactProtocolReader reader, PhysicalMetadataStore store,
        int parentOrdinal)
    {
        ParquetPhysicalType? physicalType = null;
        var typeLength = 0U;
        var repetition = ParquetRepetition.Unspecified;
        var nameOffset = 0;
        var nameLength = 0;
        var childCount = 0;
        var convertedType = -1;
        var logicalType = default(LogicalTypeInfo);
        var hasLogicalType = false;
        var annotation = NodeKind.Group;

        reader.BeginStruct();

        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))

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
                {
                    var name = reader.ReadBinary();
                    nameOffset = reader.Offset - name.Length;
                    nameLength = name.Length;
                    break;
                }
                case 5:
                    childCount = checked((int)reader.ReadI32AsU32());
                    break;
                case 6:
                    convertedType = reader.ReadI32();
                    break;
                case 7:
                    logicalType = new LogicalTypeInfo(LogicalTypeKind.Decimal, Scale: reader.ReadI32());
                    break;
                case 8:
                    logicalType = new LogicalTypeInfo(LogicalTypeKind.Decimal, Precision: reader.ReadI32(),
                        Scale: logicalType.Scale);
                    break;
                case 10:
                    (logicalType, annotation) = ReadLogicalType(ref reader);
                    hasLogicalType = true;
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }

        if (nameLength == 0 && parentOrdinal >= 0)
            throw new CorruptParquetException("Schema node is missing its name.");
        if (!hasLogicalType)
            (logicalType, annotation) = ReadConvertedType(convertedType, logicalType);

        var kind = physicalType.HasValue
            ? NodeKind.Leaf
            : annotation is NodeKind.List or NodeKind.Map ? annotation : NodeKind.Group;
        
        return new PhysicalSchemaNode(parentOrdinal, kind, repetition, physicalType, typeLength, logicalType,
            nameOffset, nameLength, childCount);
    }

    static (LogicalTypeInfo LogicalType, NodeKind Annotation) ReadLogicalType(ref CompactProtocolReader reader)
    {
        var logicalType = default(LogicalTypeInfo);
        var annotation = NodeKind.Group;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    reader.Skip(type, inlineBool);
                    logicalType = new LogicalTypeInfo(LogicalTypeKind.String);
                    break;
                case 3:
                    reader.Skip(type, inlineBool);
                    annotation = NodeKind.List;
                    break;
                case 4:
                    reader.Skip(type, inlineBool);
                    annotation = NodeKind.Map;
                    break;
                case 5:
                    logicalType = ReadDecimalLogicalType(ref reader);
                    break;
                case 6:
                    reader.Skip(type, inlineBool);
                    logicalType = new LogicalTypeInfo(LogicalTypeKind.Date);
                    break;
                case 7:
                    logicalType = ReadTemporalLogicalType(ref reader, LogicalTypeKind.Time);
                    break;
                case 8:
                    logicalType = ReadTemporalLogicalType(ref reader, LogicalTypeKind.Timestamp);
                    break;
                case 10:
                    logicalType = ReadIntegerLogicalType(ref reader);
                    break;
                case 12:
                    reader.Skip(type, inlineBool);
                    logicalType = new LogicalTypeInfo(LogicalTypeKind.Json);
                    break;
                case 14:
                    reader.Skip(type, inlineBool);
                    logicalType = new LogicalTypeInfo(LogicalTypeKind.Uuid);
                    break;
                default:
                    reader.Skip(type, inlineBool);
                    break;
            }
        }
        
        return (logicalType, annotation);
    }

    static LogicalTypeInfo ReadDecimalLogicalType(ref CompactProtocolReader reader)
    {
        var scale = 0;
        var precision = 0;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
            if (fieldId == 1)
                scale = reader.ReadI32();
            else if (fieldId == 2)
                precision = reader.ReadI32();
            else
                reader.Skip(type, inlineBool);

        return new LogicalTypeInfo(LogicalTypeKind.Decimal, precision, scale);
    }

    static LogicalTypeInfo ReadIntegerLogicalType(ref CompactProtocolReader reader)
    {
        byte bitWidth = 0;
        var isSigned = false;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
            if (fieldId == 1)
                bitWidth = reader.ReadByte();
            else if (fieldId == 2)
                isSigned = reader.ReadBool(inlineBool);
            else
                reader.Skip(type, inlineBool);

        return new LogicalTypeInfo(LogicalTypeKind.Integer, BitWidth: bitWidth, IsSigned: isSigned);
    }

    static LogicalTypeInfo ReadTemporalLogicalType(ref CompactProtocolReader reader, LogicalTypeKind kind)
    {
        var isAdjustedToUtc = false;
        var unit = TimeUnit.Millis;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
            if (fieldId == 1)
                isAdjustedToUtc = reader.ReadBool(inlineBool);
            else if (fieldId == 2)
                unit = ReadTimeUnit(ref reader);
            else
                reader.Skip(type, inlineBool);

        return new LogicalTypeInfo(kind, Unit: unit, IsAdjustedToUtc: isAdjustedToUtc);
    }

    static TimeUnit ReadTimeUnit(ref CompactProtocolReader reader)
    {
        var unit = TimeUnit.Millis;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            unit = fieldId switch
            {
                1 => TimeUnit.Millis,
                2 => TimeUnit.Micros,
                3 => TimeUnit.Nanos,
                _ => unit
            };
            reader.Skip(type, inlineBool);
        }

        return unit;
    }

    static (LogicalTypeInfo LogicalType, NodeKind Annotation) ReadConvertedType(int convertedType,
        LogicalTypeInfo decimalType)
        => convertedType switch
        {
            0 => (new LogicalTypeInfo(LogicalTypeKind.String), NodeKind.Group),
            1 or 2 => (default, NodeKind.Map),
            3 => (default, NodeKind.List),
            5 => (new LogicalTypeInfo(LogicalTypeKind.Decimal, decimalType.Precision, decimalType.Scale), NodeKind.Group),
            6 => (new LogicalTypeInfo(LogicalTypeKind.Date), NodeKind.Group),
            7 => (new LogicalTypeInfo(LogicalTypeKind.Time, Unit: TimeUnit.Millis), NodeKind.Group),
            8 => (new LogicalTypeInfo(LogicalTypeKind.Time, Unit: TimeUnit.Micros), NodeKind.Group),
            9 => (new LogicalTypeInfo(LogicalTypeKind.Timestamp, Unit: TimeUnit.Millis), NodeKind.Group),
            10 => (new LogicalTypeInfo(LogicalTypeKind.Timestamp, Unit: TimeUnit.Micros), NodeKind.Group),
            11 => (new LogicalTypeInfo(LogicalTypeKind.Integer, BitWidth: 8), NodeKind.Group),
            12 => (new LogicalTypeInfo(LogicalTypeKind.Integer, BitWidth: 16), NodeKind.Group),
            13 => (new LogicalTypeInfo(LogicalTypeKind.Integer, BitWidth: 32), NodeKind.Group),
            14 => (new LogicalTypeInfo(LogicalTypeKind.Integer, BitWidth: 64), NodeKind.Group),
            15 => (new LogicalTypeInfo(LogicalTypeKind.Integer, BitWidth: 8, IsSigned: true), NodeKind.Group),
            16 => (new LogicalTypeInfo(LogicalTypeKind.Integer, BitWidth: 16, IsSigned: true), NodeKind.Group),
            17 => (new LogicalTypeInfo(LogicalTypeKind.Integer, BitWidth: 32, IsSigned: true), NodeKind.Group),
            18 => (new LogicalTypeInfo(LogicalTypeKind.Integer, BitWidth: 64, IsSigned: true), NodeKind.Group),
            19 => (new LogicalTypeInfo(LogicalTypeKind.Json), NodeKind.Group),
            _ => (decimalType, NodeKind.Group)
        };

    static void ReadRowGroups(ref CompactProtocolReader reader, PhysicalMetadataStore store)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new CorruptParquetException("Expected row_groups to be encoded as a list of structs.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Row group count {count} exceeds remaining input bytes.");

        var rowGroupCount = checked((int)count);
        store.EnsureRowGroups(rowGroupCount);
        for (var ordinal = 0; ordinal < rowGroupCount; ordinal++)
            store.RowGroups[ordinal] = ReadRowGroup(ref reader, store, ordinal,
                store.FooterOffset + (ulong)reader.Offset);
        store.RowGroupCount = rowGroupCount;
        
    }

    static PhysicalRowGroup ReadRowGroup(ref CompactProtocolReader reader, PhysicalMetadataStore store, int ordinal,
        ulong metadataOffset)
    {
        var columnChunkOffset = 0UL;
        var rowCount = 0UL;
        var columnStart = store.ColumnChunkCount;
        var columnCount = 0;
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    columnCount = ReadColumns(ref reader, store, ordinal);
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

        if (columnChunkOffset == 0 && columnCount != 0)
            columnChunkOffset = store.ColumnChunks[columnStart].ChunkOffset;
        
        return new PhysicalRowGroup(ordinal, metadataOffset, columnChunkOffset, rowCount, columnStart, columnCount);
    }

    static int ReadColumns(ref CompactProtocolReader reader, PhysicalMetadataStore store, int rowGroupOrdinal)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Struct)
            throw new CorruptParquetException("Expected row group columns to be encoded as a list of structs.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Column count {count} exceeds remaining input bytes.");

        var columnCount = checked((int)count);
        store.EnsureColumnChunks(checked(store.ColumnChunkCount + columnCount));
        for (var i = 0; i < columnCount; i++)
            store.ColumnChunks[store.ColumnChunkCount++] = ReadColumn(ref reader, store, rowGroupOrdinal, i);
        
        return columnCount;
    }

    static ParquetColumnChunkInfo ReadColumn(ref CompactProtocolReader reader, PhysicalMetadataStore store,
        int rowGroupOrdinal, int expectedColumnOrdinal)
    {
        var dataPageOffset = 0UL;
        var dictionaryPageOffset = 0UL;
        var totalCompressedSize = 0UL;
        var totalUncompressedSize = 0UL;
        var columnIndexOffset = 0UL;
        var columnIndexLength = 0U;
        var offsetIndexOffset = 0UL;
        var offsetIndexLength = 0U;
        var compression = CompressionKind.None;
        var physicalType = (ParquetPhysicalType?)null;
        var encodings = default(ParquetColumnChunkEncodings);

        reader.BeginStruct();

        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))

        {
            switch (fieldId)
            {
                case 2:
                    dataPageOffset = reader.ReadI64AsU64();
                    break;
                case 3:
                    ReadColumnMetadata(ref reader, store, ref physicalType, ref compression, ref dataPageOffset,
                        ref dictionaryPageOffset, ref totalCompressedSize, ref totalUncompressedSize,
                        ref encodings, expectedColumnOrdinal);
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

        if (!physicalType.HasValue)
            throw new CorruptParquetException("Column chunk metadata is missing a physical type.");
        
        return new ParquetColumnChunkInfo(rowGroupOrdinal, expectedColumnOrdinal, physicalType.Value, compression,
            dataPageOffset, dictionaryPageOffset, totalCompressedSize, totalUncompressedSize, columnIndexOffset,
            columnIndexLength, offsetIndexOffset, offsetIndexLength, encodings);
    }

    static void ReadColumnMetadata(ref CompactProtocolReader reader, PhysicalMetadataStore store,
        ref ParquetPhysicalType? physicalType, ref CompressionKind compression, ref ulong dataPageOffset,
        ref ulong dictionaryPageOffset, ref ulong totalCompressedSize, ref ulong totalUncompressedSize,
        ref ParquetColumnChunkEncodings encodings, int expectedColumnOrdinal)
    {
        reader.BeginStruct();
        while (reader.TryReadFieldHeader(out var fieldId, out var type, out var inlineBool))
        {
            switch (fieldId)
            {
                case 1:
                    physicalType = ReadPhysicalType(reader.ReadI32());
                    break;
                case 2:
                    encodings = ReadEncodings(ref reader);
                    break;
                case 3:
                    ReadAndValidatePath(ref reader, store, expectedColumnOrdinal);
                    break;
                case 4:
                    compression = ParquetThriftConversions.ReadCompression(reader.ReadI32());
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

    static ParquetColumnChunkEncodings ReadEncodings(ref CompactProtocolReader reader)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.I32)
            throw new CorruptParquetException("Expected encoding ids to be encoded as I32 list elements.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Encoding count {count} exceeds remaining input bytes.");
        if (count > ParquetColumnChunkEncodings.MaxCount)
            throw new CorruptParquetException(
                $"Encoding count {count} exceeds the supported maximum of {ParquetColumnChunkEncodings.MaxCount}.");

        var encodingCount = checked((int)count);
        var encodings = default(ParquetColumnChunkEncodings.EncodingBuffer);
        for (var i = 0; i < encodingCount; i++)
            encodings[i] = ParquetThriftConversions.ReadEncoding(reader.ReadI32());
        
        return new ParquetColumnChunkEncodings(encodings, encodingCount);
    }

    static void ReadAndValidatePath(ref CompactProtocolReader reader, PhysicalMetadataStore store,
        int expectedColumnOrdinal)
    {
        var (count, elementType) = reader.ReadListHeader();
        if (elementType != CompactProtocolType.Binary)
            throw new CorruptParquetException("Expected path_in_schema to be encoded as binary list elements.");
        if (count > reader.Remaining)
            throw new CorruptParquetException($"Path segment count {count} exceeds remaining input bytes.");
        if ((uint)expectedColumnOrdinal >= (uint)store.ColumnCount)
            throw new CorruptParquetException(
                $"Column chunk ordinal {expectedColumnOrdinal} is outside the file schema column count ({store.ColumnCount}).");

        var column = store.Columns[expectedColumnOrdinal];
        if (count != (uint)column.PathSegmentCount)
            throw new CorruptParquetException(
                $"Column chunk path has {count} segments, but schema column {expectedColumnOrdinal} has {column.PathSegmentCount}.");

        for (var segmentOrdinal = 0; segmentOrdinal < column.PathSegmentCount; segmentOrdinal++)
        {
            var actual = reader.ReadBinary();
            var nodeOrdinal = column.NodeOrdinal;
            for (var i = column.PathSegmentCount - 1; i > segmentOrdinal; i--)
                nodeOrdinal = store.SchemaNodes[nodeOrdinal].ParentOrdinal;
            var node = store.SchemaNodes[nodeOrdinal];
            var expected = store.FooterBytes.AsSpan(node.NameOffset, node.NameLength);
            if (!actual.SequenceEqual(expected))
                throw new CorruptParquetException(
                    $"Column chunk path does not match schema column {expectedColumnOrdinal}.");
        }
        
    }

    internal static ParquetPhysicalType ReadPhysicalType(int type)
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

}
