using System.Runtime.CompilerServices;
using Plank.Schema;

namespace Plank.Writing.Encoding;

static class DeltaBinaryPackedEncoding
{
    const int BlockSize = 128;
    const int MiniBlockCount = 4;
    const int MiniBlockSize = BlockSize / MiniBlockCount;

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
        WriteUnsignedVarInt(BlockSize, ref writer);
        WriteUnsignedVarInt(MiniBlockCount, ref writer);
        WriteUnsignedVarInt((ulong)values.Length, ref writer);

        if (values.Length == 0)
        {
            WriteUnsignedVarInt(0, ref writer);
            return;
        }

        WriteUnsignedVarInt(ZigZag32(values[0]), ref writer);
        if (values.Length == 1)
            return;

        Span<long> deltas = stackalloc long[BlockSize];
        var previous = values[0];
        var index = 1;
        while (index < values.Length)
        {
            var count = Math.Min(BlockSize, values.Length - index);
            var minDelta = long.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var current = values[index + i];
                var delta = (long)current - previous;
                previous = current;
                deltas[i] = delta;
                if (delta < minDelta)
                    minDelta = delta;
            }

            for (var i = count; i < BlockSize; i++)
                deltas[i] = minDelta;

            WriteDeltaBlock(deltas, minDelta, ref writer);
            index += count;
        }
    }

    internal static void WriteInt64(ReadOnlySpan<long> values, ref BufferWriter writer)
    {
        WriteUnsignedVarInt(BlockSize, ref writer);
        WriteUnsignedVarInt(MiniBlockCount, ref writer);
        WriteUnsignedVarInt((ulong)values.Length, ref writer);

        if (values.Length == 0)
        {
            WriteUnsignedVarInt(0, ref writer);
            return;
        }

        WriteUnsignedVarInt(ZigZag64(values[0]), ref writer);
        if (values.Length == 1)
            return;

        Span<long> deltas = stackalloc long[BlockSize];
        var previous = values[0];
        var index = 1;
        while (index < values.Length)
        {
            var count = Math.Min(BlockSize, values.Length - index);
            var minDelta = long.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var current = values[index + i];
                var delta = current - previous;
                previous = current;
                deltas[i] = delta;
                if (delta < minDelta)
                    minDelta = delta;
            }

            for (var i = count; i < BlockSize; i++)
                deltas[i] = minDelta;

            WriteDeltaBlock(deltas, minDelta, ref writer);
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

        throw new InvalidOperationException(
            $"Column '{column.Name}' expects '{ParquetPhysicalType.Int64}' values, but got '{typeof(T)}'.");
    }

    static void WriteDeltaBlock(Span<long> deltas, long minDelta, ref BufferWriter writer)
    {
        WriteUnsignedVarInt(ZigZag64(minDelta), ref writer);

        Span<byte> bitWidths = stackalloc byte[MiniBlockCount];
        for (var block = 0; block < MiniBlockCount; block++)
        {
            var start = block * MiniBlockSize;
            ulong max = 0;
            for (var i = 0; i < MiniBlockSize; i++)
            {
                var normalized = (ulong)(deltas[start + i] - minDelta);
                if (normalized > max)
                    max = normalized;
                deltas[start + i] = (long)normalized;
            }

            bitWidths[block] = GetBitWidth(max);
        }

        var bitWidthBytes = writer.GetSpan(MiniBlockCount);
        bitWidths.CopyTo(bitWidthBytes);
        writer.Advance(MiniBlockCount);

        for (var block = 0; block < MiniBlockCount; block++)
        {
            var width = bitWidths[block];
            WritePackedUnsignedValues(deltas[(block * MiniBlockSize)..((block + 1) * MiniBlockSize)], width, ref writer);
        }
    }

    static void WritePackedUnsignedValues(ReadOnlySpan<long> values, int bitWidth, ref BufferWriter writer)
    {
        if (bitWidth == 0)
            return;

        var byteCount = checked((values.Length * bitWidth + 7) >> 3);
        var destination = writer.GetSpan(byteCount);
        destination[..byteCount].Clear();
        var bitOffset = 0;
        var mask = bitWidth == 64 ? ulong.MaxValue : (1UL << bitWidth) - 1UL;
        for (var i = 0; i < values.Length; i++)
        {
            var value = (ulong)values[i] & mask;
            var remainingBits = bitWidth;
            while (remainingBits > 0)
            {
                var byteIndex = bitOffset >> 3;
                var bitIndex = bitOffset & 7;
                var byteRemainingBits = 8 - bitIndex;
                var bitsToWrite = Math.Min(byteRemainingBits, remainingBits);
                var chunkMask = (1UL << bitsToWrite) - 1UL;
                var chunk = (byte)(value & chunkMask);
                destination[byteIndex] |= (byte)(chunk << bitIndex);
                value >>= bitsToWrite;
                bitOffset += bitsToWrite;
                remainingBits -= bitsToWrite;
            }
        }

        writer.Advance(byteCount);
    }

    static void WriteUnsignedVarInt(ulong value, ref BufferWriter writer)
    {
        var destination = writer.GetSpan(10);
        var offset = 0;
        while (value >= 0x80)
        {
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[offset++] = (byte)value;
        writer.Advance(offset);
    }

    static byte GetBitWidth(ulong value)
    {
        if (value == 0)
            return 0;

        byte width = 0;
        while (value != 0)
        {
            width++;
            value >>= 1;
        }

        return width;
    }

    static ulong ZigZag32(int value)
        => (uint)((value << 1) ^ (value >> 31));

    static ulong ZigZag64(long value)
        => (ulong)((value << 1) ^ (value >> 63));
}
