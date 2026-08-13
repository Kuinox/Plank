using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class DeltaBinaryPackedEncoding
{
    const int BlockSize = 128;
    const int MiniBlockCount = 4;
    const int MiniBlockSize = BlockSize / MiniBlockCount;
    const int MaxInt32HeaderByteCount = 13;
    const int MaxInt64HeaderByteCount = 18;

    internal static void WriteValues<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        switch (column.PhysicalType)
        {
            case ParquetPhysicalType.Int32:
                WriteInt32Values(column, values, ref writer);
                return;
            case ParquetPhysicalType.Int64:
                WriteInt64Values(column, values, ref writer);
                return;
            default:
                throw new NotSupportedException(
                    $"Encoding '{EncodingKind.DeltaBinaryPacked}' does not support physical type '{column.PhysicalType}' for column '{column.Name}'.");
        }
    }

    internal static void WriteInt32(ReadOnlySpan<int> values, ref BufferWriter writer)
    {
        var header = writer.GetSpan(MaxInt32HeaderByteCount);
        ref var headerStart = ref MemoryMarshal.GetReference(header);
        var headerLength = WriteUnsignedVarInt(BlockSize, ref headerStart);
        headerLength += WriteUnsignedVarInt(MiniBlockCount, ref Unsafe.Add(ref headerStart, headerLength));
        headerLength += WriteUnsignedVarInt((ulong)values.Length, ref Unsafe.Add(ref headerStart, headerLength));

        if (values.Length == 0)
        {
            headerLength += WriteUnsignedVarInt(0, ref Unsafe.Add(ref headerStart, headerLength));
            writer.Advance(headerLength);
            return;
        }

        ref var input = ref MemoryMarshal.GetReference(values);
        headerLength += WriteUnsignedVarInt(ZigZag32(input), ref Unsafe.Add(ref headerStart, headerLength));
        writer.Advance(headerLength);
        if (values.Length == 1)
            return;

        Span<long> deltas = stackalloc long[BlockSize];
        ref var deltaBuffer = ref MemoryMarshal.GetReference(deltas);
        var index = 1;
        while (index < values.Length)
        {
            var count = Math.Min(BlockSize, values.Length - index);
            var minDelta = PrepareInt32Block(ref input, index, count, ref deltaBuffer);
            WriteDeltaBlock(ref deltaBuffer, minDelta, ref writer);
            index += count;
        }
    }

    internal static void WriteInt64(ReadOnlySpan<long> values, ref BufferWriter writer)
    {
        var header = writer.GetSpan(MaxInt64HeaderByteCount);
        ref var headerStart = ref MemoryMarshal.GetReference(header);
        var headerLength = WriteUnsignedVarInt(BlockSize, ref headerStart);
        headerLength += WriteUnsignedVarInt(MiniBlockCount, ref Unsafe.Add(ref headerStart, headerLength));
        headerLength += WriteUnsignedVarInt((ulong)values.Length, ref Unsafe.Add(ref headerStart, headerLength));

        if (values.Length == 0)
        {
            headerLength += WriteUnsignedVarInt(0, ref Unsafe.Add(ref headerStart, headerLength));
            writer.Advance(headerLength);
            return;
        }

        ref var input = ref MemoryMarshal.GetReference(values);
        headerLength += WriteUnsignedVarInt(ZigZag64(input), ref Unsafe.Add(ref headerStart, headerLength));
        writer.Advance(headerLength);
        if (values.Length == 1)
            return;

        Span<long> deltas = stackalloc long[BlockSize];
        ref var deltaBuffer = ref MemoryMarshal.GetReference(deltas);
        var index = 1;
        while (index < values.Length)
        {
            var count = Math.Min(BlockSize, values.Length - index);
            var minDelta = PrepareInt64Block(ref input, index, count, ref deltaBuffer);
            WriteInt64DeltaBlock(ref deltaBuffer, minDelta, ref writer);
            index += count;
        }
    }

    static long PrepareInt32Block(ref int input, int inputOffset, int count, ref long deltas)
    {
        var minDelta = long.MaxValue;
        var i = 0;
        if (Avx512F.IsSupported)
        {
            var vectorMin = Vector512.Create(long.MaxValue);
            for (; i <= count - Vector512<long>.Count; i += Vector512<long>.Count)
            {
                var current = Avx512F.ConvertToVector512Int64(
                    Vector256.LoadUnsafe(ref input, (nuint)(inputOffset + i)));
                var previous = Avx512F.ConvertToVector512Int64(
                    Vector256.LoadUnsafe(ref input, (nuint)(inputOffset + i - 1)));
                var delta = Vector512.Subtract(current, previous);
                delta.StoreUnsafe(ref deltas, (nuint)i);
                vectorMin = Vector512.Min(vectorMin, delta);
            }

            minDelta = GetMinimum(vectorMin);
        }

        for (; i < count; i++)
        {
            var current = Unsafe.Add(ref input, inputOffset + i);
            var previous = Unsafe.Add(ref input, inputOffset + i - 1);
            var delta = (long)current - previous;
            Unsafe.Add(ref deltas, i) = delta;
            if (delta < minDelta)
                minDelta = delta;
        }

        FillPaddedDeltas(ref deltas, count, minDelta);
        return minDelta;
    }

    static long PrepareInt64Block(ref long input, int inputOffset, int count, ref long deltas)
    {
        var minDelta = long.MaxValue;
        var i = 0;
        if (Avx512F.IsSupported)
        {
            var vectorMin = Vector512.Create(long.MaxValue);
            for (; i <= count - Vector512<long>.Count; i += Vector512<long>.Count)
            {
                var current = Vector512.LoadUnsafe(ref input, (nuint)(inputOffset + i));
                var previous = Vector512.LoadUnsafe(ref input, (nuint)(inputOffset + i - 1));
                var delta = Vector512.Subtract(current, previous);
                delta.StoreUnsafe(ref deltas, (nuint)i);
                vectorMin = Vector512.Min(vectorMin, delta);
            }

            minDelta = GetMinimum(vectorMin);
        }

        for (; i < count; i++)
        {
            var current = Unsafe.Add(ref input, inputOffset + i);
            var previous = Unsafe.Add(ref input, inputOffset + i - 1);
            var delta = current - previous;
            Unsafe.Add(ref deltas, i) = delta;
            if (delta < minDelta)
                minDelta = delta;
        }

        FillPaddedDeltas(ref deltas, count, minDelta);
        return minDelta;
    }

    static void FillPaddedDeltas(ref long deltas, int count, long minDelta)
    {
        var i = count;
        if (Avx512F.IsSupported)
        {
            var fill = Vector512.Create(minDelta);
            for (; i <= BlockSize - Vector512<long>.Count; i += Vector512<long>.Count)
                fill.StoreUnsafe(ref deltas, (nuint)i);
        }

        for (; i < BlockSize; i++)
            Unsafe.Add(ref deltas, i) = minDelta;
    }

    static long GetMinimum(Vector512<long> values)
    {
        var result = values.GetElement(0);
        for (var i = 1; i < Vector512<long>.Count; i++)
            result = Math.Min(result, values.GetElement(i));
        return result;
    }

    static void WriteInt32Values<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) == typeof(int))
        {
            var intValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<int>>(ref values);
            WriteInt32(intValues, ref writer);
            return;
        }

        Span<int> converted = values.Length <= 256 ? stackalloc int[values.Length] : new int[values.Length];
        if (typeof(T) == typeof(byte))
        {
            var byteValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<byte>>(ref values);
            for (var i = 0; i < byteValues.Length; i++)
                converted[i] = byteValues[i];
            WriteInt32(converted, ref writer);
            return;
        }

        if (typeof(T) == typeof(ushort))
        {
            var ushortValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ushort>>(ref values);
            for (var i = 0; i < ushortValues.Length; i++)
                converted[i] = ushortValues[i];
            WriteInt32(converted, ref writer);
            return;
        }

        if (typeof(T) == typeof(uint))
        {
            var uintValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<uint>>(ref values);
            for (var i = 0; i < uintValues.Length; i++)
                converted[i] = unchecked((int)uintValues[i]);
            WriteInt32(converted, ref writer);
            return;
        }

        if (typeof(T) == typeof(decimal))
        {
            var decimalValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<decimal>>(ref values);
            for (var i = 0; i < decimalValues.Length; i++)
                converted[i] = ParquetDecimalConverter.ToInt32(decimalValues[i], column);
            WriteInt32(converted, ref writer);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.Int32}' values, but got '{typeof(T)}'.");
    }

    static void WriteInt64Values<T>(Column column, ReadOnlySpan<T> values, ref BufferWriter writer)
        where T : notnull
    {
        if (typeof(T) == typeof(long))
        {
            var longValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<long>>(ref values);
            WriteInt64(longValues, ref writer);
            return;
        }

        Span<long> converted = values.Length <= 256 ? stackalloc long[values.Length] : new long[values.Length];
        if (typeof(T) == typeof(ulong))
        {
            var ulongValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<ulong>>(ref values);
            for (var i = 0; i < ulongValues.Length; i++)
                converted[i] = unchecked((long)ulongValues[i]);
            WriteInt64(converted, ref writer);
            return;
        }

        if (typeof(T) == typeof(decimal))
        {
            var decimalValues = Unsafe.As<ReadOnlySpan<T>, ReadOnlySpan<decimal>>(ref values);
            for (var i = 0; i < decimalValues.Length; i++)
                converted[i] = ParquetDecimalConverter.ToInt64(decimalValues[i], column);
            WriteInt64(converted, ref writer);
            return;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.Int64}' values, but got '{typeof(T)}'.");
    }

    static void WriteDeltaBlock(ref long deltas, long minDelta, ref BufferWriter writer)
    {
        var bitWidths = Avx512F.IsSupported
            ? NormalizeDeltasVectorized(ref deltas, minDelta, out var packedByteCount)
            : NormalizeDeltasScalar(ref deltas, minDelta, out packedByteCount);

        var encodedMinDelta = ZigZag64(minDelta);
        var outputLength = GetUnsignedVarIntByteCount(encodedMinDelta) + MiniBlockCount + packedByteCount;
        var destination = writer.GetSpan(outputLength);
        ref var output = ref MemoryMarshal.GetReference(destination);
        var outputOffset = WriteUnsignedVarInt(encodedMinDelta, ref output);
        var bitWidthBytes = BitConverter.IsLittleEndian ? bitWidths : BinaryPrimitives.ReverseEndianness(bitWidths);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), bitWidthBytes);
        outputOffset += MiniBlockCount;

        for (var block = 0; block < MiniBlockCount; block++)
        {
            var width = (int)(byte)(bitWidths >> (block * 8));
            if (width == 0)
                continue;

            var byteCount = width * 4;
            PackUnsignedValues(
                ref Unsafe.Add(ref deltas, block * MiniBlockSize),
                width,
                ref Unsafe.Add(ref output, outputOffset));
            outputOffset += byteCount;
        }

        writer.Advance(outputLength);
    }

    static void WriteInt64DeltaBlock(ref long deltas, long minDelta, ref BufferWriter writer)
    {
        var bitWidths = Avx512F.IsSupported
            ? NormalizeDeltasVectorized(ref deltas, minDelta, out var packedByteCount)
            : NormalizeDeltasScalar(ref deltas, minDelta, out packedByteCount);

        var encodedMinDelta = ZigZag64(minDelta);
        var outputLength = GetUnsignedVarIntByteCount(encodedMinDelta) + MiniBlockCount + packedByteCount;
        var destination = writer.GetSpan(outputLength);
        ref var output = ref MemoryMarshal.GetReference(destination);
        var outputOffset = WriteUnsignedVarInt(encodedMinDelta, ref output);
        var bitWidthBytes = BitConverter.IsLittleEndian ? bitWidths : BinaryPrimitives.ReverseEndianness(bitWidths);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), bitWidthBytes);
        outputOffset += MiniBlockCount;

        var containsWidthAboveFour = ((bitWidths | 0x80808080U) - 0x05050505U) & 0x80808080U;
        if (containsWidthAboveFour == 0)
            PackNarrowInt64Block(ref deltas, bitWidths, ref Unsafe.Add(ref output, outputOffset));
        else
            PackGenericDeltaBlock(ref deltas, bitWidths, ref Unsafe.Add(ref output, outputOffset));

        writer.Advance(outputLength);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void PackGenericDeltaBlock(ref long deltas, uint bitWidths, ref byte output)
    {
        var outputOffset = 0;
        for (var block = 0; block < MiniBlockCount; block++)
        {
            var width = (int)(byte)(bitWidths >> (block * 8));
            if (width == 0)
                continue;

            var byteCount = width * 4;
            PackUnsignedValues(
                ref Unsafe.Add(ref deltas, block * MiniBlockSize),
                width,
                ref Unsafe.Add(ref output, outputOffset));
            outputOffset += byteCount;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void PackNarrowInt64Block(ref long input, uint bitWidths, ref byte output)
    {
        var outputOffset = 0;
        for (var block = 0; block < MiniBlockCount; block++)
        {
            ref var blockInput = ref Unsafe.Add(ref input, block * MiniBlockSize);
            var width = (byte)(bitWidths >> (block * 8));
            switch (width)
            {
                case 0:
                    break;
                case 1:
                    PackInt64Width1(ref blockInput, ref Unsafe.Add(ref output, outputOffset));
                    outputOffset += 4;
                    break;
                case 2:
                    PackInt64Width2(ref blockInput, ref Unsafe.Add(ref output, outputOffset));
                    outputOffset += 8;
                    break;
                case 3:
                    PackInt64Width3(ref blockInput, ref Unsafe.Add(ref output, outputOffset));
                    outputOffset += 12;
                    break;
                case 4:
                    PackInt64Width4(ref blockInput, ref Unsafe.Add(ref output, outputOffset));
                    outputOffset += 16;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected narrow bit width {width}.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void PackInt64Width1(ref long input, ref byte output)
    {
        for (var i = 0; i < MiniBlockSize; i += 8)
        {
            var packed = (ulong)Unsafe.Add(ref input, i) |
                         (ulong)Unsafe.Add(ref input, i + 1) << 1 |
                         (ulong)Unsafe.Add(ref input, i + 2) << 2 |
                         (ulong)Unsafe.Add(ref input, i + 3) << 3 |
                         (ulong)Unsafe.Add(ref input, i + 4) << 4 |
                         (ulong)Unsafe.Add(ref input, i + 5) << 5 |
                         (ulong)Unsafe.Add(ref input, i + 6) << 6 |
                         (ulong)Unsafe.Add(ref input, i + 7) << 7;
            Unsafe.Add(ref output, i >> 3) = (byte)packed;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void PackInt64Width2(ref long input, ref byte output)
    {
        for (var i = 0; i < MiniBlockSize; i += 8)
        {
            var packed = (ulong)Unsafe.Add(ref input, i) |
                         (ulong)Unsafe.Add(ref input, i + 1) << 2 |
                         (ulong)Unsafe.Add(ref input, i + 2) << 4 |
                         (ulong)Unsafe.Add(ref input, i + 3) << 6 |
                         (ulong)Unsafe.Add(ref input, i + 4) << 8 |
                         (ulong)Unsafe.Add(ref input, i + 5) << 10 |
                         (ulong)Unsafe.Add(ref input, i + 6) << 12 |
                         (ulong)Unsafe.Add(ref input, i + 7) << 14;
            var word = (ushort)packed;
            if (!BitConverter.IsLittleEndian)
                word = BinaryPrimitives.ReverseEndianness(word);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, (i >> 3) * sizeof(ushort)), word);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void PackInt64Width3(ref long input, ref byte output)
    {
        for (var i = 0; i < MiniBlockSize; i += 8)
        {
            var packed = (ulong)Unsafe.Add(ref input, i) |
                         (ulong)Unsafe.Add(ref input, i + 1) << 3 |
                         (ulong)Unsafe.Add(ref input, i + 2) << 6 |
                         (ulong)Unsafe.Add(ref input, i + 3) << 9 |
                         (ulong)Unsafe.Add(ref input, i + 4) << 12 |
                         (ulong)Unsafe.Add(ref input, i + 5) << 15 |
                         (ulong)Unsafe.Add(ref input, i + 6) << 18 |
                         (ulong)Unsafe.Add(ref input, i + 7) << 21;
            var outputIndex = (i >> 3) * 3;
            Unsafe.Add(ref output, outputIndex) = (byte)packed;
            Unsafe.Add(ref output, outputIndex + 1) = (byte)(packed >> 8);
            Unsafe.Add(ref output, outputIndex + 2) = (byte)(packed >> 16);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void PackInt64Width4(ref long input, ref byte output)
    {
        for (var i = 0; i < MiniBlockSize; i += 8)
        {
            var packed = (ulong)Unsafe.Add(ref input, i) |
                         (ulong)Unsafe.Add(ref input, i + 1) << 4 |
                         (ulong)Unsafe.Add(ref input, i + 2) << 8 |
                         (ulong)Unsafe.Add(ref input, i + 3) << 12 |
                         (ulong)Unsafe.Add(ref input, i + 4) << 16 |
                         (ulong)Unsafe.Add(ref input, i + 5) << 20 |
                         (ulong)Unsafe.Add(ref input, i + 6) << 24 |
                         (ulong)Unsafe.Add(ref input, i + 7) << 28;
            var word = (uint)packed;
            if (!BitConverter.IsLittleEndian)
                word = BinaryPrimitives.ReverseEndianness(word);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, (i >> 3) * sizeof(uint)), word);
        }
    }

    static uint NormalizeDeltasVectorized(ref long deltas, long minDelta, out int packedByteCount)
    {
        uint bitWidths = 0;
        packedByteCount = 0;
        var vectorMinDelta = Vector512.Create(minDelta);
        for (var block = 0; block < MiniBlockCount; block++)
        {
            var start = block * MiniBlockSize;
            var vectorMax = Vector512<ulong>.Zero;
            for (var i = 0; i < MiniBlockSize; i += Vector512<long>.Count)
            {
                var delta = Vector512.LoadUnsafe(ref deltas, (nuint)(start + i));
                var normalized = Vector512.Subtract(delta, vectorMinDelta).AsUInt64();
                normalized.AsInt64().StoreUnsafe(ref deltas, (nuint)(start + i));
                vectorMax = Vector512.Max(vectorMax, normalized);
            }

            var width = GetBitWidth(GetMaximum(vectorMax));
            bitWidths |= (uint)width << (block * 8);
            packedByteCount += width * 4;
        }

        return bitWidths;
    }

    static uint NormalizeDeltasScalar(ref long deltas, long minDelta, out int packedByteCount)
    {
        uint bitWidths = 0;
        packedByteCount = 0;
        for (var block = 0; block < MiniBlockCount; block++)
        {
            var start = block * MiniBlockSize;
            ulong max = 0;
            for (var i = 0; i < MiniBlockSize; i++)
            {
                ref var delta = ref Unsafe.Add(ref deltas, start + i);
                var normalized = (ulong)(delta - minDelta);
                if (normalized > max)
                    max = normalized;
                delta = (long)normalized;
            }

            var width = GetBitWidth(max);
            bitWidths |= (uint)width << (block * 8);
            packedByteCount += width * 4;
        }

        return bitWidths;
    }

    static ulong GetMaximum(Vector512<ulong> values)
    {
        var result = values.GetElement(0);
        for (var i = 1; i < Vector512<ulong>.Count; i++)
            result = Math.Max(result, values.GetElement(i));
        return result;
    }

    internal static void WritePackedUnsignedValues(ReadOnlySpan<long> values, int bitWidth, ref BufferWriter writer)
    {
        if (bitWidth == 0)
            return;
        if (values.Length != MiniBlockSize)
            throw new ArgumentException(
                "Delta binary packed mini-blocks must contain exactly 32 values.", nameof(values));

        var byteCount = checked((values.Length * bitWidth + 7) >> 3);
        var destination = writer.GetSpan(byteCount);
        PackUnsignedValues(
            ref MemoryMarshal.GetReference(values),
            bitWidth,
            ref MemoryMarshal.GetReference(destination));
        writer.Advance(byteCount);
    }

    static void PackUnsignedValues(ref long input, int bitWidth, ref byte output)
    {
        var mask = bitWidth == 64 ? ulong.MaxValue : (1UL << bitWidth) - 1UL;
        ulong low = 0;
        ulong high = 0;
        var bufferedBits = 0;
        var outputOffset = 0;
        for (var i = 0; i < MiniBlockSize; i++)
        {
            var value = (ulong)Unsafe.Add(ref input, i) & mask;
            if (bufferedBits == 0)
            {
                low = value;
            }
            else
            {
                low |= value << bufferedBits;
                high = value >> (64 - bufferedBits);
            }

            bufferedBits += bitWidth;

            if (bufferedBits >= 64)
            {
                var word = BitConverter.IsLittleEndian ? low : BinaryPrimitives.ReverseEndianness(low);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), word);
                outputOffset += sizeof(ulong);
                low = high;
                high = 0;
                bufferedBits -= 64;
            }
        }

        // A 32-value mini-block has either no remainder (even width) or exactly 32 bits (odd width).
        if (bufferedBits > 0)
        {
            var tail = (uint)low;
            if (!BitConverter.IsLittleEndian)
                tail = BinaryPrimitives.ReverseEndianness(tail);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), tail);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int WriteUnsignedVarInt(ulong value, ref byte destination)
    {
        var offset = 0;
        while (value >= 0x80)
        {
            Unsafe.Add(ref destination, offset++) = (byte)(value | 0x80);
            value >>= 7;
        }

        Unsafe.Add(ref destination, offset++) = (byte)value;
        return offset;
    }

    static int GetUnsignedVarIntByteCount(ulong value)
        => Math.Max(1, (GetBitWidth(value) + 6) / 7);

    static byte GetBitWidth(ulong value)
        => (byte)(64 - BitOperations.LeadingZeroCount(value));

    static ulong ZigZag32(int value)
        => (uint)((value << 1) ^ (value >> 31));

    static ulong ZigZag64(long value)
        => (ulong)((value << 1) ^ (value >> 63));
}
