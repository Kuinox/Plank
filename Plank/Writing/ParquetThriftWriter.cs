using System.Collections.Immutable;
using System.Text;
using Plank.Schema;

namespace Plank.Writing;

internal static class ParquetThriftWriter
{
    static readonly System.Text.Encoding Utf8 = new UTF8Encoding(false, true);

    internal static int WriteDataPageHeader(Span<byte> destination, int valueCount, int nullCount, int rowCount, EncodingKind encoding, int definitionLevelsByteLength, int repetitionLevelsByteLength, int uncompressedSize, int compressedSize, bool isCompressed)
    {
        var writer = new CompactSpanWriter(destination);
        var previous = writer.BeginStruct();

        writer.WriteFieldI32(1, (int)PageType.DataPageV2);
        writer.WriteFieldI32(2, uncompressedSize);
        writer.WriteFieldI32(3, compressedSize);
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
        return writer._index;
    }

    internal static int GetFileMetaDataSize(ParquetSchema schema, ColumnLogicalType[] columnLogicalTypes, ColumnSemanticRegistry.ColumnSemanticState[] semanticStates, ParquetWriter.RowGroupInfo[] rowGroups, int rowGroupCount)
    {
        var writer = new CompactSizeCounter();
        var previous = writer.BeginStruct();

        writer.WriteFieldI32(1, 1);
        WriteSchema(ref writer, schema, columnLogicalTypes, semanticStates);
        writer.WriteFieldI64(3, CountRows(rowGroups, rowGroupCount));
        WriteRowGroups(ref writer, schema, rowGroups, rowGroupCount);

        writer.EndStruct(previous);
        return writer._count;
    }

    internal static int WriteFileMetaData(Span<byte> destination, ParquetSchema schema, ColumnLogicalType[] columnLogicalTypes, ColumnSemanticRegistry.ColumnSemanticState[] semanticStates, ParquetWriter.RowGroupInfo[] rowGroups, int rowGroupCount)
    {
        var writer = new CompactSpanWriter(destination);
        var previous = writer.BeginStruct();

        writer.WriteFieldI32(1, 1);
        WriteSchema(ref writer, schema, columnLogicalTypes, semanticStates);
        writer.WriteFieldI64(3, CountRows(rowGroups, rowGroupCount));
        WriteRowGroups(ref writer, schema, rowGroups, rowGroupCount);

        writer.EndStruct(previous);
        return writer._index;
    }

