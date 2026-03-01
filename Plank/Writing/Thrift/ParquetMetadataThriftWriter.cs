using System.Collections.Immutable;
using System.Text;
using Plank.Schema;
using TextEncoding = System.Text.Encoding;

namespace Plank.Writing.Thrift;

static class ParquetMetadataThriftWriter
{
    static readonly TextEncoding _utf8 = new UTF8Encoding(false, true);

    internal static void WriteDataPageHeaderV2(ref BufferWriter destination, int rowCount, int valueCount, int nullCount,
        int repetitionLevelsByteLength, int definitionLevelsByteLength, EncodingKind encoding, int uncompressedPageSize,
        int compressedPageSize, bool isCompressed)
    {
        var writer = new CompactWriter(ref destination);
        var previous = writer.BeginStruct();
        writer.WriteFieldI32(1, (int)PageType.DataPageV2);
        writer.WriteFieldI32(2, uncompressedPageSize);
        writer.WriteFieldI32(3, compressedPageSize);
        writer.WriteFieldHeader(8, CompactType.Struct);

        var previousData = writer.BeginStruct();
        writer.WriteFieldI32(1, valueCount);
        writer.WriteFieldI32(2, nullCount);
        writer.WriteFieldI32(3, rowCount);
        writer.WriteFieldI32(4, GetEncoding(encoding));
        writer.WriteFieldI32(5, definitionLevelsByteLength);
        writer.WriteFieldI32(6, repetitionLevelsByteLength);
        writer.WriteFieldBool(7, isCompressed);
        writer.EndStruct(previousData);

        writer.EndStruct(previous);
    }

    internal static void WriteDictionaryPageHeader(ref BufferWriter destination, int valueCount, int uncompressedPageSize,
        int compressedPageSize)
    {
        var writer = new CompactWriter(ref destination);
        var previous = writer.BeginStruct();
        writer.WriteFieldI32(1, (int)PageType.DictionaryPage);
        writer.WriteFieldI32(2, uncompressedPageSize);
        writer.WriteFieldI32(3, compressedPageSize);
        writer.WriteFieldHeader(7, CompactType.Struct);

        var previousDictionary = writer.BeginStruct();
        writer.WriteFieldI32(1, valueCount);
        writer.WriteFieldI32(2, GetEncoding(EncodingKind.Plain));
        writer.EndStruct(previousDictionary);

        writer.EndStruct(previous);
    }

    internal static void WriteFileMetaData(ref BufferWriter destination, ParquetSchema schema, int rowGroupCount,
        long totalRowCount, ref BufferWriter serializedRowGroups)
    {
        var writer = new CompactWriter(ref destination);
        var previous = writer.BeginStruct();
        writer.WriteFieldI32(1, 1);
        WriteSchema(ref writer, schema);
        writer.WriteFieldI64(3, totalRowCount);
        writer.WriteFieldHeader(4, CompactType.List);
        writer.WriteListHeader(rowGroupCount, CompactType.Struct);
        writer.WriteRaw(ref serializedRowGroups);
        writer.EndStruct(previous);
    }

    internal static void WriteRowGroup(ref BufferWriter destination, ReadOnlySpan<Column> columns,
        ReadOnlySpan<ColumnChunkMetadata> metadata, int rowCount)
        => WriteRowGroup(ref destination, columns, default, metadata, rowCount);

