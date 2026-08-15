using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Plank.Schema;

namespace Plank.Writing.Encoding;

/// <summary>
/// Encoding primitives shared by every encoder: LEB128 varints, bit widths, RLE runs and boolean
/// bit packing. Each of these previously existed in two or three encoders with slightly different
/// implementations, so the encoders disagreed on how fast the same operation was.
/// </summary>
static class EncodingPrimitives
{
    /// <summary>Maximum bytes a 32-bit LEB128 varint can occupy.</summary>
    internal const int MaxVarIntByteCount = 5;

    /// <summary>Writes an unsigned LEB128 varint, reserving the whole varint in one call.</summary>
    internal static void WriteUnsignedVarInt(uint value, ref BufferWriter writer)
    {
        var destination = writer.GetSpan(MaxVarIntByteCount);
        var offset = 0;
        while (value >= 0x80)
        {
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[offset++] = (byte)value;
        writer.Advance(offset);
    }

    /// <summary>Writes an unsigned LEB128 varint to raw storage and returns the bytes written.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int WriteUnsignedVarInt(ulong value, ref byte destination)
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

    internal static int GetUnsignedVarIntByteCount(ulong value)
        => Math.Max(1, (GetBitWidth(value) + 6) / 7);

    /// <summary>Bits needed to represent <paramref name="value"/>; 0 for 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetBitWidth(ulong value)
        => 64 - BitOperations.LeadingZeroCount(value);

    /// <summary>Bits needed to represent every value in <c>[0, maxValue]</c>.</summary>
    internal static int GetBitWidthFromMaxValue(int maxValue)
    {
        if (maxValue < 0)
            throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "Maximum value must be non-negative.");

        return GetBitWidth((uint)maxValue);
    }

    /// <summary>
    /// Writes an RLE run: the run length varint with its low bit clear, then the repeated value in
    /// <paramref name="bitWidth"/> rounded up to whole little-endian bytes.
    /// </summary>
    internal static void WriteRleRun(int value, int runLength, int bitWidth, ref BufferWriter writer)
    {
        if (runLength <= 0)
            return;

        WriteUnsignedVarInt(((uint)runLength) << 1, ref writer);
        var byteWidth = (bitWidth + 7) >> 3;
        if (byteWidth == 0)
            return;

        var destination = writer.GetSpan(byteWidth);
        var unsignedValue = unchecked((uint)value);
        for (var i = 0; i < byteWidth; i++)
            destination[i] = (byte)(unsignedValue >> (8 * i));
        writer.Advance(byteWidth);
    }

    /// <summary>
    /// Packs booleans one bit per value, least significant bit first, into
    /// <c>ceil(values.Length / 8)</c> bytes. The caller must size the destination.
    /// </summary>
    internal static void PackBooleans(ReadOnlySpan<bool> values, Span<byte> destination)
    {
        var sourceBytes = MemoryMarshal.AsBytes(values);
        var fullByteCount = values.Length >> 3;
        var valueIndex = 0;
        var byteIndex = 0;

        if (BitConverter.IsLittleEndian && Vector512.IsHardwareAccelerated && Vector512<byte>.IsSupported)
        {
            var vectorValueCount = sourceBytes.Length / Vector512<byte>.Count;
            ref var source = ref MemoryMarshal.GetReference(sourceBytes);
            for (var i = 0; i < vectorValueCount; i++)
            {
                var isFalse = Vector512.Equals(Vector512.LoadUnsafe(ref source), Vector512<byte>.Zero);
                var mask = (ulong)~isFalse.ExtractMostSignificantBits();
                Unsafe.WriteUnaligned(ref destination[byteIndex], mask);
                byteIndex += sizeof(ulong);
                source = ref Unsafe.Add(ref source, Vector512<byte>.Count);
            }

            valueIndex = vectorValueCount * Vector512<byte>.Count;
        }
        else if (BitConverter.IsLittleEndian && Vector256.IsHardwareAccelerated && Vector256<byte>.IsSupported)
        {
            var vectorValueCount = sourceBytes.Length / Vector256<byte>.Count;
            ref var source = ref MemoryMarshal.GetReference(sourceBytes);
            for (var i = 0; i < vectorValueCount; i++)
            {
                var isFalse = Vector256.Equals(Vector256.LoadUnsafe(ref source), Vector256<byte>.Zero);
                var mask = (uint)~isFalse.ExtractMostSignificantBits();
                Unsafe.WriteUnaligned(ref destination[byteIndex], mask);
                byteIndex += sizeof(uint);
                source = ref Unsafe.Add(ref source, Vector256<byte>.Count);
            }

            valueIndex = vectorValueCount * Vector256<byte>.Count;
        }
        else if (Sse2.IsSupported)
        {
            var simdValueCount = sourceBytes.Length & ~15;
            for (; valueIndex < simdValueCount; valueIndex += 16)
            {
                var chunk = MemoryMarshal.Read<Vector128<byte>>(sourceBytes[valueIndex..]);
                var gtZero = Sse2.CompareGreaterThan(chunk.AsSByte(), Vector128<sbyte>.Zero);
                var mask = Sse2.MoveMask(gtZero);
                destination[byteIndex] = (byte)mask;
                destination[byteIndex + 1] = (byte)(mask >> 8);
                byteIndex += 2;
            }
        }

        for (; byteIndex < fullByteCount; byteIndex++)
        {
            var packed =
                (values[valueIndex] ? 1 : 0) |
                ((values[valueIndex + 1] ? 1 : 0) << 1) |
                ((values[valueIndex + 2] ? 1 : 0) << 2) |
                ((values[valueIndex + 3] ? 1 : 0) << 3) |
                ((values[valueIndex + 4] ? 1 : 0) << 4) |
                ((values[valueIndex + 5] ? 1 : 0) << 5) |
                ((values[valueIndex + 6] ? 1 : 0) << 6) |
                ((values[valueIndex + 7] ? 1 : 0) << 7);
            destination[byteIndex] = (byte)packed;
            valueIndex += 8;
        }

        var tailCount = values.Length - valueIndex;
        if (tailCount > 0)
        {
            var packed = 0;
            for (var bit = 0; bit < tailCount; bit++)
                packed |= (values[valueIndex + bit] ? 1 : 0) << bit;
            destination[byteIndex] = (byte)packed;
        }
    }

    /// <summary>Validated fixed payload length for a FIXED_LEN_BYTE_ARRAY column.</summary>
    internal static int GetFixedLength(Column column)
    {
        var valueLength = column.Options.TypeLength;
        if (valueLength == 0)
            throw new InvalidOperationException(
                $"Column '{column.Name}' is '{ParquetPhysicalType.FixedLenByteArray}' and requires a positive '{nameof(ColumnOptions.TypeLength)}'.");
        if (valueLength > int.MaxValue)
            throw new InvalidOperationException(
                $"Column '{column.Name}' fixed length ({valueLength}) exceeds supported maximum of {int.MaxValue}.");

        return checked((int)valueLength);
    }
}
