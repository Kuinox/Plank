using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using K4os.Compression.LZ4;
using Plank.Schema;
using Plank.Snappy;
using Plank.Writing;
using ZstdSharp;

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

            var payload = buffer.AsSpan(offset, header.CompressedPageSize);
            offset += header.CompressedPageSize;

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
                    var physicalValueCount = header.ValueCount - header.NullCount;

                    // In DataPageV2, levels are always uncompressed; only the values portion may be compressed.
                    var definitionPayload = defLen > 0 ? payload.Slice(repLen, defLen) : default;
                    var dataPayload = payload[levelBytes..];

                    ReadOnlySpan<byte> effectiveData;
                    if (header.IsCompressed && dataPayload.Length > 0)
                    {
                        var expectedUncompressedDataSize = header.UncompressedPageSize - levelBytes;
                        effectiveData = Decompress(dataPayload, expectedUncompressedDataSize, columnChunk.Compression);
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
                                header.ValueCount, physicalValueCount, header.Encoding, header.NullCount > 0);
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
                            values = DecodeDictionaryIndexesWithNulls<T>(effectiveData, header.ValueCount, physicalValueCount,
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

    static bool TryDecodeValuesIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, int valueCount,
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
            default:
                values = default;
                return false;
        }
    }

    static bool TryDecodePlainIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, int valueCount, ref T[]? valuesBuffer,
        out ReadOnlyMemory<T> values)
    {
        if (typeof(T) == typeof(bool) && column.PhysicalType == ParquetPhysicalType.Boolean)
        {
            var typed = (bool[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
                typed[i] = ((payload[i >> 3] >> (i & 7)) & 1) != 0;
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(int) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            var typed = (int[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianInt32(payload, typed.AsSpan(0, valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(byte) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            var typed = (byte[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
                typed[i] = unchecked((byte)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(ushort) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            var typed = (ushort[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
                typed[i] = unchecked((ushort)BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4)));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(uint) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            var typed = (uint[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianUInt32(payload, typed.AsSpan(0, valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(long) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            var typed = (long[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianInt64(payload, typed.AsSpan(0, valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(ulong) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
            var typed = (ulong[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianUInt64(payload, typed.AsSpan(0, valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(float) && column.PhysicalType == ParquetPhysicalType.Float)
        {
            var typed = (float[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianFloat(payload, typed.AsSpan(0, valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(double) && column.PhysicalType == ParquetPhysicalType.Double)
        {
            var typed = (double[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            CopyLittleEndianDouble(payload, typed.AsSpan(0, valueCount));
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(DateOnly) && column.PhysicalType == ParquetPhysicalType.Int32)
        {
            var typed = (DateOnly[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var days = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(i * 4, 4));
                typed[i] = DateOnly.FromDayNumber(days);
            }
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(DateTimeOffset) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
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
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        if (typeof(T) == typeof(DateTime) && column.PhysicalType == ParquetPhysicalType.Int64)
        {
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
            values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
            return true;
        }

        values = default;
        return false;
    }

    static bool TryDecodeBooleanRleIntoBuffer<T>(ReadOnlySpan<byte> payload, int valueCount, ref T[]? valuesBuffer,
        out ReadOnlyMemory<T> values)
    {
        if (typeof(T) != typeof(bool))
        {
            values = default;
            return false;
        }

        var ints = ReadRleBitPackedHybrid(payload, valueCount, bitWidth: 1);
        var typed = (bool[])(object)EnsureValueBuffer(ref valuesBuffer, ints.Length);
        for (var i = 0; i < ints.Length; i++)
            typed[i] = ints[i] != 0;
        values = new ReadOnlyMemory<T>(valuesBuffer!, 0, ints.Length);
        return true;
    }

    static bool TryDecodeByteStreamSplitIntoBuffer<T>(ReadOnlySpan<byte> payload, Column column, int valueCount,
        ref T[]? valuesBuffer, out ReadOnlyMemory<T> values)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(int):
            {
                var typed = (int[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitInt32(payload, typed.AsSpan(0, valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(byte):
            {
                var typed = (byte[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                for (var i = 0; i < valueCount; i++)
                    typed[i] = (byte)(payload[i] | (payload[valueCount + i] << 8) | (payload[(valueCount * 2) + i] << 16) |
                        (payload[(valueCount * 3) + i] << 24));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(ushort):
            {
                var typed = (ushort[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                for (var i = 0; i < valueCount; i++)
                    typed[i] = unchecked((ushort)(payload[i] | (payload[valueCount + i] << 8) |
                        (payload[(valueCount * 2) + i] << 16) | (payload[(valueCount * 3) + i] << 24)));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
                return true;
            }
            case ParquetPhysicalType.Int32 when typeof(T) == typeof(uint):
            {
                var typed = (uint[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                for (var i = 0; i < valueCount; i++)
                    typed[i] = unchecked((uint)(payload[i] | (payload[valueCount + i] << 8) |
                        (payload[(valueCount * 2) + i] << 16) | (payload[(valueCount * 3) + i] << 24)));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(long):
            {
                var typed = (long[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitInt64(payload, typed.AsSpan(0, valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
                return true;
            }
            case ParquetPhysicalType.Int64 when typeof(T) == typeof(ulong):
            {
                var typed = (ulong[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitUInt64(payload, typed.AsSpan(0, valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
                return true;
            }
            case ParquetPhysicalType.Float when typeof(T) == typeof(float):
            {
                var typed = (float[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitFloat(payload, typed.AsSpan(0, valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
                return true;
            }
            case ParquetPhysicalType.Double when typeof(T) == typeof(double):
            {
                var typed = (double[])(object)EnsureValueBuffer(ref valuesBuffer, valueCount);
                DecodeByteStreamSplitDouble(payload, typed.AsSpan(0, valueCount));
                values = new ReadOnlyMemory<T>(valuesBuffer!, 0, valueCount);
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
            : Decompress(payload, header.UncompressedPageSize, compression);

        if (physicalDecodeType == typeof(T) &&
            TryDecodeValuesIntoBuffer(effectivePayload, column, header.ValueCount, header.Encoding,
                ref dictionaryBuffer, out _))
            return dictionaryBuffer!;

        return DecodeValues(effectivePayload, column, header.ValueCount, header.Encoding, physicalDecodeType);
    }

    static T[] EnsureValueBuffer<T>(ref T[]? buffer, int minimumLength)
    {
        if (buffer is not null && buffer.Length >= minimumLength)
            return buffer;

        if (buffer is not null)
            ArrayRenter<T>.Shared.Return(buffer, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());

        buffer = ArrayRenter<T>.Shared.Rent(minimumLength);
        return buffer;
    }

    static void DecodeDictionaryIndexes<T>(ReadOnlySpan<byte> payload, int valueCount, Array dictionary,
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

        values = new ReadOnlyMemory<T>(result, 0, valueCount);
    }

    static T[] DecodeDictionaryIndexesWithNulls<T>(ReadOnlySpan<byte> dataPayload, int totalValueCount,
        int physicalValueCount, Array dictionary, ReadOnlySpan<byte> definitionPayload)
    {
        var indexes = ReadRleBitPackedHybrid(dataPayload, physicalValueCount, hasBitWidthPrefix: true);
        var definitionLevels = ReadRleBitPackedHybrid(definitionPayload, totalValueCount, bitWidth: 1);
        var result = new T[totalValueCount];
        var valueIndex = 0;
        for (var i = 0; i < totalValueCount; i++)
        {
            if (definitionLevels[i] != 0)
                result[i] = (T)dictionary.GetValue(indexes[valueIndex++])!;
        }
        return result;
    }

    static void DecodeDictionaryIndexesIntoBuffer<T>(ReadOnlySpan<byte> payload, int valueCount, T[] dictionary,
        T[] destination)
    {
        if (valueCount == 0)
            return;

        var bitWidth = payload[0];
        payload = payload[1..];
        var valueIndex = 0;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = checked((int)(header >> 1));
                var byteWidth = (bitWidth + 7) >> 3;
                var dictionaryIndex = byteWidth == 0 ? 0 : ReadLittleEndian(ref payload, byteWidth);
                var repeated = dictionary[dictionaryIndex];
                var copyLength = Math.Min(runLength, valueCount - valueIndex);
                destination.AsSpan(valueIndex, copyLength).Fill(repeated);
                valueIndex += copyLength;
                continue;
            }

            var literalCount = checked((int)(header >> 1) * 8);
            var literalByteCount = ((literalCount * bitWidth) + 7) >> 3;
            var literalPayload = payload[..literalByteCount];
            var literalCopyLength = Math.Min(literalCount, valueCount - valueIndex);
            DecodeDictionaryLiteralIndexes(literalPayload, bitWidth, dictionary, destination.AsSpan(valueIndex, literalCopyLength));
            valueIndex += literalCopyLength;
            payload = payload[literalByteCount..];
        }
    }

    static void DecodeDictionaryLiteralIndexes<T>(ReadOnlySpan<byte> payload, int bitWidth, T[] dictionary,
        Span<T> destination)
    {
        if (bitWidth == 0)
        {
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
            destination[i] = dictionary[dictionaryIndex];
        }
    }

    static Array DecodeValues(ReadOnlySpan<byte> payload, Column column, PageHeader header, CompressionKind compression,
        Type targetType)
    {
        var bytes = compression == CompressionKind.None || header.CompressedPageSize == 0
            ? payload.ToArray()
            : Decompress(payload, header.UncompressedPageSize, compression);
        return DecodeValues(bytes, column, header.ValueCount, header.Encoding, targetType);
    }

    static Array DecodeValues(ReadOnlySpan<byte> payload, Column column, int valueCount, EncodingKind encoding,
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

    static Array DecodePlain(ReadOnlySpan<byte> payload, Column column, int valueCount, Type targetType)
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

    static Array DecodePlainBoolean(ReadOnlySpan<byte> payload, int valueCount, Type targetType)
    {
        if (targetType != typeof(bool))
            throw new InvalidOperationException($"Boolean column cannot be projected to '{targetType}'.");

        var values = new bool[valueCount];
        for (var i = 0; i < valueCount; i++)
            values[i] = ((payload[i >> 3] >> (i & 7)) & 1) != 0;
        return values;
    }

    static Array DecodePlainInt32(ReadOnlySpan<byte> payload, int valueCount, LogicalType? logicalType, Type targetType)
    {
        if (targetType == typeof(int))
        {
            var values = new int[valueCount];
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

    static Array DecodePlainInt64(ReadOnlySpan<byte> payload, int valueCount, LogicalType? logicalType, Type targetType)
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

    static Array DecodePlainFloat(ReadOnlySpan<byte> payload, int valueCount, Type targetType)
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

    static Array DecodePlainDouble(ReadOnlySpan<byte> payload, int valueCount, Type targetType)
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

    static Array DecodePlainByteArray(ReadOnlySpan<byte> payload, int valueCount, Type targetType)
    {
        if (targetType == typeof(byte[]))
        {
            var values = new byte[valueCount][];
            var offset = 0;
            for (var i = 0; i < valueCount; i++)
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
                offset += 4;
                values[i] = payload.Slice(offset, length).ToArray();
                offset += length;
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

    static Array DecodeFixedLengthByteArray(ReadOnlySpan<byte> payload, int valueCount, int valueLength, Type targetType)
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

    static Array DecodeBooleanRle(ReadOnlySpan<byte> payload, int valueCount, Type targetType)
    {
        if (targetType != typeof(bool))
            throw new InvalidOperationException($"Boolean column cannot be projected to '{targetType}'.");

        var ints = ReadRleBitPackedHybrid(payload, valueCount, bitWidth: 1);
        var values = new bool[ints.Length];
        for (var i = 0; i < ints.Length; i++)
            values[i] = ints[i] != 0;
        return values;
    }

    static Array DecodeByteStreamSplit(ReadOnlySpan<byte> payload, Column column, int valueCount, Type targetType)
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32:
            {
                if (targetType == typeof(int))
                {
                    var values = new int[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = payload[i] | (payload[valueCount + i] << 8) | (payload[(valueCount * 2) + i] << 16) |
                            (payload[(valueCount * 3) + i] << 24);
                    return values;
                }
                if (targetType == typeof(byte))
                {
                    var values = new byte[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = (byte)(payload[i] | (payload[valueCount + i] << 8) | (payload[(valueCount * 2) + i] << 16) |
                            (payload[(valueCount * 3) + i] << 24));
                    return values;
                }
                if (targetType == typeof(ushort))
                {
                    var values = new ushort[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = unchecked((ushort)(payload[i] | (payload[valueCount + i] << 8) |
                            (payload[(valueCount * 2) + i] << 16) | (payload[(valueCount * 3) + i] << 24)));
                    return values;
                }
                if (targetType == typeof(uint))
                {
                    var values = new uint[valueCount];
                    for (var i = 0; i < valueCount; i++)
                        values[i] = unchecked((uint)(payload[i] | (payload[valueCount + i] << 8) |
                            (payload[(valueCount * 2) + i] << 16) | (payload[(valueCount * 3) + i] << 24)));
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
                            value |= (ulong)payload[(lane * valueCount) + i] << (lane * 8);
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
                            value |= (ulong)payload[(lane * valueCount) + i] << (lane * 8);
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
        var lengths = DeltaBinaryPackedDecoder.ReadInt32(payload);
        var consumedLengthBytes = DeltaBinaryPackedDecoder.ReadConsumedByteCount(payload);
        var dataBytes = payload[consumedLengthBytes..];
        var values = new byte[lengths.Length][];
        var offset = 0;
        for (var i = 0; i < lengths.Length; i++)
        {
            values[i] = dataBytes.Slice(offset, lengths[i]).ToArray();
            offset += lengths[i];
        }
        return targetType == typeof(byte[]) ? values : DecodePlainByteArray(ToLengthPrefixed(values), values.Length, targetType);
    }

    static Array DecodeDeltaByteArray(ReadOnlySpan<byte> payload, Type targetType)
    {
        var prefixLengths = DeltaBinaryPackedDecoder.ReadInt32(payload);
        var prefixConsumed = DeltaBinaryPackedDecoder.ReadConsumedByteCount(payload);
        var suffixPayload = payload[prefixConsumed..];
        var suffixLengths = DeltaBinaryPackedDecoder.ReadInt32(suffixPayload);
        var suffixConsumed = DeltaBinaryPackedDecoder.ReadConsumedByteCount(suffixPayload);
        var suffixBytes = suffixPayload[suffixConsumed..];

        var values = new byte[prefixLengths.Length][];
        var offset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var prefixLength = prefixLengths[i];
            var suffixLength = suffixLengths[i];
            var value = new byte[prefixLength + suffixLength];
            if (prefixLength > 0 && i > 0)
                values[i - 1].AsSpan(0, prefixLength).CopyTo(value);
            if (suffixLength > 0)
            {
                suffixBytes.Slice(offset, suffixLength).CopyTo(value.AsSpan(prefixLength));
                offset += suffixLength;
            }
            values[i] = value;
        }

        return targetType == typeof(byte[]) ? values : DecodePlainByteArray(ToLengthPrefixed(values), values.Length, targetType);
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
        ReadOnlySpan<byte> definitionPayload, Column column, int totalValueCount, int physicalValueCount,
        EncodingKind encoding, bool hasNulls)
    {
        var physicalDecodeType = GetPhysicalDecodeType<T>();
        var physicalValues = physicalValueCount > 0
            ? DecodeValues(dataPayload, column, physicalValueCount, encoding, physicalDecodeType)
            : Array.CreateInstance(physicalDecodeType, 0);

        if (!hasNulls)
            return ExpandAllPresent<T>(physicalValues, totalValueCount);

        var definitionLevels = ReadRleBitPackedHybrid(definitionPayload, totalValueCount, bitWidth: 1);
        return ExpandWithDefinitionLevels<T>(physicalValues, definitionLevels, totalValueCount);
    }

    static ReadOnlyMemory<T> ExpandAllPresent<T>(Array physicalValues, int valueCount)
    {
        var result = new T[valueCount];
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
        return new ReadOnlyMemory<T>(result, 0, valueCount);
    }

    static ReadOnlyMemory<T> ExpandWithDefinitionLevels<T>(Array physicalValues, int[] definitionLevels, int totalValueCount)
    {
        var result = new T[totalValueCount];
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
        return new ReadOnlyMemory<T>(result, 0, totalValueCount);
    }

    static int[] ReadRleBitPackedHybrid(ReadOnlySpan<byte> payload, int valueCount, bool hasBitWidthPrefix)
    {
        if (payload.IsEmpty)
            return [];
        var bitWidth = hasBitWidthPrefix ? payload[0] : 0;
        if (hasBitWidthPrefix)
            payload = payload[1..];
        return ReadRleBitPackedHybrid(payload, valueCount, bitWidth);
    }

    static int[] ReadRleBitPackedHybrid(ReadOnlySpan<byte> payload, int valueCount, int bitWidth)
    {
        var values = new int[valueCount];
        var valueIndex = 0;
        while (valueIndex < valueCount)
        {
            var header = ReadUnsignedVarInt(ref payload);
            if ((header & 1U) == 0)
            {
                var runLength = checked((int)(header >> 1));
                var byteWidth = (bitWidth + 7) >> 3;
                var repeated = byteWidth == 0 ? 0 : ReadLittleEndian(ref payload, byteWidth);
                var copyLength = Math.Min(runLength, valueCount - valueIndex);
                Array.Fill(values, repeated, valueIndex, copyLength);
                valueIndex += copyLength;
                continue;
            }

            var literalCount = checked((int)(header >> 1) * 8);
            for (var i = 0; i < literalCount && valueIndex < valueCount; i++)
                values[valueIndex++] = ReadBitPackedValue(ref payload, bitWidth, i);

            var literalByteCount = ((literalCount * bitWidth) + 7) >> 3;
            payload = payload[literalByteCount..];
        }

        return values;
    }

    static int ReadBitPackedValue(ref ReadOnlySpan<byte> payload, int bitWidth, int index)
    {
        if (bitWidth == 0)
            return 0;

        var bitOffset = index * bitWidth;
        var byteIndex = bitOffset >> 3;
        var shift = bitOffset & 7;
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
        var value = byteWidth switch
        {
            1 => payload[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(payload),
            3 => payload[0] | (payload[1] << 8) | (payload[2] << 16),
            4 => unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload)),
            _ => throw new InvalidDataException($"Unsupported RLE byte width '{byteWidth}'.")
        };
        payload = payload[byteWidth..];
        return value;
    }

    static byte[] Decompress(ReadOnlySpan<byte> payload, int expectedLength, CompressionKind compression)
        => compression switch
        {
            CompressionKind.Gzip => DecompressWithStream(payload, expectedLength, static s => new GZipStream(s, CompressionMode.Decompress, leaveOpen: true)),
            CompressionKind.Brotli => DecompressWithStream(payload, expectedLength, static s => new BrotliStream(s, CompressionMode.Decompress, leaveOpen: true)),
            CompressionKind.Lz4 => DecompressLz4(payload, expectedLength),
            CompressionKind.Zstd => DecompressZstd(payload, expectedLength),
            CompressionKind.Snappy => DecompressSnappy(payload, expectedLength),
            _ => throw new NotSupportedException($"Compression '{compression}' is not supported.")
        };

    static byte[] DecompressWithStream(ReadOnlySpan<byte> payload, int expectedLength, Func<MemoryStream, Stream> create)
    {
        using var memory = new MemoryStream(payload.ToArray(), writable: false);
        using var stream = create(memory);
        var buffer = new byte[expectedLength];
        stream.ReadExactly(buffer);
        return buffer;
    }

    static byte[] DecompressLz4(ReadOnlySpan<byte> payload, int expectedLength)
    {
        var buffer = new byte[expectedLength];
        var written = LZ4Codec.Decode(payload, buffer);
        if (written != expectedLength)
            throw new InvalidDataException("LZ4 decompression did not produce the expected byte count.");
        return buffer;
    }

    static byte[] DecompressZstd(ReadOnlySpan<byte> payload, int expectedLength)
    {
        using var decompressor = new Decompressor();
        var buffer = new byte[expectedLength];
        var written = decompressor.Unwrap(payload, buffer);
        if (written != expectedLength)
            throw new InvalidDataException("Zstd decompression did not produce the expected byte count.");
        return buffer;
    }

    static byte[] DecompressSnappy(ReadOnlySpan<byte> payload, int expectedLength)
    {
        var buffer = new byte[expectedLength];
        var written = SnappyCodec.Decompress(payload, buffer);
        if (written != expectedLength)
            throw new InvalidDataException("Snappy decompression did not produce the expected byte count.");
        return buffer;
    }
}