    internal static void WriteRowGroup(ref BufferWriter destination, ReadOnlySpan<Column> columns,
        ReadOnlySpan<string[]> columnPaths, ReadOnlySpan<ColumnChunkMetadata> metadata, int rowCount)
    {
        var writer = new CompactWriter(ref destination);
        var previous = writer.BeginStruct();
        writer.WriteFieldHeader(1, CompactType.List);
        writer.WriteListHeader(columns.Length, CompactType.Struct);

        long totalUncompressedSize = 0;
        long totalCompressedSize = 0;
        long rowGroupOffset = 0;
        var hasRowGroupOffset = false;
        for (var i = 0; i < columns.Length; i++)
        {
            ref readonly var chunk = ref metadata[i];
            var path = columnPaths.IsEmpty ? GetPathSegments(columns[i].Name) : columnPaths[i];
            WriteColumnChunk(ref writer, columns[i], path, chunk);
            totalUncompressedSize = checked(totalUncompressedSize + chunk.TotalUncompressedSize);
            totalCompressedSize = checked(totalCompressedSize + chunk.TotalCompressedSize);
            if (hasRowGroupOffset)
                continue;
            rowGroupOffset = chunk.DataPageOffset;
            hasRowGroupOffset = true;
        }

        writer.WriteFieldI64(2, totalUncompressedSize);
        writer.WriteFieldI64(3, rowCount);
        if (hasRowGroupOffset)
            writer.WriteFieldI64(5, rowGroupOffset);
        writer.WriteFieldI64(6, totalCompressedSize);
        writer.EndStruct(previous);
    }

    static void WriteSchema(ref CompactWriter writer, ParquetSchema schema)
    {
        ImmutableArray<ColumnDefinition> definitions = schema.Definitions.IsDefault ? [] : schema.Definitions;
        writer.WriteFieldHeader(2, CompactType.List);
        writer.WriteListHeader(checked(CountSchemaNodes(definitions.AsSpan()) + 1), CompactType.Struct);

        var previousRoot = writer.BeginStruct();
        writer.WriteFieldBinary(4, "schema");
        writer.WriteFieldI32(5, definitions.Length);
        writer.EndStruct(previousRoot);

        for (var i = 0; i < definitions.Length; i++)
            WriteSchemaNode(ref writer, definitions[i]);
    }

    static int CountSchemaNodes(ReadOnlySpan<ColumnDefinition> definitions)
    {
        var count = 0;
        for (var i = 0; i < definitions.Length; i++)
            count = checked(count + CountSchemaNodes(definitions[i]));

        return count;
    }

    static int CountSchemaNodes(ColumnDefinition node)
        => node.Kind switch
        {
            NodeKind.Leaf => 1,
            NodeKind.Group => checked(1 + CountSchemaNodes(node.Children.AsSpan())),
            NodeKind.List => checked(2 + CountSchemaNodes(GetListElement(node))),
            NodeKind.Map => checked(2 + CountSchemaNodes(GetMapKey(node)) + CountSchemaNodes(GetMapValue(node))),
            _ => throw new NotSupportedException($"Node kind '{node.Kind}' is not supported.")
        };

    static void WriteSchemaNode(ref CompactWriter writer, ColumnDefinition node, string? nameOverride = null)
    {
        switch (node.Kind)
        {
            case NodeKind.Leaf:
                WriteLeafSchemaNode(ref writer, nameOverride ?? node.Name, node);
                return;
            case NodeKind.Group:
                WriteGroupSchemaNode(ref writer, nameOverride ?? node.Name, node);
                return;
            case NodeKind.List:
                WriteListSchemaNode(ref writer, nameOverride ?? node.Name, node);
                return;
            case NodeKind.Map:
                WriteMapSchemaNode(ref writer, nameOverride ?? node.Name, node);
                return;
            default:
                throw new NotSupportedException($"Node kind '{node.Kind}' is not supported.");
        }
    }

    static void WriteLeafSchemaNode(ref CompactWriter writer, string name, ColumnDefinition node)
    {
        if (node.PhysicalType is not { } physicalType)
            throw new InvalidOperationException($"LEAF node '{node.Name}' must define a physical type.");

        var previous = writer.BeginStruct();
        writer.WriteFieldI32(1, GetPhysicalType(physicalType));
        var options = node.Options ?? ColumnOptions.Default;
        if (physicalType == ParquetPhysicalType.FixedLenByteArray)
            writer.WriteFieldI32(2, checked((int)options.TypeLength));
        writer.WriteFieldI32(3, GetRepetition(node.Repetition));
        writer.WriteFieldBinary(4, name);
        if (node.LogicalType is not null)
            WriteLogicalType(ref writer, node.LogicalType);
        writer.EndStruct(previous);
    }

