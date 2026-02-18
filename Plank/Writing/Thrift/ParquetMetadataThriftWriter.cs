using System.Collections.Immutable;
using System.Text;
using Plank.Schema;
using TextEncoding = System.Text.Encoding;

namespace Plank.Writing;

static class ParquetMetadataThriftWriter
{
    static readonly TextEncoding _utf8 = new UTF8Encoding(false, true);

    internal static void WriteDataPageHeaderV2(ref BufferWriter destination, int rowCount, EncodingKind encoding,
        int uncompressedPageSize, int compressedPageSize, bool isCompressed)
    {
        var writer = new CompactWriter(ref destination);
        var previous = writer.BeginStruct();
        writer.WriteFieldI32(1, (int)PageType.DataPageV2);
        writer.WriteFieldI32(2, uncompressedPageSize);
        writer.WriteFieldI32(3, compressedPageSize);
        writer.WriteFieldHeader(8, CompactType.Struct);

        var previousData = writer.BeginStruct();
        writer.WriteFieldI32(1, rowCount);
        writer.WriteFieldI32(2, 0);
        writer.WriteFieldI32(3, rowCount);
        writer.WriteFieldI32(4, GetEncoding(encoding));
        writer.WriteFieldI32(5, 0);
        writer.WriteFieldI32(6, 0);
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
            WriteColumnChunk(ref writer, columns[i], chunk);
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
        ImmutableArray<Column> columns = schema.Columns.IsDefault ? [] : schema.Columns;
        writer.WriteFieldHeader(2, CompactType.List);
        writer.WriteListHeader(checked(columns.Length + 1), CompactType.Struct);

        var previousRoot = writer.BeginStruct();
        writer.WriteFieldBinary(4, "schema");
        writer.WriteFieldI32(5, columns.Length);
        writer.EndStruct(previousRoot);

        for (var i = 0; i < columns.Length; i++)
            WriteColumnSchema(ref writer, columns[i]);
    }

    static void WriteColumnSchema(ref CompactWriter writer, Column column)
    {
        var previous = writer.BeginStruct();
        writer.WriteFieldI32(1, GetPhysicalType(column.PhysicalType));
        writer.WriteFieldI32(3, GetRepetition(column.Options.Repetition));
        writer.WriteFieldBinary(4, column.Name);
        writer.EndStruct(previous);
    }

    static void WriteColumnChunk(ref CompactWriter writer, Column column, in ColumnChunkMetadata metadata)
    {
        var previousChunk = writer.BeginStruct();
        writer.WriteFieldI64(2, metadata.DataPageOffset);
        writer.WriteFieldHeader(3, CompactType.Struct);

        var previousMetadata = writer.BeginStruct();
        writer.WriteFieldI32(1, GetPhysicalType(column.PhysicalType));
        WriteEncodings(ref writer, metadata.DataEncoding, metadata.HasDictionaryPage);
        WritePath(ref writer, column.Name);
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

    static void WritePath(ref CompactWriter writer, string name)
    {
        writer.WriteFieldHeader(3, CompactType.List);
        writer.WriteListHeader(1, CompactType.Binary);
        writer.WriteBinary(name);
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
