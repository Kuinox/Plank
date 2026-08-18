using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class AlpEncoding
{
    const int HeaderSize = 7;
    const int LogVectorSize = 10;
    const int VectorSize = 1 << LogVectorSize;
    const int MaxStackOffsets = 256;

    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values,
        BufferWriterFactory bufferWriters, ref BufferWriter writer)
        where T : notnull
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Float when typeof(T) == typeof(float):
                WriteFloatPage(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<float>>(ref values),
                    bufferWriters, ref writer);
                return;
            case ParquetPhysicalType.Double when typeof(T) == typeof(double):
                WriteDoublePage(Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<double>>(ref values),
                    bufferWriters, ref writer);
                return;
            case ParquetPhysicalType.Float:
            case ParquetPhysicalType.Double:
                throw new InvalidOperationException(
                    $"Column '{column.Name}' expects '{column.PhysicalType}' values, but got '{typeof(T)}'.");
            default:
                throw new NotSupportedException(
                    $"ALP encoding does not support physical type '{column.PhysicalType}' for column '{column.Name}'.");
        }
    }

    static void WriteFloatPage(ReadOnlySpan<float> values, BufferWriterFactory bufferWriters,
        ref BufferWriter writer)
    {
        var vectorCount = GetVectorCount(values.Length);
        ParquetBuffer offsetBuffer = default;
        ParquetBuffer encodedBuffer = default;
        Span<uint> offsets = vectorCount <= MaxStackOffsets
            ? stackalloc uint[vectorCount]
            : (offsetBuffer = bufferWriters.RentScratch<uint>(checked((uint)vectorCount)))
                .AsSpan<uint>()[..vectorCount];
        var encoded = values.IsEmpty
            ? Span<int>.Empty
            : (encodedBuffer = bufferWriters.RentScratch<int>(
                checked((uint)Math.Min(values.Length, VectorSize)))).AsSpan<int>()[..Math.Min(values.Length, VectorSize)];
        var vectors = bufferWriters.CreatePageBufferWriter();
        try
        {
            for (var vectorIndex = 0; vectorIndex < vectorCount; vectorIndex++)
            {
                offsets[vectorIndex] = checked((uint)(vectorCount * sizeof(uint) + vectors.WrittenLength));
                var start = vectorIndex * VectorSize;
                var count = Math.Min(VectorSize, values.Length - start);
                WriteFloatVector(values.Slice(start, count), encoded[..count], ref vectors);
            }

            WritePageHeaderAndOffsets(values.Length, offsets, ref writer);
            writer.CopyFrom(ref vectors);
        }
        finally
        {
            vectors.Dispose();
            encodedBuffer.Dispose();
            offsetBuffer.Dispose();
        }
    }

    static void WriteDoublePage(ReadOnlySpan<double> values, BufferWriterFactory bufferWriters,
        ref BufferWriter writer)
    {
        var vectorCount = GetVectorCount(values.Length);
        ParquetBuffer offsetBuffer = default;
        ParquetBuffer encodedBuffer = default;
        Span<uint> offsets = vectorCount <= MaxStackOffsets
            ? stackalloc uint[vectorCount]
            : (offsetBuffer = bufferWriters.RentScratch<uint>(checked((uint)vectorCount)))
                .AsSpan<uint>()[..vectorCount];
        var encoded = values.IsEmpty
            ? Span<long>.Empty
            : (encodedBuffer = bufferWriters.RentScratch<long>(
                checked((uint)Math.Min(values.Length, VectorSize)))).AsSpan<long>()[..Math.Min(values.Length, VectorSize)];
        var vectors = bufferWriters.CreatePageBufferWriter();
        try
        {
            for (var vectorIndex = 0; vectorIndex < vectorCount; vectorIndex++)
            {
                offsets[vectorIndex] = checked((uint)(vectorCount * sizeof(uint) + vectors.WrittenLength));
                var start = vectorIndex * VectorSize;
                var count = Math.Min(VectorSize, values.Length - start);
                WriteDoubleVector(values.Slice(start, count), encoded[..count], ref vectors);
            }

            WritePageHeaderAndOffsets(values.Length, offsets, ref writer);
            writer.CopyFrom(ref vectors);
        }
        finally
        {
            vectors.Dispose();
            encodedBuffer.Dispose();
            offsetBuffer.Dispose();
        }
    }

    static void WritePageHeaderAndOffsets(int valueCount, ReadOnlySpan<uint> offsets,
        ref BufferWriter writer)
    {
        var byteCount = checked(HeaderSize + offsets.Length * sizeof(uint));
        var destination = writer.GetSpan(byteCount)[..byteCount];
        destination[0] = 0; // ALP compression mode.
        destination[1] = 0; // FOR plus bit-packing integer encoding.
        destination[2] = LogVectorSize;
        BinaryPrimitives.WriteInt32LittleEndian(destination[3..], valueCount);
        for (var i = 0; i < offsets.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(HeaderSize + i * sizeof(uint))..], offsets[i]);
        writer.Advance(byteCount);
    }

    static void WriteFloatVector(ReadOnlySpan<float> values, Span<int> encoded, ref BufferWriter writer)
    {
        var info = SelectFloatParameters(values);
        var placeholder = 0;
        for (var i = 0; i < values.Length; i++)
            if (AlpEncodingPrimitives.TryEncode(values[i], info.Exponent, info.Factor, out placeholder))
                break;

        for (var i = 0; i < values.Length; i++)
            if (!AlpEncodingPrimitives.TryEncode(values[i], info.Exponent, info.Factor, out encoded[i]))
                encoded[i] = placeholder;

        var packedByteCount = GetPackedByteCount(values.Length, info.BitWidth);
        var vectorByteCount = checked(9 + packedByteCount + info.ExceptionCount * 6);
        var destination = writer.GetSpan(vectorByteCount)[..vectorByteCount];
        destination[0] = info.Exponent;
        destination[1] = info.Factor;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], checked((ushort)info.ExceptionCount));
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], info.FrameOfReference);
        destination[8] = info.BitWidth;
        Pack(encoded, info.FrameOfReference, info.BitWidth, destination.Slice(9, packedByteCount));

        var positionOffset = 9 + packedByteCount;
        var valueOffset = positionOffset + info.ExceptionCount * sizeof(ushort);
        var exceptionIndex = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (AlpEncodingPrimitives.TryEncode(values[i], info.Exponent, info.Factor, out _))
                continue;
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[(positionOffset + exceptionIndex * sizeof(ushort))..], checked((ushort)i));
            BinaryPrimitives.WriteInt32LittleEndian(
                destination[(valueOffset + exceptionIndex * sizeof(float))..],
                BitConverter.SingleToInt32Bits(values[i]));
            exceptionIndex++;
        }

        writer.Advance(vectorByteCount);
    }

    static void WriteDoubleVector(ReadOnlySpan<double> values, Span<long> encoded, ref BufferWriter writer)
    {
        var info = SelectDoubleParameters(values);
        var placeholder = 0L;
        for (var i = 0; i < values.Length; i++)
            if (AlpEncodingPrimitives.TryEncode(values[i], info.Exponent, info.Factor, out placeholder))
                break;

        for (var i = 0; i < values.Length; i++)
            if (!AlpEncodingPrimitives.TryEncode(values[i], info.Exponent, info.Factor, out encoded[i]))
                encoded[i] = placeholder;

        var packedByteCount = GetPackedByteCount(values.Length, info.BitWidth);
        var vectorByteCount = checked(13 + packedByteCount + info.ExceptionCount * 10);
        var destination = writer.GetSpan(vectorByteCount)[..vectorByteCount];
        destination[0] = info.Exponent;
        destination[1] = info.Factor;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], checked((ushort)info.ExceptionCount));
        BinaryPrimitives.WriteInt64LittleEndian(destination[4..], info.FrameOfReference);
        destination[12] = info.BitWidth;
        Pack(encoded, info.FrameOfReference, info.BitWidth, destination.Slice(13, packedByteCount));

        var positionOffset = 13 + packedByteCount;
        var valueOffset = positionOffset + info.ExceptionCount * sizeof(ushort);
        var exceptionIndex = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (AlpEncodingPrimitives.TryEncode(values[i], info.Exponent, info.Factor, out _))
                continue;
            BinaryPrimitives.WriteUInt16LittleEndian(
                destination[(positionOffset + exceptionIndex * sizeof(ushort))..], checked((ushort)i));
            BinaryPrimitives.WriteInt64LittleEndian(
                destination[(valueOffset + exceptionIndex * sizeof(double))..],
                BitConverter.DoubleToInt64Bits(values[i]));
            exceptionIndex++;
        }

        writer.Advance(vectorByteCount);
    }

    static FloatVectorInfo SelectFloatParameters(ReadOnlySpan<float> values)
    {
        var best = default(FloatVectorInfo);
        var bestSize = int.MaxValue;
        for (var exponent = 0; exponent <= AlpEncodingPrimitives.FloatMaxExponent; exponent++)
            for (var factor = 0; factor <= exponent; factor++)
            {
                var exceptionCount = 0;
                var minimum = int.MaxValue;
                var maximum = int.MinValue;
                var hasEncodedValue = false;
                for (var i = 0; i < values.Length; i++)
                {
                    if (!AlpEncodingPrimitives.TryEncode(values[i], exponent, factor, out var encoded))
                    {
                        exceptionCount++;
                        continue;
                    }
                    hasEncodedValue = true;
                    minimum = Math.Min(minimum, encoded);
                    maximum = Math.Max(maximum, encoded);
                }

                if (!hasEncodedValue)
                    minimum = maximum = 0;
                var bitWidth = GetBitWidth(unchecked((uint)maximum - (uint)minimum));
                var size = checked(GetPackedByteCount(values.Length, bitWidth) + exceptionCount * 6);
                if (size >= bestSize)
                    continue;
                bestSize = size;
                best = new FloatVectorInfo((byte)exponent, (byte)factor, exceptionCount,
                    minimum, checked((byte)bitWidth));
            }
        return best;
    }

    static DoubleVectorInfo SelectDoubleParameters(ReadOnlySpan<double> values)
    {
        var best = default(DoubleVectorInfo);
        var bestSize = int.MaxValue;
        for (var exponent = 0; exponent <= AlpEncodingPrimitives.DoubleMaxExponent; exponent++)
            for (var factor = 0; factor <= exponent; factor++)
            {
                var exceptionCount = 0;
                var minimum = long.MaxValue;
                var maximum = long.MinValue;
                var hasEncodedValue = false;
                for (var i = 0; i < values.Length; i++)
                {
                    if (!AlpEncodingPrimitives.TryEncode(values[i], exponent, factor, out var encoded))
                    {
                        exceptionCount++;
                        continue;
                    }
                    hasEncodedValue = true;
                    minimum = Math.Min(minimum, encoded);
                    maximum = Math.Max(maximum, encoded);
                }

                if (!hasEncodedValue)
                    minimum = maximum = 0;
                var bitWidth = GetBitWidth(unchecked((ulong)maximum - (ulong)minimum));
                var size = checked(GetPackedByteCount(values.Length, bitWidth) + exceptionCount * 10);
                if (size >= bestSize)
                    continue;
                bestSize = size;
                best = new DoubleVectorInfo((byte)exponent, (byte)factor, exceptionCount,
                    minimum, checked((byte)bitWidth));
            }
        return best;
    }

    static void Pack(ReadOnlySpan<int> values, int frameOfReference, int bitWidth, Span<byte> destination)
    {
        destination.Clear();
        if (bitWidth == 0)
            return;
        UInt128 accumulator = 0;
        var bitsInAccumulator = 0;
        var outputOffset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var delta = unchecked((uint)values[i] - (uint)frameOfReference);
            accumulator |= (UInt128)delta << bitsInAccumulator;
            bitsInAccumulator += bitWidth;
            while (bitsInAccumulator >= 8)
            {
                destination[outputOffset++] = (byte)accumulator;
                accumulator >>= 8;
                bitsInAccumulator -= 8;
            }
        }
        if (bitsInAccumulator != 0)
            destination[outputOffset] = (byte)accumulator;
    }

    static void Pack(ReadOnlySpan<long> values, long frameOfReference, int bitWidth, Span<byte> destination)
    {
        destination.Clear();
        if (bitWidth == 0)
            return;
        UInt128 accumulator = 0;
        var bitsInAccumulator = 0;
        var outputOffset = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var delta = unchecked((ulong)values[i] - (ulong)frameOfReference);
            accumulator |= (UInt128)delta << bitsInAccumulator;
            bitsInAccumulator += bitWidth;
            while (bitsInAccumulator >= 8)
            {
                destination[outputOffset++] = (byte)accumulator;
                accumulator >>= 8;
                bitsInAccumulator -= 8;
            }
        }
        if (bitsInAccumulator != 0)
            destination[outputOffset] = (byte)accumulator;
    }

    static int GetVectorCount(int valueCount)
        => checked((int)(((long)valueCount + VectorSize - 1) / VectorSize));

    static int GetPackedByteCount(int valueCount, int bitWidth)
        => checked((int)(((long)valueCount * bitWidth + 7) / 8));

    static int GetBitWidth(uint value)
        => 32 - BitOperations.LeadingZeroCount(value);

    static int GetBitWidth(ulong value)
        => 64 - BitOperations.LeadingZeroCount(value);

    readonly record struct FloatVectorInfo(byte Exponent, byte Factor, int ExceptionCount,
        int FrameOfReference, byte BitWidth);

    readonly record struct DoubleVectorInfo(byte Exponent, byte Factor, int ExceptionCount,
        long FrameOfReference, byte BitWidth);
}