    static void WriteGroupSchemaNode(ref CompactWriter writer, string name, ColumnDefinition node)
    {
        var previous = writer.BeginStruct();
        writer.WriteFieldI32(3, GetRepetition(node.Repetition));
        writer.WriteFieldBinary(4, name);
        writer.WriteFieldI32(5, node.Children.Length);
        writer.EndStruct(previous);

        for (var i = 0; i < node.Children.Length; i++)
            WriteSchemaNode(ref writer, node.Children[i]);
    }

    static void WriteListSchemaNode(ref CompactWriter writer, string name, ColumnDefinition node)
    {
        var element = GetListElement(node);

        var listGroup = writer.BeginStruct();
        writer.WriteFieldI32(3, GetRepetition(node.Repetition));
        writer.WriteFieldBinary(4, name);
        writer.WriteFieldI32(5, 1);
        writer.WriteFieldI32(6, (int)ConvertedType.List);
        writer.WriteFieldHeader(10, CompactType.Struct);
        var previousLogicalType = writer.BeginStruct();
        writer.WriteFieldHeader(3, CompactType.Struct);
        var previousListType = writer.BeginStruct();
        writer.EndStruct(previousListType);
        writer.EndStruct(previousLogicalType);
        writer.EndStruct(listGroup);

        var repeatedList = writer.BeginStruct();
        writer.WriteFieldI32(3, GetRepetition(ParquetRepetition.Repeated));
        writer.WriteFieldBinary(4, "list");
        writer.WriteFieldI32(5, 1);
        writer.EndStruct(repeatedList);

        WriteSchemaNode(ref writer, element, "element");
    }

    static ColumnDefinition GetListElement(ColumnDefinition node)
    {
        if (node.Children.Length == 1)
            return node.Children[0];

        throw new InvalidOperationException($"LIST node '{node.Name}' must contain exactly one child element.");
    }

    static void WriteMapSchemaNode(ref CompactWriter writer, string name, ColumnDefinition node)
    {
        var key = GetMapKey(node);
        var value = GetMapValue(node);

        var mapGroup = writer.BeginStruct();
        writer.WriteFieldI32(3, GetRepetition(node.Repetition));
        writer.WriteFieldBinary(4, name);
        writer.WriteFieldI32(5, 1);
        writer.WriteFieldI32(6, (int)ConvertedType.Map);
        writer.EndStruct(mapGroup);

        var keyValue = writer.BeginStruct();
        writer.WriteFieldI32(3, GetRepetition(ParquetRepetition.Repeated));
        writer.WriteFieldBinary(4, "key_value");
        writer.WriteFieldI32(5, 2);
        writer.EndStruct(keyValue);

        WriteSchemaNode(ref writer, ForceRepetition(key, ParquetRepetition.Required), "key");
        WriteSchemaNode(ref writer, value, "value");
    }

    static ColumnDefinition GetMapKey(ColumnDefinition node)
    {
        if (node.Children.Length == 2)
            return node.Children[0];

        throw new InvalidOperationException($"MAP node '{node.Name}' must contain exactly two child nodes (key,value).");
    }

    static ColumnDefinition GetMapValue(ColumnDefinition node)
    {
        if (node.Children.Length == 2)
            return node.Children[1];

        throw new InvalidOperationException($"MAP node '{node.Name}' must contain exactly two child nodes (key,value).");
    }

    static ColumnDefinition ForceRepetition(ColumnDefinition node, ParquetRepetition repetition)
        => node with { Repetition = repetition };