    static void WriteSchema(ref CompactSizeCounter writer, ParquetSchema schema, ColumnLogicalType[] columnLogicalTypes, ColumnSemanticRegistry.ColumnSemanticState[] semanticStates)
    {
        ImmutableArray<Column> columns = schema.Columns.IsDefault ? [] : schema.Columns;
        var count = GetSchemaNodeCount(columns);
        writer.WriteFieldHeader(2, CompactType.List);
        writer.WriteListHeader(count, CompactType.Struct);

        var previous = writer.BeginStruct();
        writer.WriteFieldString(4, "schema");
        writer.WriteFieldI32(5, columns.Length);
        writer.EndStruct(previous);

        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            WriteColumnSchema(ref writer, column, columnLogicalTypes, semanticStates, i);
        }
    }

    static void WriteRowGroups(ref CompactSizeCounter writer, ParquetSchema schema, ParquetWriter.RowGroupInfo[] rowGroups, int rowGroupCount)
    {
        writer.WriteFieldHeader(4, CompactType.List);
        writer.WriteListHeader(rowGroupCount, CompactType.Struct);
        ImmutableArray<Column> columns = schema.Columns.IsDefault ? [] : schema.Columns;

        for (var i = 0; i < rowGroupCount; i++)
        {
            var rowGroup = rowGroups[i];
            var previous = writer.BeginStruct();
            WriteRowGroupColumns(ref writer, columns, rowGroup.Columns);
            writer.WriteFieldI64(2, GetRowGroupTotalUncompressedSize(rowGroup.Columns));
            writer.WriteFieldI64(3, rowGroup.RowCount);
            if (columns.Length > 0)
                writer.WriteFieldI64(5, rowGroup.Columns[0].Offset);
            writer.WriteFieldI64(6, GetRowGroupTotalCompressedSize(rowGroup.Columns));
            writer.EndStruct(previous);
        }
    }

    static void WriteRowGroupColumns(ref CompactSizeCounter writer, ImmutableArray<Column> columns, ParquetWriter.ColumnChunkMetadata[] metadata)
    {
        writer.WriteFieldHeader(1, CompactType.List);
        writer.WriteListHeader(columns.Length, CompactType.Struct);

        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            var chunk = metadata[i];
            var previous = writer.BeginStruct();
            writer.WriteFieldI64(2, 0);
            writer.WriteFieldHeader(3, CompactType.Struct);

            var previousMeta = writer.BeginStruct();
            writer.WriteFieldI32(1, GetType(column.PhysicalType));
            WriteEncodings(ref writer, chunk.Encoding);
            WritePath(ref writer, column);
            writer.WriteFieldI32(4, GetCompression(chunk.Compression));
            writer.WriteFieldI64(5, chunk.ValueCount);
            writer.WriteFieldI64(6, chunk.TotalUncompressedSize);
            writer.WriteFieldI64(7, chunk.TotalCompressedSize);
            writer.WriteFieldI64(9, chunk.Offset);
            writer.EndStruct(previousMeta);

            writer.EndStruct(previous);
        }
    }

    static void WriteEncodings(ref CompactSizeCounter writer, EncodingKind dataEncoding)
    {
        var valueEncoding = GetEncoding(dataEncoding);
        var rleEncoding = GetEncoding(EncodingKind.Rle);
        var count = valueEncoding == rleEncoding ? 1 : 2;

        writer.WriteFieldHeader(2, CompactType.List);
        writer.WriteListHeader(count, CompactType.I32);
        writer.WriteI32(valueEncoding);
        if (count == 2)
            writer.WriteI32(rleEncoding);
    }

    static void WritePath(ref CompactSizeCounter writer, string name)
    {
        writer.WriteFieldHeader(3, CompactType.List);
        writer.WriteListHeader(1, CompactType.Binary);
        writer.WriteBinary(name);
    }

    static void WritePath(ref CompactSizeCounter writer, Column column)
    {
        if (column.Options.Repetition is not ParquetRepetition.Repeated)
        {
            WritePath(ref writer, column.Name);
            return;
        }

        writer.WriteFieldHeader(3, CompactType.List);
        writer.WriteListHeader(3, CompactType.Binary);
        writer.WriteBinary(column.Name);
        writer.WriteBinary("list");
        writer.WriteBinary("element");
    }

    static void WriteSchema(ref CompactSpanWriter writer, ParquetSchema schema, ColumnLogicalType[] columnLogicalTypes, ColumnSemanticRegistry.ColumnSemanticState[] semanticStates)
    {
        ImmutableArray<Column> columns = schema.Columns.IsDefault ? [] : schema.Columns;
        var count = GetSchemaNodeCount(columns);
        writer.WriteFieldHeader(2, CompactType.List);
        writer.WriteListHeader(count, CompactType.Struct);

        var previous = writer.BeginStruct();
        writer.WriteFieldString(4, "schema");
        writer.WriteFieldI32(5, columns.Length);
        writer.EndStruct(previous);

        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            WriteColumnSchema(ref writer, column, columnLogicalTypes, semanticStates, i);
        }
    }

    static void WriteConvertedType(ref CompactSizeCounter writer, ColumnLogicalType[] columnLogicalTypes, int columnIndex)
    {
        if ((uint)columnIndex >= (uint)columnLogicalTypes.Length)
            return;

        var logicalType = columnLogicalTypes[columnIndex];
        switch (logicalType)
        {
            case ColumnLogicalType.TimestampMicrosUtc:
                writer.WriteFieldI32(6, (int)ConvertedType.TimestampMicros);
                break;
            case ColumnLogicalType.Utf8:
                writer.WriteFieldI32(6, (int)ConvertedType.Utf8);
                break;
            case ColumnLogicalType.Date:
                writer.WriteFieldI32(6, (int)ConvertedType.Date);
                break;
            case ColumnLogicalType.TimeMicros:
                writer.WriteFieldI32(6, (int)ConvertedType.TimeMicros);
                break;
        }
    }

    static void WriteConvertedType(ref CompactSpanWriter writer, ColumnLogicalType[] columnLogicalTypes, int columnIndex)
    {
        if ((uint)columnIndex >= (uint)columnLogicalTypes.Length)
            return;

        var logicalType = columnLogicalTypes[columnIndex];
        switch (logicalType)
        {
            case ColumnLogicalType.TimestampMicrosUtc:
                writer.WriteFieldI32(6, (int)ConvertedType.TimestampMicros);
                break;
            case ColumnLogicalType.Utf8:
                writer.WriteFieldI32(6, (int)ConvertedType.Utf8);
                break;
            case ColumnLogicalType.Date:
                writer.WriteFieldI32(6, (int)ConvertedType.Date);
                break;
            case ColumnLogicalType.TimeMicros:
                writer.WriteFieldI32(6, (int)ConvertedType.TimeMicros);
                break;
        }
    }

    static void WriteRowGroups(ref CompactSpanWriter writer, ParquetSchema schema, ParquetWriter.RowGroupInfo[] rowGroups, int rowGroupCount)
    {
        writer.WriteFieldHeader(4, CompactType.List);
        writer.WriteListHeader(rowGroupCount, CompactType.Struct);
        ImmutableArray<Column> columns = schema.Columns.IsDefault ? [] : schema.Columns;

        for (var i = 0; i < rowGroupCount; i++)
        {
            var rowGroup = rowGroups[i];
            var previous = writer.BeginStruct();
            WriteRowGroupColumns(ref writer, columns, rowGroup.Columns);
            writer.WriteFieldI64(2, GetRowGroupTotalUncompressedSize(rowGroup.Columns));
            writer.WriteFieldI64(3, rowGroup.RowCount);
            if (columns.Length > 0)
                writer.WriteFieldI64(5, rowGroup.Columns[0].Offset);
            writer.WriteFieldI64(6, GetRowGroupTotalCompressedSize(rowGroup.Columns));
            writer.EndStruct(previous);
        }
    }

    static void WriteRowGroupColumns(ref CompactSpanWriter writer, ImmutableArray<Column> columns, ParquetWriter.ColumnChunkMetadata[] metadata)
    {
        writer.WriteFieldHeader(1, CompactType.List);
        writer.WriteListHeader(columns.Length, CompactType.Struct);

        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            var chunk = metadata[i];
            var previous = writer.BeginStruct();
            writer.WriteFieldI64(2, 0);
            writer.WriteFieldHeader(3, CompactType.Struct);

            var previousMeta = writer.BeginStruct();
            writer.WriteFieldI32(1, GetType(column.PhysicalType));
            WriteEncodings(ref writer, chunk.Encoding);
            WritePath(ref writer, column);
            writer.WriteFieldI32(4, GetCompression(chunk.Compression));
            writer.WriteFieldI64(5, chunk.ValueCount);
            writer.WriteFieldI64(6, chunk.TotalUncompressedSize);
            writer.WriteFieldI64(7, chunk.TotalCompressedSize);
            writer.WriteFieldI64(9, chunk.Offset);
            writer.EndStruct(previousMeta);

            writer.EndStruct(previous);
        }
    }

    static void WriteEncodings(ref CompactSpanWriter writer, EncodingKind dataEncoding)
    {
        var valueEncoding = GetEncoding(dataEncoding);
        var rleEncoding = GetEncoding(EncodingKind.Rle);
        var count = valueEncoding == rleEncoding ? 1 : 2;

        writer.WriteFieldHeader(2, CompactType.List);
        writer.WriteListHeader(count, CompactType.I32);
        writer.WriteI32(valueEncoding);
        if (count == 2)
            writer.WriteI32(rleEncoding);
    }

    static void WritePath(ref CompactSpanWriter writer, string name)
    {
        writer.WriteFieldHeader(3, CompactType.List);
        writer.WriteListHeader(1, CompactType.Binary);
        writer.WriteBinary(name);
    }

    static void WritePath(ref CompactSpanWriter writer, Column column)
    {
        if (column.Options.Repetition is not ParquetRepetition.Repeated)
        {
            WritePath(ref writer, column.Name);
            return;
        }

        writer.WriteFieldHeader(3, CompactType.List);
        writer.WriteListHeader(3, CompactType.Binary);
        writer.WriteBinary(column.Name);
        writer.WriteBinary("list");
        writer.WriteBinary("element");
    }

    static long CountRows(ParquetWriter.RowGroupInfo[] rowGroups, int rowGroupCount)
    {
        long total = 0;
        for (var i = 0; i < rowGroupCount; i++)
            total = checked(total + rowGroups[i].RowCount);
        return total;
    }

    static long GetRowGroupTotalUncompressedSize(ParquetWriter.ColumnChunkMetadata[] columns)
    {
        long total = 0;
        foreach (var t in columns)
            total = checked(total + t.TotalUncompressedSize);

        return total;
    }

    static long GetRowGroupTotalCompressedSize(ParquetWriter.ColumnChunkMetadata[] columns)
    {
        long total = 0;
        foreach (var t in columns)
            total = checked(total + t.TotalCompressedSize);

        return total;
    }

    static int GetType(ParquetPhysicalType type)
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

    static int GetSchemaNodeCount(ImmutableArray<Column> columns)
    {
        var count = 1;
        for (var i = 0; i < columns.Length; i++)
            count += columns[i].Options.Repetition is ParquetRepetition.Repeated ? 3 : 1;
        return count;
    }

    static void WriteColumnSchema(ref CompactSizeCounter writer, Column column, ColumnLogicalType[] columnLogicalTypes, ColumnSemanticRegistry.ColumnSemanticState[] semanticStates, int columnIndex)
    {
        if (column.Options.Repetition is not ParquetRepetition.Repeated)
        {
            var previous = writer.BeginStruct();
            writer.WriteFieldI32(1, GetType(column.PhysicalType));
            writer.WriteFieldI32(3, GetRepetition(column.Options.Repetition));
            writer.WriteFieldString(4, column.Name);
            WriteConvertedType(ref writer, columnLogicalTypes, columnIndex);
            writer.EndStruct(previous);
            return;
        }

        var outer = writer.BeginStruct();
        writer.WriteFieldI32(3, (int)FieldRepetitionType.Required);
        writer.WriteFieldString(4, column.Name);
        writer.WriteFieldI32(5, 1);
        writer.WriteFieldI32(6, (int)ConvertedType.List);
        writer.EndStruct(outer);

        var middle = writer.BeginStruct();
        writer.WriteFieldI32(3, (int)FieldRepetitionType.Repeated);
        writer.WriteFieldString(4, "list");
        writer.WriteFieldI32(5, 1);
        writer.EndStruct(middle);

        var element = writer.BeginStruct();
        writer.WriteFieldI32(1, GetType(column.PhysicalType));
        writer.WriteFieldI32(3, GetRepeatedElementRepetition(semanticStates, columnIndex));
        writer.WriteFieldString(4, "element");
        WriteConvertedType(ref writer, columnLogicalTypes, columnIndex);
        writer.EndStruct(element);
    }

    static void WriteColumnSchema(ref CompactSpanWriter writer, Column column, ColumnLogicalType[] columnLogicalTypes, ColumnSemanticRegistry.ColumnSemanticState[] semanticStates, int columnIndex)
    {
        if (column.Options.Repetition is not ParquetRepetition.Repeated)
        {
            var previous = writer.BeginStruct();
            writer.WriteFieldI32(1, GetType(column.PhysicalType));
            writer.WriteFieldI32(3, GetRepetition(column.Options.Repetition));
            writer.WriteFieldString(4, column.Name);
            WriteConvertedType(ref writer, columnLogicalTypes, columnIndex);
            writer.EndStruct(previous);
            return;
        }

        var outer = writer.BeginStruct();
        writer.WriteFieldI32(3, (int)FieldRepetitionType.Required);
        writer.WriteFieldString(4, column.Name);
        writer.WriteFieldI32(5, 1);
        writer.WriteFieldI32(6, (int)ConvertedType.List);
        writer.EndStruct(outer);

        var middle = writer.BeginStruct();
        writer.WriteFieldI32(3, (int)FieldRepetitionType.Repeated);
        writer.WriteFieldString(4, "list");
        writer.WriteFieldI32(5, 1);
        writer.EndStruct(middle);

        var element = writer.BeginStruct();
        writer.WriteFieldI32(1, GetType(column.PhysicalType));
        writer.WriteFieldI32(3, GetRepeatedElementRepetition(semanticStates, columnIndex));
        writer.WriteFieldString(4, "element");
        WriteConvertedType(ref writer, columnLogicalTypes, columnIndex);
        writer.EndStruct(element);
    }

    static int GetRepeatedElementRepetition(ColumnSemanticRegistry.ColumnSemanticState[] semanticStates, int columnIndex)
    {
        if ((uint)columnIndex < (uint)semanticStates.Length && semanticStates[columnIndex].Repeated == ColumnSemanticRegistry.RepeatedElementState.Optional)
            return (int)FieldRepetitionType.Optional;
        return (int)FieldRepetitionType.Required;
    }

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

    enum PageType
    {
        DataPageV2 = 3
    }

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

    enum ConvertedType
    {
        Utf8 = 0,
        List = 3,
        Date = 6,
        TimeMicros = 8,
        TimestampMicros = 10
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

    ref struct CompactSpanWriter
    {
        readonly Span<byte> _buffer;
        internal int _index;
        int _lastFieldId;

        internal CompactSpanWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _index = 0;
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

        internal void WriteFieldString(int fieldId, string value)
        {
            WriteFieldHeader(fieldId, CompactType.Binary);
            WriteBinary(value);
        }

        internal void WriteFieldBool(int fieldId, bool value)
        {
            WriteFieldHeader(fieldId, value ? CompactType.BooleanTrue : CompactType.BooleanFalse);
        }

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

        void WriteI16(int value)
            => WriteVarInt32(EncodeZigZag32(value));

        internal void WriteI32(int value)
            => WriteVarInt32(EncodeZigZag32(value));

        void WriteI64(long value)
            => WriteVarInt64(EncodeZigZag64(value));

        internal void WriteBinary(string value)
        {
            var byteCount = Utf8.GetByteCount(value);
            WriteVarInt32((uint)byteCount);
            if (byteCount == 0)
                return;

            if (_index > _buffer.Length - byteCount)
                throw new InvalidOperationException("File metadata buffer is too small.");

            Utf8.GetBytes(value.AsSpan(), _buffer.Slice(_index, byteCount));
            _index += byteCount;
        }

        void WriteByte(byte value)
        {
            if ((uint)_index >= (uint)_buffer.Length)
                throw new InvalidOperationException("File metadata buffer is too small.");

            _buffer[_index] = value;
            _index++;
        }

        void WriteBytes(ReadOnlySpan<byte> data)
        {
            if (_index > _buffer.Length - data.Length)
                throw new InvalidOperationException("File metadata buffer is too small.");

            data.CopyTo(_buffer[_index..]);
            _index += data.Length;
        }

        void WriteVarInt32(uint value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        void WriteVarInt64(ulong value)
        {
            while (value >= 0x80)
            {
                WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            WriteByte((byte)value);
        }

        static uint EncodeZigZag32(int value)
            => (uint)((value << 1) ^ (value >> 31));

        static ulong EncodeZigZag64(long value)
            => (ulong)((value << 1) ^ (value >> 63));
    }

    struct CompactSizeCounter
    {
        internal int _count;
        int _lastFieldId;

        internal int BeginStruct()
        {
            var previous = _lastFieldId;
            _lastFieldId = 0;
            return previous;
        }

        internal void EndStruct(int previousFieldId)
        {
            WriteByte();
            _lastFieldId = previousFieldId;
        }

        internal void WriteFieldHeader(int fieldId, CompactType _)
        {
            var delta = fieldId - _lastFieldId;
            if (delta > 0 && delta <= 15)
                WriteByte();
            else
            {
                WriteByte();
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

        internal void WriteFieldString(int fieldId, string value)
        {
            WriteFieldHeader(fieldId, CompactType.Binary);
            WriteBinary(value);
        }

        internal void WriteFieldBool(int fieldId, bool value)
            => WriteFieldHeader(fieldId, value ? CompactType.BooleanTrue : CompactType.BooleanFalse);

        internal void WriteListHeader(int count, CompactType _)
        {
            if (count < 15)
                WriteByte();
            else
            {
                WriteByte();
                WriteVarInt32((uint)count);
            }
        }

        internal void WriteI16(int value)
            => WriteVarInt32(EncodeZigZag32(value));

        internal void WriteI32(int value)
            => WriteVarInt32(EncodeZigZag32(value));

        internal void WriteI64(long value)
            => WriteVarInt64(EncodeZigZag64(value));

        internal void WriteBinary(string value)
        {
            var byteCount = Utf8.GetByteCount(value);
            WriteVarInt32((uint)byteCount);
            _count += byteCount;
        }

        void WriteByte()
            => _count += 1;

        void WriteVarInt32(uint value)
        {
            while (value >= 0x80)
            {
                WriteByte();
                value >>= 7;
            }

            WriteByte();
        }

        void WriteVarInt64(ulong value)
        {
            while (value >= 0x80)
            {
                WriteByte();
                value >>= 7;
            }

            WriteByte();
        }

        static uint EncodeZigZag32(int value)
            => (uint)((value << 1) ^ (value >> 31));

        static ulong EncodeZigZag64(long value)
            => (ulong)((value << 1) ^ (value >> 63));
    }
}
