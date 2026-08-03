using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        var previous = input;
        var index = 1;
        while (index < values.Length)
        {
            var count = Math.Min(BlockSize, values.Length - index);
            var minDelta = long.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var current = Unsafe.Add(ref input, index + i);
                var delta = (long)current - previous;
                previous = current;
                Unsafe.Add(ref deltaBuffer, i) = delta;
                if (delta < minDelta)
                    minDelta = delta;
            }

            for (var i = count; i < BlockSize; i++)
                Unsafe.Add(ref deltaBuffer, i) = minDelta;

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
        var previous = input;
        var index = 1;
        while (index < values.Length)
        {
            var count = Math.Min(BlockSize, values.Length - index);
            var minDelta = long.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var current = Unsafe.Add(ref input, index + i);
                var delta = current - previous;
                previous = current;
                Unsafe.Add(ref deltaBuffer, i) = delta;
                if (delta < minDelta)
                    minDelta = delta;
            }

            for (var i = count; i < BlockSize; i++)
                Unsafe.Add(ref deltaBuffer, i) = minDelta;

            WriteDeltaBlock(ref deltaBuffer, minDelta, ref writer);
            index += count;
        }
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
        uint bitWidths = 0;
        var packedByteCount = 0;
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