    static void WriteLogicalType(ref CompactWriter writer, LogicalType logicalType)
    {
        switch (logicalType)
        {
            case LogicalType.Date:
                writer.WriteFieldI32(6, (int)ConvertedType.Date);
                writer.WriteFieldHeader(10, CompactType.Struct);
                WriteDateLogicalType(ref writer);
                return;
            case LogicalType.Time time:
                if (time.Unit == TimeUnit.Millis)
                    writer.WriteFieldI32(6, (int)ConvertedType.TimeMillis);
                else if (time.Unit == TimeUnit.Micros)
                    writer.WriteFieldI32(6, (int)ConvertedType.TimeMicros);
                writer.WriteFieldHeader(10, CompactType.Struct);
                WriteTimeLogicalType(ref writer, time.IsAdjustedToUtc, time.Unit);
                return;
            case LogicalType.Timestamp timestamp:
                if (timestamp.Unit == TimeUnit.Millis)
                    writer.WriteFieldI32(6, (int)ConvertedType.TimestampMillis);
                else if (timestamp.Unit == TimeUnit.Micros)
                    writer.WriteFieldI32(6, (int)ConvertedType.TimestampMicros);
                writer.WriteFieldHeader(10, CompactType.Struct);
                WriteTimestampLogicalType(ref writer, timestamp.IsAdjustedToUtc, timestamp.Unit);
                return;
            case LogicalType.String:
                writer.WriteFieldI32(6, (int)ConvertedType.Utf8);
                writer.WriteFieldHeader(10, CompactType.Struct);
                WriteStringLogicalType(ref writer);
                return;
            case LogicalType.Json:
                writer.WriteFieldI32(6, (int)ConvertedType.Json);
                writer.WriteFieldHeader(10, CompactType.Struct);
                WriteJsonLogicalType(ref writer);
                return;
            case LogicalType.Uuid:
                writer.WriteFieldHeader(10, CompactType.Struct);
                WriteUuidLogicalType(ref writer);
                return;
            case LogicalType.Decimal decimalType:
                writer.WriteFieldI32(6, (int)ConvertedType.Decimal);
                writer.WriteFieldI32(7, decimalType.Scale);
                writer.WriteFieldI32(8, decimalType.Precision);
                writer.WriteFieldHeader(10, CompactType.Struct);
                WriteDecimalLogicalType(ref writer, decimalType.Scale, decimalType.Precision);
                return;
            default:
                throw new NotSupportedException($"Logical type '{logicalType.GetType()}' is not supported.");
        }
    }

    static void WriteDateLogicalType(ref CompactWriter writer)
    {
        var previous = writer.BeginStruct();
        writer.WriteFieldHeader(6, CompactType.Struct);
        var previousDate = writer.BeginStruct();
        writer.EndStruct(previousDate);
        writer.EndStruct(previous);
    }

    static void WriteTimeLogicalType(ref CompactWriter writer, bool isAdjustedToUtc, TimeUnit unit)
    {
        var previous = writer.BeginStruct();
        writer.WriteFieldHeader(7, CompactType.Struct);
        var previousTime = writer.BeginStruct();
        writer.WriteFieldBool(1, isAdjustedToUtc);
        writer.WriteFieldHeader(2, CompactType.Struct);
        WriteTimeUnit(ref writer, unit);
        writer.EndStruct(previousTime);
        writer.EndStruct(previous);
    }

    static void WriteTimestampLogicalType(ref CompactWriter writer, bool isAdjustedToUtc, TimeUnit unit)
    {
        var previous = writer.BeginStruct();
        writer.WriteFieldHeader(8, CompactType.Struct);
        var previousTimestamp = writer.BeginStruct();
        writer.WriteFieldBool(1, isAdjustedToUtc);
        writer.WriteFieldHeader(2, CompactType.Struct);
        WriteTimeUnit(ref writer, unit);
        writer.EndStruct(previousTimestamp);
        writer.EndStruct(previous);
    }

    static void WriteStringLogicalType(ref CompactWriter writer)
    {
        var previous = writer.BeginStruct();
        writer.WriteFieldHeader(1, CompactType.Struct);
        var previousString = writer.BeginStruct();
        writer.EndStruct(previousString);
        writer.EndStruct(previous);
    }

