using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Reading;

static class ColumnChunkReader
{
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static int ReadChunkBuffer(IParquetReadSource source, InternalColumnChunkMetadata columnChunk, ref byte[]? buffer,
        IParquetBufferPool bufferPool)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bufferPool);
        if (columnChunk.TotalCompressedSize > int.MaxValue)
            throw new NotSupportedException("Column chunks larger than Int32.MaxValue are not supported.");

        var length = checked((int)columnChunk.TotalCompressedSize);
        buffer = EnsureByteBuffer(ref buffer, length, bufferPool);
        source.ReadExactly(columnChunk.ChunkOffset, buffer.AsSpan(0, length));
        return length;
    }

    internal static bool TryReadNextDataPage<T>(byte[] buffer, int bufferLength, ref int offset, Column column,
        InternalColumnChunkMetadata columnChunk, ref Array? dictionary, ref T[]? dictionaryBuffer, ref T[]? valuesBuffer,
        out ReadOnlyMemory<T> values, out EncodingKind encoding)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(column);

        if (column.Options.Repetition == ParquetRepetition.Repeated)
            throw new NotSupportedException($"Repeated readback is not implemented yet for column '{column.Name}'.");

        while (offset < bufferLength)
        {
            var header = PageHeaderReader.Read(buffer.AsSpan(offset, bufferLength - offset));
            offset += header.HeaderLength;

            if (header.CompressedPageSize > (uint)(bufferLength - offset))
                throw new CorruptParquetException(
                    $"Page compressed size ({header.CompressedPageSize}) exceeds remaining column chunk buffer ({bufferLength - offset}).");

            var compressedPageSize = checked((int)header.CompressedPageSize);
            var payload = buffer.AsSpan(offset, compressedPageSize);
            offset += compressedPageSize;

            switch (header.Type)
            {
                case PageHeaderType.DictionaryPage:
                    dictionary = DecodeDictionaryPage(payload, column, header, columnChunk.Compression,
                        ref dictionaryBuffer, GetPhysicalDecodeType<T>());
                    break;
                case PageHeaderType.DataPageV2:
                {
                    var repLen = header.RepetitionLevelsByteLength;
                    var defLen = header.DefinitionLevelsByteLength;
                    var levelBytes = repLen + defLen;
                    if (header.NullCount > header.ValueCount)
                        throw new CorruptParquetException(
                            $"Page null count ({header.NullCount}) exceeds value count ({header.ValueCount}).");
                    var totalValueCount = header.ValueCount;
                    var physicalValueCount = header.ValueCount - header.NullCount;

                    // In DataPageV2, levels are always uncompressed; only the values portion may be compressed.
                    if (levelBytes > (uint)payload.Length)
                        throw new CorruptParquetException(
                            $"Level bytes ({levelBytes}) exceed compressed page size ({payload.Length}).");
                    var definitionPayload = defLen > 0 ? payload.Slice((int)repLen, (int)defLen) : default;
                    var dataPayload = payload[(int)levelBytes..];

                    ReadOnlySpan<byte> effectiveData;
                    if (header.IsCompressed && dataPayload.Length > 0)
                    {
                        if (levelBytes > header.UncompressedPageSize)
                            throw new CorruptParquetException(
                                $"Level bytes ({levelBytes}) exceed uncompressed page size ({header.UncompressedPageSize}).");
                        var expectedUncompressedDataSize = header.UncompressedPageSize - levelBytes;
                        effectiveData = ParquetDecompressor.Decompress(dataPayload, expectedUncompressedDataSize, columnChunk.Compression);
                    }
                    else
                    {
                        effectiveData = dataPayload;
                    }

                    var physicalDecodeType = GetPhysicalDecodeType<T>();
                    // Nullable value types (int?, long?, …) always need expansion since the physical type differs.
                    // Reference types (string, byte[]) need expansion when there are actual nulls.
                    var isNullableValueType = physicalDecodeType != typeof(T);
                    var needsNullExpansion = isNullableValueType
                        || (!typeof(T).IsValueType && header.NullCount > 0 && defLen > 0);

                    if (dictionary is null)
                    {
                        if (needsNullExpansion)
                        {
                            values = DecodeValuesWithNullExpansion<T>(effectiveData, definitionPayload, column,
                                totalValueCount, physicalValueCount, header.Encoding, header.NullCount > 0);
                        }
                        else if (header.NullCount > 0 && defLen > 0)
                        {
                            // Non-nullable value type with actual nulls — decode physical values only.
                            values = (T[])DecodeValues(effectiveData, column, physicalValueCount, header.Encoding, typeof(T));
                        }
                        else if (TryDecodeValuesIntoBuffer(effectiveData, column, physicalValueCount, header.Encoding,
                                     ref valuesBuffer, out values))
                        {
                            encoding = header.Encoding;
                            return true;
                        }
                        else
                        {
                            values = (T[])DecodeValues(effectiveData, column, physicalValueCount, header.Encoding, typeof(T));
                        }
                    }
                    else
                    {
                        if (header.NullCount > 0 && defLen > 0)
                        {
                            values = DecodeDictionaryIndexesWithNulls<T>(effectiveData, totalValueCount, physicalValueCount,
                                dictionary, definitionPayload);
                        }
                        else
                        {
                            DecodeDictionaryIndexes(effectiveData, physicalValueCount, dictionary, ref valuesBuffer, out values);
                        }
                    }

                    encoding = header.Encoding;
                    return true;
                }
                default:
                    throw new NotSupportedException($"Page type '{header.Type}' is not supported.");
            }
        }

        values = ReadOnlyMemory<T>.Empty;
        encoding = default;
        return false;
    }

    static bool TryDecodeValuesIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        EncodingKind encoding, ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                return TryDecodePlainIntoBuffer(payload, column, valueCount, ref valuesBuffer, out values);
            case EncodingKind.Rle:
                return TryDecodeBooleanRleIntoBuffer(payload, valueCount, ref valuesBuffer, out values);
            case EncodingKind.ByteStreamSplit:
                return TryDecodeByteStreamSplitIntoBuffer(payload, column, valueCount, ref valuesBuffer, out values);
            case EncodingKind.DeltaBinaryPacked:
                return TryDecodeDeltaBinaryPackedIntoBuffer(payload, column, valueCount, ref valuesBuffer, out values);
            default:
                values = default;
                return false;
        }
    }

    static void ValidatePlainPayload(ReadOnlySpan<byte> payload, uint valueCount, uint elementSize)
    {
        if (valueCount > (uint)payload.Length / elementSize)
            throw new CorruptParquetException(
                $"Payload ({payload.Length} bytes) is too short to decode {valueCount} plain values of {elementSize} bytes each.");
    }

    static bool TryDecodePlainIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount, ref T[]? valuesBuffer,
        out ReadOnlyMemory<T> values)
    {
        if (typeof(T) == typeof(bool) && column.PhysicalType == ParquetPhysicalType.Boolean)
        {
            if ((uint)payload.Length < (valueCount + 7u) / 8u)
                throw new CorruptParquetException(
                    $"Payload ({payload.Length} bytes) is too short to decode {valueCount} plain boolean values.");
            var typed = (bool[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
                typed[i] = ((payload[i >> 3] >> (i & 7)) & 1) != 0;
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(int) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = (int[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianInt32(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(byte) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = (byte[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
                typed[i] = unchecked((byte)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(ushort) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = (ushort[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
                typed[i] = unchecked((ushort)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(uint) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(uint));
            var typed = (uint[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianUInt32(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(long) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            var typed = (long[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianInt64(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(ulong) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(ulong));
            var typed = (ulong[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianUInt64(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(float) && column.PhysicalType == ParquetPhysicalType.Float)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(float));
            var typed = (float[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianFloat(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(double) && column.PhysicalType == ParquetPhysicalType.Double)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(double));
            var typed = (double[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianDouble(payload, typed.AsSpan(0, (int)valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(DateOnly) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(int));
            var typed = (DateOnly[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var days = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));
                typed[i] = DateOnly.FromDayNumber(days);
            }
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(DateTimeOffset) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            var typed = (DateTimeOffset[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
                typed[i] = column.LogicalType switch
                {
                    LogicalType.Timestamp { Unit: TimeUnit.Millis } => DateTimeOffset.FromUnixTimeMilliseconds(raw),
                    LogicalType.Timestamp { Unit: TimeUnit.Micros } => DateTimeOffset.UnixEpoch.AddTicks(raw * 10),
                    _ => throw new InvalidOperationException("DateTimeOffset projection requires a timestamp logical type.")
                };
            }
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        if (typeof(T) == typeof(DateTime) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            ValidatePlainPayload(payload, valueCount, sizeof(long));
            var typed = (DateTime[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
                typed[i] = column.LogicalType switch
                {
                    LogicalType.Timestamp { Unit: TimeUnit.Millis } => DateTimeOffset.FromUnixTimeMilliseconds(raw).UtcDateTime,
                    LogicalType.Timestamp { Unit: TimeUnit.Micros } => DateTimeOffset.UnixEpoch.AddTicks(raw * 10).UtcDateTime,
                    _ => throw new InvalidOperationException("DateTime projection requires a timestamp logical type.")
                };
            }
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
            return true;
        }

        values = default;
        return false;
    }

    static bool TryDecodeBooleanRleIntoBuffer<T>(ReadOnlySpan<byte> payload, uint valueCount, ref T[]? valuesBuffer,
        out ReadOnlyMemory<T> values)
    {
        if (typeof(T) != typeof(bool))
        {
            values = default;
            return false;
        }

        var typed = (bool[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
        DecodeBooleanRle(payload, typed.AsSpan(0, (int)valueCount));
        values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
        return true;
    }

    static bool TryDecodeByteStreamSplitIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(int):
            {
                var typed = (int[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitInt32(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(byte):
            {
                var typed = (byte[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                for (var i = 0; i < valueCount; i++)
                    typed[i] = (byte)(payload[i] | (payload[(int)valueCount + i] << 8) | (payload[((int)valueCount * 2) + i] << 16) |
                        (payload[((int)valueCount * 3) + i] << 24));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(ushort):
            {
                var typed = (ushort[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                for (var i = 0; i < valueCount; i++)
                    typed[i] = unchecked((ushort)(payload[i] | (payload[(int)valueCount + i] << 8) |
                        (payload[((int)valueCount * 2) + i] << 16) | (payload[((int)valueCount * 3) + i] << 24)));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(uint):
            {
                var typed = (uint[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                for (var i = 0; i < valueCount; i++)
                    typed[i] = unchecked((uint)(payload[i] | (payload[(int)valueCount + i] << 8) |
                        (payload[((int)valueCount * 2) + i] << 16) | (payload[((int)valueCount * 3) + i] << 24)));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(long):
            {
                var typed = (long[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitInt64(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(ulong):
            {
                var typed = (ulong[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitUInt64(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Float when typeof(T) == typeof(float):
            {
                var typed = (float[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitFloat(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Double when typeof(T) == typeof(double):
            {
                var typed = (double[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitDouble(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            default:
                values = default;
                return false;
        }
    }

    static bool TryDecodeDeltaBinaryPackedIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, uint valueCount,
        ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(int):
            {
                var typed = (int[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DeltaBinaryPackedDecoder.ReadInt32(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(long):
            {
                var typed = (long[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DeltaBinaryPackedDecoder.ReadInt64(payload, typed.AsSpan(0, (int)valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, (int)valueCount);
                return true;
            }
            default:
                values = default;
                return false;
        }
    }

    static byte[] EnsureByteBuffer(ref byte[]? buffer, int minimumLength, IParquetBufferPool bufferPool)
    {
        if (buffer is not null && buffer.Length >= minimumLength)
            return buffer;

        if (buffer is not null)
            bufferPool.Return(buffer);

        buffer = bufferPool.Rent(checked((uint)minimumLength));
        return buffer;
    }

    static void CopyLittleEndianInt32(ReadOnlySpan<byte> source, Span<int> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(int))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * sizeof(int), sizeof(int)));
    }

    static void CopyLittleEndianUInt32(ReadOnlySpan<byte> source, Span<uint> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(uint))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(i * sizeof(uint), sizeof(uint)));
    }

    static void CopyLittleEndianInt64(ReadOnlySpan<byte> source, Span<long> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(long))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(i * sizeof(long), sizeof(long)));
    }

    static void CopyLittleEndianUInt64(ReadOnlySpan<byte> source, Span<ulong> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(ulong))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(i * sizeof(ulong), sizeof(ulong)));
    }

    static void CopyLittleEndianFloat(ReadOnlySpan<byte> source, Span<float> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(float))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * sizeof(float), sizeof(float)));
            destination[i] = BitConverter.Int32BitsToSingle(bits);
        }
    }

    static void CopyLittleEndianDouble(ReadOnlySpan<byte> source, Span<double> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            source[..checked(destination.Length * sizeof(double))].CopyTo(MemoryMarshal.AsBytes(destination));
            return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var bits = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(i * sizeof(double), sizeof(double)));
            destination[i] = BitConverter.Int64BitsToDouble(bits);
        }
    }

    static void DecodeByteStreamSplitInt32(ReadOnlySpan<byte> payload, Span<int> destination)
    {
        var count = destination.Length;
        if ((long)count * 4 > payload.Length)
            throw new CorruptParquetException(
                $"ByteStreamSplit payload ({payload.Length} bytes) is too short for {count} Int32 values.");
        var lane1 = count;
        var lane2 = count * 2;
        var lane3 = count * 3;
        for (var i = 0; i < count; i++)
            destination[i] = payload[i] | (payload[lane1 + i] << 8) | (payload[lane2 + i] << 16) |
                (payload[lane3 + i] << 24);
    }

    static void DecodeByteStreamSplitInt64(ReadOnlySpan<byte> payload, Span<long> destination)
    {
        var uintDestination = MemoryMarshal.Cast<long, ulong>(destination);
        DecodeByteStreamSplitUInt64(payload, uintDestination);
    }

    static void DecodeByteStreamSplitUInt64(ReadOnlySpan<byte> payload, Span<ulong> destination)
    {
        var count = destination.Length;
        if ((long)count * 8 > payload.Length)
            throw new CorruptParquetException(
                $"ByteStreamSplit payload ({payload.Length} bytes) is too short for {count} 8-byte values.");
        var lane1 = count;
        var lane2 = count * 2;
        var lane3 = count * 3;
        var lane4 = count * 4;
        var lane5 = count * 5;
        var lane6 = count * 6;
        var lane7 = count * 7;
        for (var i = 0; i < count; i++)
            destination[i] =
                (ulong)payload[i] |
                ((ulong)payload[lane1 + i] << 8) |
                ((ulong)payload[lane2 + i] << 16) |
                ((ulong)payload[lane3 + i] << 24) |
                ((ulong)payload[lane4 + i] << 32) |
                ((ulong)payload[lane5 + i] << 40) |
                ((ulong)payload[lane6 + i] << 48) |
                ((ulong)payload[lane7 + i] << 56);
    }

    static void DecodeByteStreamSplitFloat(ReadOnlySpan<byte> payload, Span<float> destination)
    {
        var intDestination = MemoryMarshal.Cast<float, int>(destination);
        DecodeByteStreamSplitInt32(payload, intDestination);
    }

    static void DecodeByteStreamSplitDouble(ReadOnlySpan<byte> payload, Span<double> destination)
    {
        var longDestination = MemoryMarshal.Cast<double, ulong>(destination);
        DecodeByteStreamSplitUInt64(payload, longDestination);
    }

    static Array DecodeDictionaryPage<T>(ReadOnlySpan<byte> payload, Column column, PageHeader header,
        CompressionKind compression, ref T[]? dictionaryBuffer, Type physicalDecodeType)
    {
        var effectivePayload = compression == CompressionKind.None || header.CompressedPageSize == 0
            ? payload
            : ParquetDecompressor.Decompress(payload, header.UncompressedPageSize, compression);

        if (physicalDecodeType == typeof(T) &&
            TryDecodeValuesIntoBuffer(effectivePayload, column, header.ValueCount, header.Encoding,
                ref dictionaryBuffer, out _))
            return dictionaryBuffer!;

        return DecodeValues(effectivePayload, column, header.ValueCount, header.Encoding, physicalDecodeType);
    }

    static T[] EnsureValueBuffer<T>(ref T[]? buffer, uint minimumLength)
    {
        if (buffer is not null && (uint)buffer.Length >= minimumLength)
            return buffer;

        if (buffer is not null)
            ArrayRenter<T>.Shared.Return(buffer, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());

        buffer = ArrayRenter<T>.Shared.Rent(minimumLength);
        return buffer;
    }

    static void DecodeDictionaryIndexes<T>(ReadOnlySpan<byte> payload, uint valueCount, Array dictionary,
        ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        var result = EnsureValueBuffer(ref valuesBuffer, valueCount);
        if (dictionary is T[] typedDictionary)
        {
            DecodeDictionaryIndexesIntoBuffer(payload, valueCount, typedDictionary, result);
        }
        else
        {
            var indexes = ReadRleBitPackedHybrid(payload, valueCount, hasBitWidthPrefix: true);
            for (var i = 0; i < indexes.Length; i++)
                result[i] = (T)dictionary.GetValue(indexes[i])!;
        }

        values = new ReadOnlyMemory<T>(result, 0, (int)valueCount);
    }

    static T[] DecodeDictionaryIndexesWithNulls<T>(ReadOnlySpan<byte> dataPayload, uint totalValueCount,
        uint physicalValueCount, Array dictionary, ReadOnlySpan<byte> definitionPayload)
    {
        var indexes = ReadRleBitPackedHybrid(dataPayload, physicalValueCount, hasBitWidthPrefix: true);
        var definitionLevels = ReadRleBitPackedHybrid(definitionPayload, totalValueCount, bitWidth: 1);
        var result = new T[(int)totalValueCount];
        var valueIndex = 0;
        for (var i = 0; i < totalValueCount; i++)
        {
            if (definitionLevels[i] != 0)
                result[i] = (T)dictionary.GetValue(indexes[valueIndex++])!;
        }
        return result;
    }

    static void DecodeDictionaryIndexesIntoBuffer<T>(ReadOnlySpan<byte> payload, uint valueCount, T[] dictionary,
        T[] destination)
    {
        if (valueCount == 0)
            return;

        if (payload.IsEmpty)
            throw new CorruptParquetException("Dictionary payload is empty but value count is non-zero.");
        var bitWidth = payload[0];
        if (bitWidth > 32)
            throw new CorruptParquetException($"Dictionary bit width {bitWidth} exceeds the maximum of 32.");
        payload = payload[1..];
        var valueIndex = 0U;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                var byteWidth = (bitWidth + 7) >> 3;
                var dictionaryIndex = byteWidth == 0 ? 0 : ReadLittleEndian(ref payload, byteWidth);
                if ((uint)dictionaryIndex >= (uint)dictionary.Length)
                    throw new CorruptParquetException(
                        $"Dictionary index {dictionaryIndex} is out of range for a dictionary of {dictionary.Length} entries.");
                var repeated = dictionary[dictionaryIndex];
                var copyLength = Math.Min(runLength, valueCount - valueIndex);
                destination.AsSpan((int)valueIndex, (int)copyLength).Fill(repeated);
                valueIndex += copyLength;
                continue;
            }

            var literalCount = (header >> 1) * 8U;
            var literalByteCount = ((literalCount * bitWidth) + 7) >> 3;
            if (literalByteCount > (uint)payload.Length)
                throw new CorruptParquetException(
                    $"Literal run claims {literalByteCount} bytes but only {payload.Length} remain.");
            var literalPayload = payload[..(int)literalByteCount];
            var literalCopyLength = Math.Min(literalCount, valueCount - valueIndex);
            DecodeDictionaryLiteralIndexes(literalPayload, bitWidth, dictionary, destination.AsSpan((int)valueIndex, (int)literalCopyLength));
            valueIndex += literalCopyLength;
            payload = payload[(int)literalByteCount..];
        }
    }

    static void DecodeDictionaryLiteralIndexes<T>(ReadOnlySpan<byte> payload, int bitWidth, T[] dictionary,
        Span<T> destination)
    {
        if (bitWidth == 0)
        {
            if (dictionary.Length == 0)
                throw new CorruptParquetException("Dictionary is empty but values reference index 0.");
            destination.Fill(dictionary[0]);
            return;
        }

        var mask = bitWidth == 32 ? ulong.MaxValue : (1UL << bitWidth) - 1UL;
        ulong bitBuffer = 0;
        var bufferedBits = 0;
        var byteIndex = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            while (bufferedBits < bitWidth)
            {
                bitBuffer |= (ulong)payload[byteIndex++] << bufferedBits;
                bufferedBits += 8;
            }

            var dictionaryIndex = (int)(bitBuffer & mask);
            bitBuffer >>= bitWidth;
            bufferedBits -= bitWidth;
            if ((uint)dictionaryIndex >= (uint)dictionary.Length)
                throw new CorruptParquetException(
                    $"Dictionary index {dictionaryIndex} is out of range for a dictionary of {dictionary.Length} entries.");
            destination[i] = dictionary[dictionaryIndex];
        }
    }

    static Array DecodeValues(ReadOnlySpan<byte> payload, Column column, PageHeader header, CompressionKind compression,
        Type targetType)
    {
        var bytes = compression == CompressionKind.None || header.CompressedPageSize == 0
            ? payload.ToArray()
            : ParquetDecompressor.Decompress(payload, header.UncompressedPageSize, compression);
        return DecodeValues(bytes, column, header.ValueCount, header.Encoding, targetType);
    }

    static Array DecodeValues(ReadOnlySpan<byte> payload, Column column, uint valueCount, EncodingKind encoding,
        Type targetType)
    {
        switch (encoding)
        {
            case EncodingKind.Plain:
                return DecodePlain(payload, column, valueCount, targetType);
            case EncodingKind.Rle:
                return DecodeBooleanRle(payload, valueCount, targetType);
            case EncodingKind.ByteStreamSplit:
                return DecodeByteStreamSplit(payload, column, valueCount, targetType);
            case EncodingKind.DeltaBinaryPacked:
                return DecodeDeltaBinaryPacked(payload, column, targetType);
            case EncodingKind.DeltaLengthByteArray:
                return DecodeDeltaLengthByteArray(payload, targetType);
            case EncodingKind.DeltaByteArray:
                return DecodeDeltaByteArray(payload, targetType);
            default:
                throw new NotSupportedException($"Encoding '{encoding}' is not supported.");
        }
    }

    static Array DecodePlain(ReadOnlySpan<byte> payload, Column column, uint valueCount, Type targetType)
        => column.PhysicalType switch
        {
            ParquetPhysicalType.Boolean => DecodePlainBoolean(payload, valueCount, targetType),
            ParquetPhysicalType.Int32 => DecodePlainInt32(payload, valueCount, column.LogicalType, targetType),
            ParquetPhysicalType.Int64 => DecodePlainInt64(payload, valueCount, column.LogicalType, targetType),
            ParquetPhysicalType.Float => DecodePlainFloat(payload, valueCount, targetType),
            ParquetPhysicalType.Double => DecodePlainDouble(payload, valueCount, targetType),
            ParquetPhysicalType.ByteArray => DecodePlainByteArray(payload, valueCount, targetType),
            ParquetPhysicalType.FixedLenByteArray => DecodeFixedLengthByteArray(payload, valueCount,
                checked((int)column.Options.TypeLength), targetType),
            ParquetPhysicalType.Int96 => DecodeFixedLengthByteArray(payload, valueCount, 12, targetType),
            _ => throw new NotSupportedException($"Physical type '{column.PhysicalType}' is not supported.")
        };

    static Array DecodePlainBoolean(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        if (targetType != typeof(bool))
            throw new InvalidOperationException($"Boolean column cannot be projected to '{targetType}'.");

        if (valueCount > (uint)payload.Length * 8)
            throw new CorruptParquetException(
                $"Payload ({payload.Length} bytes) is too short to decode {valueCount} plain boolean values.");

        var values = new bool[(int)valueCount];
        for (var i = 0; i < valueCount; i++)
            values[i] = ((payload[i >> 3] >> (i & 7)) & 1) != 0;
        return values;
    }

    static Array DecodePlainInt32(ReadOnlySpan<byte> payload, uint valueCount, LogicalType? logicalType, Type targetType)
    {
        if (targetType == typeof(int))
        {
            var values = new int[(int)valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));
            return values;
        }

        if (targetType == typeof(byte))
        {
            var values = new byte[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = unchecked((byte)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            return values;
        }

        if (targetType == typeof(ushort))
        {
            var values = new ushort[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = unchecked((ushort)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            return values;
        }

        if (targetType == typeof(uint))
        {
            var values = new uint[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = unchecked((uint)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            return values;
        }

        if (targetType == typeof(DateOnly) || logicalType is LogicalType.Date)
        {
            var values = new DateOnly[valueCount];
            for (var i = 0; i < valueCount; i++)
            {
                var days = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));
                values[i] = DateOnly.FromDayNumber(days);
            }
            return values;
        }

        throw new InvalidOperationException($"Int32 column cannot be projected to '{targetType}'.");
    }

    static Array DecodePlainInt64(ReadOnlySpan<byte> payload, uint valueCount, LogicalType? logicalType, Type targetType)
    {
        if (targetType == typeof(long))
        {
            var values = new long[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
            return values;
        }

        if (targetType == typeof(ulong))
        {
            var values = new ulong[valueCount];
            for (var i = 0; i < valueCount; i++)
                values[i] = unchecked((ulong)BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8)));
            return values;
        }

        if (targetType == typeof(DateTimeOffset))
        {
            var values = new DateTimeOffset[valueCount];
            for (var i = 0; i < valueCount; i++)
            {
                var raw = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
                values[i] = logicalType switch
                {
                    LogicalType.Timestamp { Unit: TimeUnit.Millis } => DateTimeOffset.FromUnixTimeMilliseconds(raw),
                    LogicalType.Timestamp { Unit: TimeUnit.Micros } => DateTimeOffset.UnixEpoch.AddTicks(raw * 10),
                    _ => throw new InvalidOperationException("DateTimeOffset projection requires a timestamp logical type.")
                };
            }
            return values;
        }

        if (targetType == typeof(DateTime))
        {
            var offsets = (DateTimeOffset[])DecodePlainInt64(payload, valueCount, logicalType, typeof(DateTimeOffset));
            var values = new DateTime[offsets.Length];
            for (var i = 0; i < offsets.Length; i++)
                values[i] = offsets[i].UtcDateTime;
            return values;
        }

        throw new InvalidOperationException($"Int64 column cannot be projected to '{targetType}'.");
    }

    static Array DecodePlainFloat(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        if (targetType != typeof(float))
            throw new InvalidOperationException($"Float column cannot be projected to '{targetType}'.");

        var values = new float[valueCount];
        for (var i = 0; i < valueCount; i++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));
            values[i] = BitConverter.Int32BitsToSingle(bits);
        }
        return values;
    }

    static Array DecodePlainDouble(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        if (targetType != typeof(double))
            throw new InvalidOperationException($"Double column cannot be projected to '{targetType}'.");

        var values = new double[valueCount];
        for (var i = 0; i < valueCount; i++)
        {
            var bits = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(i * 8, 8));
            values[i] = BitConverter.Int64BitsToDouble(bits);
        }
        return values;
    }

    static Array DecodePlainByteArray(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        if (targetType == typeof(byte[]))
        {
            var values = new byte[valueCount][];
            var remaining = payload;
            for (var i = 0; i < valueCount; i++)
            {
                if (remaining.Length < 4)
                    throw new CorruptParquetException("Payload too short to read byte array length prefix.");
                var length = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
                remaining = remaining[4..];
                if (length > (uint)remaining.Length)
                    throw new CorruptParquetException(
                        $"Byte array length {length} exceeds remaining payload ({remaining.Length} bytes).");
                values[i] = remaining[..checked((int)length)].ToArray();
                remaining = remaining[checked((int)length)..];
            }
            return values;
        }

        if (targetType == typeof(string))
        {
            var binary = (byte[][])DecodePlainByteArray(payload, valueCount, typeof(byte[]));
            var values = new string[binary.Length];
            for (var i = 0; i < binary.Length; i++)
                values[i] = Utf8.GetString(binary[i]);
            return values;
        }

        throw new InvalidOperationException($"Byte-array column cannot be projected to '{targetType}'.");
    }

    static Array DecodeFixedLengthByteArray(ReadOnlySpan<byte> payload, uint valueCount, int valueLength, Type targetType)
    {
        if (targetType != typeof(byte[]))
            throw new InvalidOperationException($"Fixed-length binary column cannot be projected to '{targetType}'.");

        var values = new byte[valueCount][];
        var offset = 0;
        for (var i = 0; i < valueCount; i++)
        {
            values[i] = payload.Slice(offset, valueLength).ToArray();
            offset += valueLength;
        }
        return values;
    }

    static Array DecodeBooleanRle(ReadOnlySpan<byte> payload, uint valueCount, Type targetType)
    {
        if (targetType != typeof(bool))
            throw new InvalidOperationException($"Boolean column cannot be projected to '{targetType}'.");

        var ints = ReadRleBitPackedHybrid(payload, valueCount, bitWidth: 1);
        var values = new bool[ints.Length];
        for (var i = 0; i < ints.Length; i++)
            values[i] = ints[i] != 0;
        return values;
    }

    static Array DecodeByteStreamSplit(ReadOnlySpan<byte> payload, Column column, uint valueCount, Type targetType)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32:
            {
                if (targetType == typeof(int))
                {
                    var values = new int[(int)valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = payload[i] | (payload[(int)valueCount + i] << 8) | (payload[((int)valueCount * 2) + i] << 16) |
                            (payload[((int)valueCount * 3) + i] << 24);
                    return values;
                }
                if (targetType == typeof(byte))
                {
                    var values = new byte[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = (byte)(payload[i] | (payload[(int)valueCount + i] << 8) | (payload[((int)valueCount * 2) + i] << 16) |
                            (payload[((int)valueCount * 3) + i] << 24));
                    return values;
                }
                if (targetType == typeof(ushort))
                {
                    var values = new ushort[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = unchecked((ushort)(payload[i] | (payload[(int)valueCount + i] << 8) |
                            (payload[((int)valueCount * 2) + i] << 16) | (payload[((int)valueCount * 3) + i] << 24)));
                    return values;
                }
                if (targetType == typeof(uint))
                {
                    var values = new uint[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = unchecked((uint)(payload[i] | (payload[(int)valueCount + i] << 8) |
                            (payload[((int)valueCount * 2) + i] << 16) | (payload[((int)valueCount * 3) + i] << 24)));
                    return values;
                }
                throw new InvalidOperationException($"Int32 column cannot be projected to '{targetType}'.");
            }
            case ParquetPhysicalType.Int64:
            {
                if (targetType == typeof(long))
                {
                    var values = new long[valueCount];
                    for (var i = 0; i < valueCount; i++)
                    {
                        ulong value = 0;
                        for (var lane = 0; lane < 8; lane++)
                            value |= (ulong)payload[((int)valueCount * lane) + i] << (lane * 8);
                        values[i] = unchecked((long)value);
                    }
                    return values;
                }
                if (targetType == typeof(ulong))
                {
                    var values = new ulong[valueCount];
                    for (var i = 0; i < valueCount; i++)
                    {
                        ulong value = 0;
                        for (var lane = 0; lane < 8; lane++)
                            value |= (ulong)payload[((int)valueCount * lane) + i] << (lane * 8);
                        values[i] = value;
                    }
                    return values;
                }
                throw new InvalidOperationException($"Int64 column cannot be projected to '{targetType}'.");
            }
            case ParquetPhysicalType.Float:
            {
                var intValues = (int[])DecodeByteStreamSplit(payload, new Column(column.Name, ParquetPhysicalType.Int32),
                    valueCount, typeof(int));
                if (targetType != typeof(float))
                    throw new InvalidOperationException($"Float column cannot be projected to '{targetType}'.");
                var values = new float[intValues.Length];
                for (var i = 0; i < intValues.Length; i++)
                    values[i] = BitConverter.Int32BitsToSingle(intValues[i]);
                return values;
            }
            case ParquetPhysicalType.Double:
            {
                var longValues = (long[])DecodeByteStreamSplit(payload, new Column(column.Name, ParquetPhysicalType.Int64),
                    valueCount, typeof(long));
                if (targetType != typeof(double))
                    throw new InvalidOperationException($"Double column cannot be projected to '{targetType}'.");
                var values = new double[longValues.Length];
                for (var i = 0; i < longValues.Length; i++)
                    values[i] = BitConverter.Int64BitsToDouble(longValues[i]);
                return values;
            }
            default:
                throw new NotSupportedException(
                    $"Byte-stream-split decoding is not supported for physical type '{column.PhysicalType}'.");
        }
    }

    static Array DecodeDeltaBinaryPacked(ReadOnlySpan<byte> payload, Column column, Type targetType)
    {
        if (column.PhysicalType == ParquetPhysicalType.Int32)
        {
            var values = DeltaBinaryPackedDecoder.ReadInt32(payload);
            if (targetType == typeof(int))
                return values;
            if (targetType == typeof(byte))
            {
                var projected = new byte[values.Length];
                for (var i = 0; i < values.Length; i++)
                    projected[i] = unchecked((byte)values[i]);
                return projected;
            }
            if (targetType == typeof(ushort))
            {
                var projected = new ushort[values.Length];
                for (var i = 0; i < values.Length; i++)
                    projected[i] = unchecked((ushort)values[i]);
                return projected;
            }
            if (targetType == typeof(uint))
            {
                var projected = new uint[values.Length];
                for (var i = 0; i < values.Length; i++)
                    projected[i] = unchecked((uint)values[i]);
                return projected;
            }
            throw new InvalidOperationException($"Int32 column cannot be projected to '{targetType}'.");
        }

        if (column.PhysicalType == ParquetPhysicalType.Int64)
        {
            var values = DeltaBinaryPackedDecoder.ReadInt64(payload);
            if (targetType == typeof(long))
                return values;
            if (targetType == typeof(ulong))
            {
                var projected = new ulong[values.Length];
                for (var i = 0; i < values.Length; i++)
                    projected[i] = unchecked((ulong)values[i]);
                return projected;
            }
            throw new InvalidOperationException($"Int64 column cannot be projected to '{targetType}'.");
        }

        throw new NotSupportedException(
            $"Delta binary packed decoding is not supported for physical type '{column.PhysicalType}'.");
    }

    static Array DecodeDeltaLengthByteArray(ReadOnlySpan<byte> payload, Type targetType)
    {
        var (lengths, consumedLengthBytes) = DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(payload);
        var remaining = payload[consumedLengthBytes..];
        var values = new byte[lengths.Length][];
        for (var i = 0; i < lengths.Length; i++)
        {
            var length = lengths[i];
            if (length > (uint)remaining.Length)
                throw new CorruptParquetException(
                    $"Delta length byte array entry {i} claims {length} bytes but only {remaining.Length} remain.");
            values[i] = remaining[..(int)length].ToArray();
            remaining = remaining[(int)length..];
        }
        return targetType == typeof(byte[]) ? values : DecodePlainByteArray(ToLengthPrefixed(values), (uint)values.Length, targetType);
    }

    static Array DecodeDeltaByteArray(ReadOnlySpan<byte> payload, Type targetType)
    {
        var (prefixLengths, prefixConsumed) = DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(payload);
        var suffixPayload = payload[prefixConsumed..];
        var (suffixLengths, suffixConsumed) = DeltaBinaryPackedDecoder.ReadUInt32WithConsumedBytes(suffixPayload);
        var suffixBytes = suffixPayload[suffixConsumed..];

        if (suffixLengths.Length != prefixLengths.Length)
            throw new CorruptParquetException(
                $"Delta byte array prefix count {prefixLengths.Length} does not match suffix count {suffixLengths.Length}.");

        var values = new byte[prefixLengths.Length][];
        var suffixRemaining = suffixBytes;
        for (var i = 0; i < values.Length; i++)
        {
            var prefixLength = prefixLengths[i];
            var suffixLength = suffixLengths[i];
            var totalLength = prefixLength + suffixLength;
            if (totalLength < prefixLength)
                throw new CorruptParquetException(
                    $"Delta byte array value length overflow (prefix={prefixLength} + suffix={suffixLength}).");
            var value = new byte[(int)totalLength];
            if (prefixLength > 0 && i > 0)
            {
                if (prefixLength > (uint)values[i - 1].Length)
                    throw new CorruptParquetException(
                        $"Delta byte array prefix length {prefixLength} exceeds previous value length {values[i - 1].Length}.");
                values[i - 1].AsSpan(0, (int)prefixLength).CopyTo(value);
            }
            if (suffixLength > 0)
            {
                if (suffixLength > (uint)suffixRemaining.Length)
                    throw new CorruptParquetException(
                        $"Delta byte array suffix length {suffixLength} exceeds remaining suffix bytes ({suffixRemaining.Length}).");
                suffixRemaining[..(int)suffixLength].CopyTo(value.AsSpan((int)prefixLength));
                suffixRemaining = suffixRemaining[(int)suffixLength..];
            }
            values[i] = value;
        }

        return targetType == typeof(byte[]) ? values : DecodePlainByteArray(ToLengthPrefixed(values), (uint)values.Length, targetType);
    }

    static byte[] ToLengthPrefixed(byte[][] values)
    {
        var totalLength = 0;
        for (var i = 0; i < values.Length; i++)
            totalLength = checked(totalLength + 4 + values[i].Length);

        var buffer = new byte[totalLength];
        var offset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), values[i].Length);
            offset += 4;
            values[i].CopyTo(buffer.AsSpan(offset));
            offset += values[i].Length;
        }
        return buffer;
    }

    static Type GetPhysicalDecodeType<T>()
    {
        if (typeof(T) == typeof(int?)) return typeof(int);
        if (typeof(T) == typeof(long?)) return typeof(long);
        if (typeof(T) == typeof(bool?)) return typeof(bool);
        if (typeof(T) == typeof(float?)) return typeof(float);
        if (typeof(T) == typeof(double?)) return typeof(double);
        if (typeof(T) == typeof(byte?)) return typeof(byte);
        if (typeof(T) == typeof(ushort?)) return typeof(ushort);
        if (typeof(T) == typeof(uint?)) return typeof(uint);
        if (typeof(T) == typeof(ulong?)) return typeof(ulong);
        if (typeof(T) == typeof(DateOnly?)) return typeof(DateOnly);
        if (typeof(T) == typeof(DateTime?)) return typeof(DateTime);
        if (typeof(T) == typeof(DateTimeOffset?)) return typeof(DateTimeOffset);
        if (typeof(T) == typeof(TimeOnly?)) return typeof(TimeOnly);
        if (typeof(T) == typeof(ReadOnlyMemory<byte>?)) return typeof(ReadOnlyMemory<byte>);
        return typeof(T);
    }

    static ReadOnlyMemory<T> DecodeValuesWithNullExpansion<T>(ReadOnlySpan<byte> dataPayload,
        ReadOnlySpan<byte> definitionPayload, Column column, uint totalValueCount, uint physicalValueCount,
        EncodingKind encoding, bool hasNulls)
    {
        var physicalDecodeType = GetPhysicalDecodeType<T>();
        var physicalValues = physicalValueCount > 0
            ? DecodeValues(dataPayload, column, physicalValueCount, encoding, physicalDecodeType)
            : Array.CreateInstance(physicalDecodeType, 0);

        if (!hasNulls)
            return ExpandAllPresent<T>(physicalValues, totalValueCount);

        var definitionLevels = ReadDefinitionLevels(definitionPayload, totalValueCount, out var nonNullCount);
        if (nonNullCount != (int)physicalValueCount)
            throw new CorruptParquetException(
                $"Definition levels indicate {nonNullCount} non-null values but page header claimed {physicalValueCount}.");
        return ExpandWithDefinitionLevels<T>(physicalValues, definitionLevels, totalValueCount);
    }

    static ReadOnlyMemory<T> ExpandAllPresent<T>(Array physicalValues, uint valueCount)
    {
        var result = new T[(int)valueCount];
        if (typeof(T) == typeof(int?))
        {
            var src = (int[])physicalValues;
            var dst = (int?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(long?))
        {
            var src = (long[])physicalValues;
            var dst = (long?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(bool?))
        {
            var src = (bool[])physicalValues;
            var dst = (bool?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(float?))
        {
            var src = (float[])physicalValues;
            var dst = (float?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(double?))
        {
            var src = (double[])physicalValues;
            var dst = (double?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(byte?))
        {
            var src = (byte[])physicalValues;
            var dst = (byte?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(ushort?))
        {
            var src = (ushort[])physicalValues;
            var dst = (ushort?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(uint?))
        {
            var src = (uint[])physicalValues;
            var dst = (uint?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(ulong?))
        {
            var src = (ulong[])physicalValues;
            var dst = (ulong?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(DateOnly?))
        {
            var src = (DateOnly[])physicalValues;
            var dst = (DateOnly?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(DateTime?))
        {
            var src = (DateTime[])physicalValues;
            var dst = (DateTime?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(DateTimeOffset?))
        {
            var src = (DateTimeOffset[])physicalValues;
            var dst = (DateTimeOffset?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(TimeOnly?))
        {
            var src = (TimeOnly[])physicalValues;
            var dst = (TimeOnly?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            var src = (ReadOnlyMemory<byte>[])physicalValues;
            var dst = (ReadOnlyMemory<byte>?[])(object)result;
            for (var i = 0; i < valueCount; i++) dst[i] = src[i];
        }
        else
        {
            for (var i = 0; i < valueCount; i++)
                result[i] = (T)physicalValues.GetValue(i)!;
        }
        return new ReadOnlyMemory<T>(result, 0, (int)valueCount);
    }

    static int[] ReadDefinitionLevels(ReadOnlySpan<byte> payload, uint valueCount, out int nonNullCount)
    {
        var values = new int[checked((int)valueCount)];
        var valueIndex = 0U;
        var count = 0;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                var repeated = ReadLittleEndian(ref payload, byteWidth: 1);
                var copyLength = (int)Math.Min(runLength, valueCount - valueIndex);
                if (repeated != 0)
                {
                    Array.Fill(values, repeated, (int)valueIndex, copyLength);
                    count += copyLength;
                }
                valueIndex += (uint)copyLength;
                continue;
            }

            var literalCount = checked((header >> 1) * 8U);
            for (var i = 0U; i < literalCount && valueIndex < valueCount; i++)
            {
                var v = ReadBitPackedValue(ref payload, bitWidth: 1, checked((int)i));
                values[checked((int)valueIndex++)] = v;
                count += v;
            }

            var literalByteCount = (literalCount + 7U) >> 3;
            payload = payload[checked((int)literalByteCount)..];
        }

        nonNullCount = count;
        return values;
    }

    static ReadOnlyMemory<T> ExpandWithDefinitionLevels<T>(Array physicalValues, int[] definitionLevels, uint totalValueCount)
    {
        var result = new T[(int)totalValueCount];
        var valueIndex = 0;
        if (typeof(T) == typeof(int?))
        {
            var src = (int[])physicalValues;
            var dst = (int?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(long?))
        {
            var src = (long[])physicalValues;
            var dst = (long?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(bool?))
        {
            var src = (bool[])physicalValues;
            var dst = (bool?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(float?))
        {
            var src = (float[])physicalValues;
            var dst = (float?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(double?))
        {
            var src = (double[])physicalValues;
            var dst = (double?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(byte?))
        {
            var src = (byte[])physicalValues;
            var dst = (byte?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(ushort?))
        {
            var src = (ushort[])physicalValues;
            var dst = (ushort?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(uint?))
        {
            var src = (uint[])physicalValues;
            var dst = (uint?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(ulong?))
        {
            var src = (ulong[])physicalValues;
            var dst = (ulong?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(DateOnly?))
        {
            var src = (DateOnly[])physicalValues;
            var dst = (DateOnly?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(DateTime?))
        {
            var src = (DateTime[])physicalValues;
            var dst = (DateTime?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(DateTimeOffset?))
        {
            var src = (DateTimeOffset[])physicalValues;
            var dst = (DateTimeOffset?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(TimeOnly?))
        {
            var src = (TimeOnly[])physicalValues;
            var dst = (TimeOnly?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else if (typeof(T) == typeof(ReadOnlyMemory<byte>?))
        {
            var src = (ReadOnlyMemory<byte>[])physicalValues;
            var dst = (ReadOnlyMemory<byte>?[])(object)result;
            for (var i = 0; i < totalValueCount; i++)
                dst[i] = definitionLevels[i] != 0 ? src[valueIndex++] : null;
        }
        else
        {
            // Reference types (string, byte[], etc.): default(T) is null, works for nullable semantics.
            for (var i = 0; i < totalValueCount; i++)
            {
                if (definitionLevels[i] != 0)
                    result[i] = (T)physicalValues.GetValue(valueIndex++)!;
            }
        }
        return new ReadOnlyMemory<T>(result, 0, (int)totalValueCount);
    }

    static int[] ReadRleBitPackedHybrid(ReadOnlySpan<byte> payload, uint valueCount, bool hasBitWidthPrefix)
    {
        if (payload.IsEmpty)
            return [];
        var bitWidth = hasBitWidthPrefix ? payload[0] : 0;
        if (hasBitWidthPrefix)
            payload = payload[1..];
        return ReadRleBitPackedHybrid(payload, valueCount, bitWidth);
    }

    static int[] ReadRleBitPackedHybrid(ReadOnlySpan<byte> payload, uint valueCount, int bitWidth)
    {
        var values = new int[checked((int)valueCount)];
        var valueIndex = 0U;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                var byteWidth = (bitWidth + 7) >> 3;
                var repeated = byteWidth == 0 ? 0 : ReadLittleEndian(ref payload, byteWidth);
                var copyLength = Math.Min(runLength, valueCount - valueIndex);
                Array.Fill(values, repeated, checked((int)valueIndex), checked((int)copyLength));
                valueIndex += copyLength;
                continue;
            }

            var literalCount = checked((header >> 1) * 8U);
            for (var i = 0U; i < literalCount && valueIndex < valueCount; i++)
                values[checked((int)valueIndex++)] = ReadBitPackedValue(ref payload, bitWidth, checked((int)i));

            var literalByteCount = checked(((literalCount * (uint)bitWidth) + 7U) >> 3);
            payload = payload[checked((int)literalByteCount)..];
        }

        return values;
    }

    static void DecodeBooleanRle(ReadOnlySpan<byte> payload, Span<bool> destination)
    {
        var valueIndex = 0U;
        var destinationLength = (uint)destination.Length;
        while (valueIndex < destinationLength)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = header >> 1;
                var repeated = ReadLittleEndian(ref payload, 1) != 0;
                var copyLength = Math.Min(runLength, destinationLength - valueIndex);
                destination.Slice(checked((int)valueIndex), checked((int)copyLength)).Fill(repeated);
                valueIndex += copyLength;
                continue;
            }

            var literalCount = checked((header >> 1) * 8U);
            for (var i = 0U; i < literalCount && valueIndex < destinationLength; i++)
                destination[checked((int)valueIndex++)] = ReadBitPackedValue(ref payload, bitWidth: 1, checked((int)i)) != 0;

            var literalByteCount = (literalCount + 7U) >> 3;
            payload = payload[checked((int)literalByteCount)..];
        }
    }

    static int ReadBitPackedValue(ref ReadOnlySpan<byte> payload, int bitWidth, int index)
    {
        if (bitWidth == 0)
            return 0;

        var bitOffset = index * bitWidth;
        var byteIndex = bitOffset >> 3;
        var shift = bitOffset & 7;
        if (byteIndex >= payload.Length)
            throw new CorruptParquetException(
                $"Bit-packed value at bit offset {bitOffset} reads past end of payload ({payload.Length} bytes).");
        ulong bits = payload[byteIndex];
        if (byteIndex + 1 < payload.Length)
            bits |= (ulong)payload[byteIndex + 1] << 8;
        if (byteIndex + 2 < payload.Length)
            bits |= (ulong)payload[byteIndex + 2] << 16;
        if (byteIndex + 3 < payload.Length)
            bits |= (ulong)payload[byteIndex + 3] << 24;
        bits >>= shift;
        var mask = (1UL << bitWidth) - 1UL;
        return (int)(bits & mask);
    }

    static uint ReadUnsignedVarInt(ref ReadOnlySpan<byte> payload)
    {
        uint value = 0;
        var shift = 0;
        while (true)
        {
            if (payload.IsEmpty)
                throw new CorruptParquetException("Unexpected end of RLE/bit-pack payload while reading varint.");
            var b = payload[0];
            payload = payload[1..];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }
    }

    static int ReadLittleEndian(ref ReadOnlySpan<byte> payload, int byteWidth)
    {
        if (byteWidth > payload.Length)
            throw new CorruptParquetException(
                $"RLE run needs {byteWidth} bytes but only {payload.Length} remain.");
        var value = byteWidth switch
        {
            1 => payload[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(payload),
            3 => payload[0] | (payload[1] << 8) | (payload[2] << 16),
            4 => unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload)),
            _ => throw new CorruptParquetException($"Unsupported RLE byte width '{byteWidth}'.")
        };
        payload = payload[byteWidth..];
        return value;
    }

}
