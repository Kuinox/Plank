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
    const int Packed13BitMiniBlockByteCount = MiniBlockSize * 13 / 8;
    const uint Four13BitMiniBlocks = 0x0D0D0D0D;
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
        var headerLength = EncodingPrimitives.WriteUnsignedVarInt(BlockSize, ref headerStart);
        headerLength += EncodingPrimitives.WriteUnsignedVarInt(MiniBlockCount, ref Unsafe.Add(ref headerStart, headerLength));
        headerLength += EncodingPrimitives.WriteUnsignedVarInt((ulong)values.Length, ref Unsafe.Add(ref headerStart, headerLength));

        if (values.Length == 0)
        {
            headerLength += EncodingPrimitives.WriteUnsignedVarInt(0, ref Unsafe.Add(ref headerStart, headerLength));
            writer.Advance(headerLength);
            return;
        }

        ref var input = ref MemoryMarshal.GetReference(values);
        headerLength += EncodingPrimitives.WriteUnsignedVarInt(ZigZag32(input), ref Unsafe.Add(ref headerStart, headerLength));
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
        var headerLength = EncodingPrimitives.WriteUnsignedVarInt(BlockSize, ref headerStart);
        headerLength += EncodingPrimitives.WriteUnsignedVarInt(MiniBlockCount, ref Unsafe.Add(ref headerStart, headerLength));
        headerLength += EncodingPrimitives.WriteUnsignedVarInt((ulong)values.Length, ref Unsafe.Add(ref headerStart, headerLength));

        if (values.Length == 0)
        {
            headerLength += EncodingPrimitives.WriteUnsignedVarInt(0, ref Unsafe.Add(ref headerStart, headerLength));
            writer.Advance(headerLength);
            return;
        }

        ref var input = ref MemoryMarshal.GetReference(values);
        headerLength += EncodingPrimitives.WriteUnsignedVarInt(ZigZag64(input), ref Unsafe.Add(ref headerStart, headerLength));
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
            WriteDeltaBlock(ref deltaBuffer, minDelta, ref writer);
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
        else if (Avx2.IsSupported)
        {
            i = PrepareInt32BlockAvx2(ref input, inputOffset, count, ref deltas, out minDelta);
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
        else if (Vector256.IsHardwareAccelerated)
        {
            i = PrepareInt64BlockVector256(ref input, inputOffset, count, ref deltas, out minDelta);
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
        else if (Vector256.IsHardwareAccelerated)
        {
            var fill = Vector256.Create(minDelta);
            for (; i <= BlockSize - Vector256<long>.Count; i += Vector256<long>.Count)
                fill.StoreUnsafe(ref deltas, (nuint)i);
        }

        for (; i < BlockSize; i++)
            Unsafe.Add(ref deltas, i) = minDelta;
    }

    /// <summary>
    /// Widening eight Int32s to Int64 has no AVX2 equivalent of the AVX-512 convert, so the block is
    /// walked four values at a time with <c>vpmovsxdq</c> instead. Kept out of line so that adding it
    /// leaves the AVX-512 loop's code generation alone.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int PrepareInt32BlockAvx2(ref int input, int inputOffset, int count, ref long deltas,
        out long minDelta)
    {
        var vectorMin = Vector256.Create(long.MaxValue);
        var i = 0;
        for (; i <= count - Vector256<long>.Count; i += Vector256<long>.Count)
        {
            var current = Avx2.ConvertToVector256Int64(
                Vector128.LoadUnsafe(ref input, (nuint)(inputOffset + i)));
            var previous = Avx2.ConvertToVector256Int64(
                Vector128.LoadUnsafe(ref input, (nuint)(inputOffset + i - 1)));
            var delta = Vector256.Subtract(current, previous);
            delta.StoreUnsafe(ref deltas, (nuint)i);
            vectorMin = Vector256.Min(vectorMin, delta);
        }

        minDelta = i == 0 ? long.MaxValue : GetMinimum(vectorMin);
        return i;
    }

    /// <inheritdoc cref="PrepareInt32BlockAvx2" />
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int PrepareInt64BlockVector256(ref long input, int inputOffset, int count, ref long deltas,
        out long minDelta)
    {
        var vectorMin = Vector256.Create(long.MaxValue);
        var i = 0;
        for (; i <= count - Vector256<long>.Count; i += Vector256<long>.Count)
        {
            var current = Vector256.LoadUnsafe(ref input, (nuint)(inputOffset + i));
            var previous = Vector256.LoadUnsafe(ref input, (nuint)(inputOffset + i - 1));
            var delta = Vector256.Subtract(current, previous);
            delta.StoreUnsafe(ref deltas, (nuint)i);
            vectorMin = Vector256.Min(vectorMin, delta);
        }

        minDelta = i == 0 ? long.MaxValue : GetMinimum(vectorMin);
        return i;
    }

    static long GetMinimum(Vector256<long> values)
    {
        var lower = Vector128.Min(values.GetLower(), values.GetUpper());
        return Math.Min(lower.GetElement(0), lower.GetElement(1));
    }

    static ulong GetMaximum(Vector256<ulong> values)
    {
        var lower = Vector128.Max(values.GetLower(), values.GetUpper());
        return Math.Max(lower.GetElement(0), lower.GetElement(1));
    }

    static long GetMinimum(Vector512<long> values)
    {
        var lowerWidth = Vector256.Min(values.GetLower(), values.GetUpper());
        var lowestWidth = Vector128.Min(lowerWidth.GetLower(), lowerWidth.GetUpper());
        return Math.Min(lowestWidth.GetElement(0), lowestWidth.GetElement(1));
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

    /// <summary>
    /// Writes one delta block. Both the Int32 and Int64 writers widen their deltas to
    /// <see cref="long"/> before calling this, so a single implementation serves both.
    /// </summary>
    static void WriteDeltaBlock(ref long deltas, long minDelta, ref BufferWriter writer)
    {
        var bitWidths = Avx512F.IsSupported
            ? NormalizeDeltasVectorized(ref deltas, minDelta, out var packedByteCount)
            : Vector256.IsHardwareAccelerated
                ? NormalizeDeltasVector256(ref deltas, minDelta, out packedByteCount)
                : NormalizeDeltasScalar(ref deltas, minDelta, out packedByteCount);

        var encodedMinDelta = ZigZag64(minDelta);
        var outputLength = EncodingPrimitives.GetUnsignedVarIntByteCount(encodedMinDelta) + MiniBlockCount + packedByteCount;
        var destination = writer.GetSpan(outputLength);
        ref var output = ref MemoryMarshal.GetReference(destination);
        var outputOffset = EncodingPrimitives.WriteUnsignedVarInt(encodedMinDelta, ref output);
        var bitWidthBytes = BitConverter.IsLittleEndian ? bitWidths : BinaryPrimitives.ReverseEndianness(bitWidths);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), bitWidthBytes);
        outputOffset += MiniBlockCount;

        if (bitWidths == Four13BitMiniBlocks)
        {
            Pack13BitDeltaBlock(ref deltas, ref Unsafe.Add(ref output, outputOffset));
        }
        else if ((((bitWidths | 0x80808080U) - 0x05050505U) & 0x80808080U) == 0)
        {
            PackNarrowDeltaBlock(ref deltas, bitWidths, ref Unsafe.Add(ref output, outputOffset));
        }
        else if (Contains9To12BitWidth(bitWidths))
        {
            PackMediumDeltaBlock(ref deltas, bitWidths, ref Unsafe.Add(ref output, outputOffset));
        }
        else
        {
            PackGenericDeltaBlock(ref deltas, bitWidths, ref Unsafe.Add(ref output, outputOffset));
        }

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
    static void PackMediumDeltaBlock(ref long deltas, uint bitWidths, ref byte output)
    {
        var outputOffset = 0;
        for (var block = 0; block < MiniBlockCount; block++)
        {
            ref var blockInput = ref Unsafe.Add(ref deltas, block * MiniBlockSize);
            var width = (byte)(bitWidths >> (block * 8));
            if (width == 0)
                continue;

            var byteCount = width * 4;
            ref var blockOutput = ref Unsafe.Add(ref output, outputOffset);
            switch (width)
            {
                case 1:
                    PackInt64Width1(ref blockInput, ref blockOutput);
                    break;
                case 2:
                    PackInt64Width2(ref blockInput, ref blockOutput);
                    break;
                case 3:
                    PackInt64Width3(ref blockInput, ref blockOutput);
                    break;
                case 4:
                    PackInt64Width4(ref blockInput, ref blockOutput);
                    break;
                case 9:
                    Pack9BitUnsignedValues(ref blockInput, ref blockOutput);
                    break;
                case 10:
                    Pack10BitUnsignedValues(ref blockInput, ref blockOutput);
                    break;
                case 11 or 12:
                    Pack9To12BitUnsignedValues(ref blockInput, width, ref blockOutput);
                    break;
                case 13:
                    Pack13BitUnsignedValues(ref blockInput, ref blockOutput);
                    break;
                default:
                    PackUnsignedValues(ref blockInput, width, ref blockOutput);
                    break;
            }
            outputOffset += byteCount;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Contains9To12BitWidth(uint bitWidths)
    {
        const uint byteHighBits = 0x80808080;
        var biasedWidths = bitWidths | byteHighBits;
        var atLeast9 = (biasedWidths - 0x09090909) & byteHighBits;
        var below13 = ~(biasedWidths - 0x0D0D0D0D) & byteHighBits;
        return (atLeast9 & below13) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Pack9BitUnsignedValues(ref long input, ref byte output)
    {
        const ulong mask = (1UL << 9) - 1;
        for (var group = 0; group < 4; group++)
        {
            var inputOffset = group * 8;
            var value0 = (ulong)Unsafe.Add(ref input, inputOffset) & mask;
            var value1 = (ulong)Unsafe.Add(ref input, inputOffset + 1) & mask;
            var value2 = (ulong)Unsafe.Add(ref input, inputOffset + 2) & mask;
            var value3 = (ulong)Unsafe.Add(ref input, inputOffset + 3) & mask;
            var value4 = (ulong)Unsafe.Add(ref input, inputOffset + 4) & mask;
            var value5 = (ulong)Unsafe.Add(ref input, inputOffset + 5) & mask;
            var value6 = (ulong)Unsafe.Add(ref input, inputOffset + 6) & mask;
            var value7 = (ulong)Unsafe.Add(ref input, inputOffset + 7) & mask;

            var low = value0 |
                      value1 << 9 |
                      value2 << 18 |
                      value3 << 27 |
                      value4 << 36 |
                      value5 << 45 |
                      value6 << 54 |
                      value7 << 63;
            if (!BitConverter.IsLittleEndian)
                low = BinaryPrimitives.ReverseEndianness(low);

            var outputOffset = group * 9;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), low);
            Unsafe.Add(ref output, outputOffset + 8) = (byte)(value7 >> 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Pack10BitUnsignedValues(ref long input, ref byte output)
    {
        const ulong mask = (1UL << 10) - 1;
        for (var group = 0; group < 4; group++)
        {
            var inputOffset = group * 8;
            var value0 = (ulong)Unsafe.Add(ref input, inputOffset) & mask;
            var value1 = (ulong)Unsafe.Add(ref input, inputOffset + 1) & mask;
            var value2 = (ulong)Unsafe.Add(ref input, inputOffset + 2) & mask;
            var value3 = (ulong)Unsafe.Add(ref input, inputOffset + 3) & mask;
            var value4 = (ulong)Unsafe.Add(ref input, inputOffset + 4) & mask;
            var value5 = (ulong)Unsafe.Add(ref input, inputOffset + 5) & mask;
            var value6 = (ulong)Unsafe.Add(ref input, inputOffset + 6) & mask;
            var value7 = (ulong)Unsafe.Add(ref input, inputOffset + 7) & mask;

            var low = value0 |
                      value1 << 10 |
                      value2 << 20 |
                      value3 << 30 |
                      value4 << 40 |
                      value5 << 50 |
                      value6 << 60;
            var high = (ushort)(value6 >> 4 | value7 << 6);
            if (!BitConverter.IsLittleEndian)
            {
                low = BinaryPrimitives.ReverseEndianness(low);
                high = BinaryPrimitives.ReverseEndianness(high);
            }

            var outputOffset = group * 10;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), low);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset + 8), high);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Pack9To12BitUnsignedValues(ref long input, int bitWidth, ref byte output)
    {
        for (var group = 0; group < 4; group++)
        {
            ref var groupInput = ref Unsafe.Add(ref input, group * 8);
            UInt128 packed = (ulong)groupInput;
            packed |= (UInt128)(ulong)Unsafe.Add(ref groupInput, 1) << bitWidth;
            packed |= (UInt128)(ulong)Unsafe.Add(ref groupInput, 2) << (bitWidth * 2);
            packed |= (UInt128)(ulong)Unsafe.Add(ref groupInput, 3) << (bitWidth * 3);
            packed |= (UInt128)(ulong)Unsafe.Add(ref groupInput, 4) << (bitWidth * 4);
            packed |= (UInt128)(ulong)Unsafe.Add(ref groupInput, 5) << (bitWidth * 5);
            packed |= (UInt128)(ulong)Unsafe.Add(ref groupInput, 6) << (bitWidth * 6);
            packed |= (UInt128)(ulong)Unsafe.Add(ref groupInput, 7) << (bitWidth * 7);

            ref var groupOutput = ref Unsafe.Add(ref output, group * bitWidth);
            var low = (ulong)packed;
            if (!BitConverter.IsLittleEndian)
                low = BinaryPrimitives.ReverseEndianness(low);
            Unsafe.WriteUnaligned(ref groupOutput, low);
            WriteLittleEndianBytes((ulong)(packed >> 64), bitWidth - sizeof(ulong),
                ref Unsafe.Add(ref groupOutput, sizeof(ulong)));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void WriteLittleEndianBytes(ulong value, int byteCount, ref byte output)
    {
        switch (byteCount)
        {
            case 1:
                output = (byte)value;
                return;
            case 2:
            {
                var word = (ushort)value;
                if (!BitConverter.IsLittleEndian)
                    word = BinaryPrimitives.ReverseEndianness(word);
                Unsafe.WriteUnaligned(ref output, word);
                return;
            }
            case 3:
            {
                var word = (ushort)value;
                if (!BitConverter.IsLittleEndian)
                    word = BinaryPrimitives.ReverseEndianness(word);
                Unsafe.WriteUnaligned(ref output, word);
                Unsafe.Add(ref output, 2) = (byte)(value >> 16);
                return;
            }
            case 4:
            {
                var word = (uint)value;
                if (!BitConverter.IsLittleEndian)
                    word = BinaryPrimitives.ReverseEndianness(word);
                Unsafe.WriteUnaligned(ref output, word);
                return;
            }
            default:
                throw new InvalidOperationException($"Unexpected byte count {byteCount}.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void Pack13BitDeltaBlock(ref long deltas, ref byte output)
    {
        for (var block = 0; block < MiniBlockCount; block++)
        {
            Pack13BitUnsignedValues(
                ref Unsafe.Add(ref deltas, block * MiniBlockSize),
                ref Unsafe.Add(ref output, block * Packed13BitMiniBlockByteCount));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void PackNarrowDeltaBlock(ref long input, uint bitWidths, ref byte output)
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

            var width = EncodingPrimitives.GetBitWidth(GetMaximum(vectorMax));
            bitWidths |= (uint)width << (block * 8);
            packedByteCount += width * 4;
        }

        return bitWidths;
    }

    /// <inheritdoc cref="PrepareInt32BlockAvx2" />
    [MethodImpl(MethodImplOptions.NoInlining)]
    static uint NormalizeDeltasVector256(ref long deltas, long minDelta, out int packedByteCount)
    {
        uint bitWidths = 0;
        packedByteCount = 0;
        var vectorMinDelta = Vector256.Create(minDelta);
        for (var block = 0; block < MiniBlockCount; block++)
        {
            var start = block * MiniBlockSize;
            var vectorMax = Vector256<ulong>.Zero;
            for (var i = 0; i < MiniBlockSize; i += Vector256<long>.Count)
            {
                var delta = Vector256.LoadUnsafe(ref deltas, (nuint)(start + i));
                var normalized = Vector256.Subtract(delta, vectorMinDelta).AsUInt64();
                normalized.AsInt64().StoreUnsafe(ref deltas, (nuint)(start + i));
                vectorMax = Vector256.Max(vectorMax, normalized);
            }

            var width = EncodingPrimitives.GetBitWidth(GetMaximum(vectorMax));
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

            var width = EncodingPrimitives.GetBitWidth(max);
            bitWidths |= (uint)width << (block * 8);
            packedByteCount += width * 4;
        }

        return bitWidths;
    }

    static ulong GetMaximum(Vector512<ulong> values)
    {
        var lowerWidth = Vector256.Max(values.GetLower(), values.GetUpper());
        var lowestWidth = Vector128.Max(lowerWidth.GetLower(), lowerWidth.GetUpper());
        return Math.Max(lowestWidth.GetElement(0), lowestWidth.GetElement(1));
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
        ref var input = ref MemoryMarshal.GetReference(values);
        ref var output = ref MemoryMarshal.GetReference(destination);
        if (bitWidth == 13)
            Pack13BitUnsignedValues(ref input, ref output);
        else
            PackUnsignedValues(ref input, bitWidth, ref output);
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
    static void Pack13BitUnsignedValues(ref long input, ref byte output)
    {
        const ulong mask = (1UL << 13) - 1;
        for (var group = 0; group < 4; group++)
        {
            var inputOffset = group * 8;
            var value0 = (ulong)Unsafe.Add(ref input, inputOffset) & mask;
            var value1 = (ulong)Unsafe.Add(ref input, inputOffset + 1) & mask;
            var value2 = (ulong)Unsafe.Add(ref input, inputOffset + 2) & mask;
            var value3 = (ulong)Unsafe.Add(ref input, inputOffset + 3) & mask;
            var value4 = (ulong)Unsafe.Add(ref input, inputOffset + 4) & mask;
            var value5 = (ulong)Unsafe.Add(ref input, inputOffset + 5) & mask;
            var value6 = (ulong)Unsafe.Add(ref input, inputOffset + 6) & mask;
            var value7 = (ulong)Unsafe.Add(ref input, inputOffset + 7) & mask;

            var low = value0 |
                      value1 << 13 |
                      value2 << 26 |
                      value3 << 39 |
                      value4 << 52;
            var high = value4 >> 12 |
                       value5 << 1 |
                       value6 << 14 |
                       value7 << 27;
            var highWord = (uint)high;
            var highTail = (byte)(high >> 32);

            if (!BitConverter.IsLittleEndian)
            {
                low = BinaryPrimitives.ReverseEndianness(low);
                highWord = BinaryPrimitives.ReverseEndianness(highWord);
            }

            var outputOffset = group * 13;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), low);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset + 8), highWord);
            Unsafe.Add(ref output, outputOffset + 12) = highTail;
        }
    }

    static ulong ZigZag32(int value)
        => (uint)((value << 1) ^ (value >> 31));

    static ulong ZigZag64(long value)
        => (ulong)((value << 1) ^ (value >> 63));
}