    static void WriteJsonLogicalType(ref CompactWriter writer)
    {
        var previous = writer.BeginStruct();
        writer.WriteFieldHeader(12, CompactType.Struct);
        var previousJson = writer.BeginStruct();
        writer.EndStruct(previousJson);
        writer.EndStruct(previous);
    }

    static void WriteUuidLogicalType(ref CompactWriter writer)
    {
        var previous = writer.BeginStruct();
        writer.WriteFieldHeader(14, CompactType.Struct);
        var previousUuid = writer.BeginStruct();
        writer.EndStruct(previousUuid);
        writer.EndStruct(previous);
    }

    static void WriteDecimalLogicalType(ref CompactWriter writer, int scale, int precision)
    {
        var previous = writer.BeginStruct();
        writer.WriteFieldHeader(5, CompactType.Struct);
        var previousDecimal = writer.BeginStruct();
        writer.WriteFieldI32(1, scale);
        writer.WriteFieldI32(2, precision);
        writer.EndStruct(previousDecimal);
        writer.EndStruct(previous);
    }

    static void WriteTimeUnit(ref CompactWriter writer, TimeUnit unit)
    {
        var previous = writer.BeginStruct();
        switch (unit)
        {
            case TimeUnit.Millis:
                writer.WriteFieldHeader(1, CompactType.Struct);
                break;
            case TimeUnit.Micros:
                writer.WriteFieldHeader(2, CompactType.Struct);
                break;
            case TimeUnit.Nanos:
                writer.WriteFieldHeader(3, CompactType.Struct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(unit), unit, "Time unit must be a defined TimeUnit value.");
        }
        var previousUnit = writer.BeginStruct();
        writer.EndStruct(previousUnit);
        writer.EndStruct(previous);
    }

    static void WriteColumnChunk(ref CompactWriter writer, Column column, ReadOnlySpan<string> path,
        in ColumnChunkMetadata metadata)
    {
        var previousChunk = writer.BeginStruct();
        writer.WriteFieldI64(2, metadata.DataPageOffset);
        writer.WriteFieldHeader(3, CompactType.Struct);

        var previousMetadata = writer.BeginStruct();
        writer.WriteFieldI32(1, GetPhysicalType(column.PhysicalType));
        WriteEncodings(ref writer, metadata.DataEncoding, metadata.HasDictionaryPage);
        WritePath(ref writer, path);
        writer.WriteFieldI32(4, GetCompression(metadata.Compression));
        writer.WriteFieldI64(5, metadata.ValueCount);
        writer.WriteFieldI64(6, metadata.TotalUncompressedSize);
        writer.WriteFieldI64(7, metadata.TotalCompressedSize);
        writer.WriteFieldI64(9, metadata.DataPageOffset);
        if (metadata.HasDictionaryPage)
            writer.WriteFieldI64(11, metadata.DictionaryPageOffset);
        writer.EndStruct(previousMetadata);

        writer.EndStruct(previousChunk);
    }

    static void WriteEncodings(ref CompactWriter writer, EncodingKind dataEncoding, bool hasDictionaryPage)
    {
        var data = GetEncoding(dataEncoding);
        var dictionary = GetEncoding(EncodingKind.Plain);

        if (!hasDictionaryPage || data == dictionary)
        {
            writer.WriteFieldHeader(2, CompactType.List);
            writer.WriteListHeader(1, CompactType.I32);
            writer.WriteI32(data);
            return;
        }

        writer.WriteFieldHeader(2, CompactType.List);
        writer.WriteListHeader(2, CompactType.I32);
        writer.WriteI32(dictionary);
        writer.WriteI32(data);
    }

    static string[] GetPathSegments(string columnName)
    {
        if (columnName.IndexOf('.') < 0)
            return [columnName];

        return columnName.Split('.', StringSplitOptions.None);
    }

