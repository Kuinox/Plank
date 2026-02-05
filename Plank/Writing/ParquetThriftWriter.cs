using System.Collections.Immutable;
using System.Text;
using Plank.Schema;

namespace Plank.Writing;

internal static class ParquetThriftWriter
{
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static int WriteDataPageHeader(Span<byte> destination, int valueCount, EncodingKind encoding, int uncompressedSize, int compressedSize)
    {
        var writer = new CompactSpanWriter(destination);
        var previous = writer.BeginStruct();

        writer.WriteFieldI32(1, (int)PageType.DataPage);
        writer.WriteFieldI32(2, uncompressedSize);
        writer.WriteFieldI32(3, compressedSize);
        writer.WriteFieldHeader(5, CompactType.Struct);

        var previousData = writer.BeginStruct();
        writer.WriteFieldI32(1, valueCount);
        writer.WriteFieldI32(2, GetEncoding(encoding));
        var levelEncoding = GetEncoding(EncodingKind.Rle);
        writer.WriteFieldI32(3, levelEncoding);
        writer.WriteFieldI32(4, levelEncoding);
        writer.EndStruct(previousData);

        writer.EndStruct(previous);
        return writer.WrittenCount;
    }

    internal static int GetFileMetaDataSize(ParquetSchema schema, ParquetWriter.RowGroupInfo[] rowGroups, int rowGroupCount)
    {
        var writer = new CompactSizeCounter();
        var previous = writer.BeginStruct();

        writer.WriteFieldI32(1, 1);
        WriteSchema(ref writer, schema);
        writer.WriteFieldI64(3, CountRows(rowGroups, rowGroupCount));
        WriteRowGroups(ref writer, schema, rowGroups, rowGroupCount);

        writer.EndStruct(previous);
        return writer.WrittenCount;
    }

    internal static int WriteFileMetaData(Span<byte> destination, ParquetSchema schema, ParquetWriter.RowGroupInfo[] rowGroups, int rowGroupCount)
    {
        var writer = new CompactSpanWriter(destination);
        var previous = writer.BeginStruct();

        writer.WriteFieldI32(1, 1);
        WriteSchema(ref writer, schema);
        writer.WriteFieldI64(3, CountRows(rowGroups, rowGroupCount));
        WriteRowGroups(ref writer, schema, rowGroups, rowGroupCount);

        writer.EndStruct(previous);
        return writer.WrittenCount;
    }

    static void WriteSchema(ref CompactSizeCounter writer, ParquetSchema schema)
    {
        var columns = schema.Columns.IsDefault ? ImmutableArray<Column>.Empty : schema.Columns;
        var count = columns.Length;
        writer.WriteFieldHeader(2, CompactType.List);
        writer.WriteListHeader(count + 1, CompactType.Struct);

        var previous = writer.BeginStruct();
        writer.WriteFieldString(4, "schema");
        writer.WriteFieldI32(5, count);
        writer.EndStruct(previous);

        for (var i = 0; i < count; i++)
        {
            var column = columns[i];
            previous = writer.BeginStruct();
            writer.WriteFieldI32(1, GetType(column.PhysicalType));
            writer.WriteFieldI32(3, GetRepetition(column.Options.Repetition));
            writer.WriteFieldString(4, column.Name);
            writer.EndStruct(previous);
        }
    }

    static void WriteRowGroups(ref CompactSizeCounter writer, ParquetSchema schema, ParquetWriter.RowGroupInfo[] rowGroups, int rowGroupCount)
    {
        writer.WriteFieldHeader(4, CompactType.List);
        writer.WriteListHeader(rowGroupCount, CompactType.Struct);
        var columns = schema.Columns.IsDefault ? ImmutableArray<Column>.Empty : schema.Columns;

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
            WritePath(ref writer, column.Name);
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

    static void WriteSchema(ref CompactSpanWriter writer, ParquetSchema schema)
    {
        var columns = schema.Columns.IsDefault ? ImmutableArray<Column>.Empty : schema.Columns;
        var count = columns.Length;
        writer.WriteFieldHeader(2, CompactType.List);
        writer.WriteListHeader(count + 1, CompactType.Struct);

        var previous = writer.BeginStruct();
        writer.WriteFieldString(4, "schema");
        writer.WriteFieldI32(5, count);
        writer.EndStruct(previous);

        for (var i = 0; i < count; i++)
        {
            var column = columns[i];
            previous = writer.BeginStruct();
            writer.WriteFieldI32(1, GetType(column.PhysicalType));
            writer.WriteFieldI32(3, GetRepetition(column.Options.Repetition));
            writer.WriteFieldString(4, column.Name);
            writer.EndStruct(previous);
        }
    }

    static void WriteRowGroups(ref CompactSpanWriter writer, ParquetSchema schema, ParquetWriter.RowGroupInfo[] rowGroups, int rowGroupCount)
    {
        writer.WriteFieldHeader(4, CompactType.List);
        writer.WriteListHeader(rowGroupCount, CompactType.Struct);
        var columns = schema.Columns.IsDefault ? ImmutableArray<Column>.Empty : schema.Columns;

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
            WritePath(ref writer, column.Name);
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
        for (var i = 0; i < columns.Length; i++)
            total = checked(total + columns[i].TotalUncompressedSize);
        return total;
    }

    static long GetRowGroupTotalCompressedSize(ParquetWriter.ColumnChunkMetadata[] columns)
    {
        long total = 0;
        for (var i = 0; i < columns.Length; i++)
            total = checked(total + columns[i].TotalCompressedSize);
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

    static int GetCompression(CompressionKind compression)
        => compression switch
        {
            CompressionKind.None => (int)CompressionCodec.Uncompressed,
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
        DataPage = 0
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
        Uncompressed = 0
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
        int _index;
        int _lastFieldId;

        internal CompactSpanWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _index = 0;
            _lastFieldId = 0;
        }

        internal int WrittenCount
            => _index;

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

            data.CopyTo(_buffer.Slice(_index));
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
        int _count;
        int _lastFieldId;

        internal int WrittenCount
            => _count;

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

        internal void WriteFieldHeader(int fieldId, CompactType type)
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

        internal void WriteListHeader(int count, CompactType elementType)
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