    static void WritePath(ref CompactWriter writer, ReadOnlySpan<string> path)
    {
        writer.WriteFieldHeader(3, CompactType.List);
        writer.WriteListHeader(path.Length, CompactType.Binary);
        for (var i = 0; i < path.Length; i++)
            writer.WriteBinary(path[i]);
    }

    static int GetPhysicalType(ParquetPhysicalType type)
        => type switch
        {
            ParquetPhysicalType.Boolean => (int)ParquetType.Boolean,
            ParquetPhysicalType.Int32 => (int)ParquetType.Int32,
            ParquetPhysicalType.Int64 => (int)ParquetType.Int64,
            ParquetPhysicalType.Int96 => (int)ParquetType.Int96,
            ParquetPhysicalType.Float => (int)ParquetType.Float,
            ParquetPhysicalType.Double => (int)ParquetType.Double,
            ParquetPhysicalType.ByteArray => (int)ParquetType.ByteArray,
            ParquetPhysicalType.FixedLenByteArray => (int)ParquetType.FixedLenByteArray,
            _ => throw new NotSupportedException($"Physical type '{type}' is not supported.")
        };

    static int GetRepetition(ParquetRepetition repetition)
        => repetition switch
        {
            ParquetRepetition.Required => (int)FieldRepetitionType.Required,
            ParquetRepetition.Optional => (int)FieldRepetitionType.Optional,
            ParquetRepetition.Repeated => (int)FieldRepetitionType.Repeated,
            ParquetRepetition.Unspecified => (int)FieldRepetitionType.Required,
            _ => throw new NotSupportedException($"Repetition '{repetition}' is not supported.")
        };

    static int GetEncoding(EncodingKind encoding)
        => encoding switch
        {
            EncodingKind.Plain => (int)ParquetEncoding.Plain,
            EncodingKind.PlainDictionary => (int)ParquetEncoding.PlainDictionary,
            EncodingKind.RleDictionary => (int)ParquetEncoding.RleDictionary,
            EncodingKind.Rle => (int)ParquetEncoding.Rle,
            EncodingKind.BitPacked => (int)ParquetEncoding.BitPacked,
            EncodingKind.DeltaBinaryPacked => (int)ParquetEncoding.DeltaBinaryPacked,
            EncodingKind.DeltaLengthByteArray => (int)ParquetEncoding.DeltaLengthByteArray,
            EncodingKind.DeltaByteArray => (int)ParquetEncoding.DeltaByteArray,
            EncodingKind.ByteStreamSplit => (int)ParquetEncoding.ByteStreamSplit,
            _ => throw new NotSupportedException($"Encoding '{encoding}' is not supported.")
        };

    static int GetCompression(CompressionKind compression)
        => compression switch
        {
            CompressionKind.None => (int)CompressionCodec.Uncompressed,
            CompressionKind.Snappy => (int)CompressionCodec.Snappy,
            CompressionKind.Gzip => (int)CompressionCodec.Gzip,
            CompressionKind.Brotli => (int)CompressionCodec.Brotli,
            CompressionKind.Lz4 => (int)CompressionCodec.Lz4,
            CompressionKind.Zstd => (int)CompressionCodec.Zstd,
            _ => throw new NotSupportedException($"Compression '{compression}' is not supported.")
        };

    enum ParquetType
    {
        Boolean = 0,
        Int32 = 1,
        Int64 = 2,
        Int96 = 3,
        Float = 4,
        Double = 5,
        ByteArray = 6,
        FixedLenByteArray = 7
    }

    enum FieldRepetitionType
    {
        Required = 0,
        Optional = 1,
        Repeated = 2
    }

    enum ParquetEncoding
    {
        Plain = 0,
        PlainDictionary = 2,
        Rle = 3,
        BitPacked = 4,
        DeltaBinaryPacked = 5,
        DeltaLengthByteArray = 6,
        DeltaByteArray = 7,
        RleDictionary = 8,
        ByteStreamSplit = 9
    }

    enum CompressionCodec
    {
        Uncompressed = 0,
        Snappy = 1,
        Gzip = 2,
        Brotli = 4,
        Lz4 = 5,
        Zstd = 6
    }

    enum PageType
    {
        DataPage = 0,
        IndexPage = 1,
        DictionaryPage = 2,
        DataPageV2 = 3
    }

    enum ConvertedType
    {
        Utf8 = 0,
        Map = 1,
        List = 3,
        Decimal = 5,
        Date = 6,
        TimeMillis = 7,
        TimeMicros = 8,
        TimestampMillis = 9,
        TimestampMicros = 10,
        Json = 19
    }

    enum CompactType : byte
    {
        Stop = 0,
        BooleanTrue = 1,
        BooleanFalse = 2,
        Byte = 3,
        I16 = 4,
        I32 = 5,
        I64 = 6,
        Double = 7,
        Binary = 8,
        List = 9,
        Set = 10,
        Map = 11,
        Struct = 12
    }

    ref struct CompactWriter
    {
        ref BufferWriter _buffer;
        int _lastFieldId;

        internal CompactWriter(ref BufferWriter buffer)
        {
            _buffer = ref buffer;
            _lastFieldId = 0;
        }

        internal int BeginStruct()
        {
            var previous = _lastFieldId;
            _lastFieldId = 0;
            return previous;
        }

        internal void EndStruct(int previousFieldId)
        {
            WriteByte((byte)CompactType.Stop);
            _lastFieldId = previousFieldId;
        }

        internal void WriteFieldHeader(int fieldId, CompactType type)
        {
            var delta = fieldId - _lastFieldId;
            if (delta > 0 && delta <= 15)
                WriteByte((byte)((delta << 4) | (int)type));
            else
            {
                WriteByte((byte)type);
                WriteI16(fieldId);
            }

            _lastFieldId = fieldId;
        }

        internal void WriteFieldI32(int fieldId, int value)
        {
            WriteFieldHeader(fieldId, CompactType.I32);
            WriteI32(value);
        }

        internal void WriteFieldI64(int fieldId, long value)
        {
            WriteFieldHeader(fieldId, CompactType.I64);
            WriteI64(value);
        }

        internal void WriteFieldBinary(int fieldId, string value)
        {
            WriteFieldHeader(fieldId, CompactType.Binary);
            WriteBinary(value);
        }

        internal void WriteFieldBool(int fieldId, bool value)
            => WriteFieldHeader(fieldId, value ? CompactType.BooleanTrue : CompactType.BooleanFalse);

        internal void WriteListHeader(int count, CompactType elementType)
        {
            if (count < 15)
                WriteByte((byte)((count << 4) | (int)elementType));
            else
            {
                WriteByte((byte)(0xF0 | (int)elementType));
                WriteVarInt32((uint)count);
            }
        }

        internal void WriteI32(int value)
            => WriteVarInt32(EncodeZigZag32(value));

        internal void WriteRaw(ref BufferWriter source)
            => _buffer.CopyFrom(ref source);

        void WriteI16(int value)
            => WriteVarInt32(EncodeZigZag32(value));

        void WriteI64(long value)
            => WriteVarInt64(EncodeZigZag64(value));

        internal void WriteBinary(string value)
        {
            var byteCount = _utf8.GetByteCount(value);
            WriteVarInt32((uint)byteCount);
            if (byteCount == 0)
                return;

            var destination = _buffer.GetSpan(byteCount);
            _utf8.GetBytes(value.AsSpan(), destination[..byteCount]);
            _buffer.Advance(byteCount);
        }

        void WriteByte(byte value)
        {
            var destination = _buffer.GetSpan(1);
            destination[0] = value;
            _buffer.Advance(1);
        }

        void WriteVarInt32(uint value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        void WriteVarInt64(ulong value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        static uint EncodeZigZag32(int value)
            => (uint)((value << 1) ^ (value >> 31));

        static ulong EncodeZigZag64(long value)
            => (ulong)((value << 1) ^ (value >> 63));
    }
}
