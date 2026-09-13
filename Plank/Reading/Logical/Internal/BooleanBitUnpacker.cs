using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Plank.Reading.Logical.Internal;

/// <summary>
/// Expands a plain-encoded boolean bitmap into one <see cref="bool"/> per value.
/// </summary>
/// <remarks>
/// Plain booleans are the densest thing Parquet stores — one bit each — so a page holds eight times
/// the values of any other fixed-width page and the expansion loop runs eight times as often. Reading
/// a bit at a time costs about 2.8 cycles per value; a table lookup expands a whole byte at once, and
/// a vector shuffle expands four or eight bytes at once.
/// </remarks>
static class BooleanBitUnpacker
{
    /// <summary>Each entry holds the eight bytes that one packed byte expands to, LSB first.</summary>
    static readonly ulong[] ExpandedBooleanBytes = CreateExpandedBooleanBytes();

    internal static void Unpack(ReadOnlySpan<byte> payload, int bitOffset, Span<bool> destination)
    {
        // Checking the last bit index once is what lets every step below add unchecked.
        _ = checked(bitOffset + destination.Length);
        var index = 0;

        // Align to a byte boundary first: every wide path below reads whole packed bytes.
        var leading = Math.Min((8 - (bitOffset & 7)) & 7, destination.Length);
        for (; index < leading; index++)
            destination[index] = ReadBit(payload, bitOffset + index);

        var sourceIndex = (bitOffset + index) >> 3;
        var remaining = destination.Length - index;
        if (remaining >= 8)
        {
            var consumed = UnpackAligned(payload[sourceIndex..], destination.Slice(index, remaining));
            index += consumed;
        }

        for (; index < destination.Length; index++)
            destination[index] = ReadBit(payload, bitOffset + index);
    }

    /// <summary>
    /// Expands whole packed bytes from the start of <paramref name="payload"/> and returns how many
    /// values were written. The caller finishes the remainder a bit at a time.
    /// </summary>
    static int UnpackAligned(ReadOnlySpan<byte> payload, Span<bool> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(payload);
        ref var output = ref Unsafe.As<bool, byte>(ref MemoryMarshal.GetReference(destination));
        var index = 0;

        if (Vector512.IsHardwareAccelerated && destination.Length >= Vector512<byte>.Count)
        {
            var bits = Vector512.Create(Vector128.Create(
                (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128));
            var ones = Vector512.Create((byte)1);
            var last = destination.Length - Vector512<byte>.Count;
            for (; index <= last; index += Vector512<byte>.Count)
            {
                var packed = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, index >> 3));
                // Constant indices select bytes within each 128-bit lane on AVX-512.
                var spreadBytes = Vector512.ShuffleNative(Vector512.Create(packed).AsByte(),
                    Vector512.Create(
                        (byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
                        18, 18, 18, 18, 18, 18, 18, 18, 19, 19, 19, 19, 19, 19, 19, 19,
                        36, 36, 36, 36, 36, 36, 36, 36, 37, 37, 37, 37, 37, 37, 37, 37,
                        54, 54, 54, 54, 54, 54, 54, 54, 55, 55, 55, 55, 55, 55, 55, 55));
                (Vector512.Equals(spreadBytes & bits, bits) & ones).StoreUnsafe(
                    ref Unsafe.Add(ref output, index));
            }
        }
        else if (Vector256.IsHardwareAccelerated && destination.Length >= Vector256<byte>.Count)
        {
            var bits = Vector256.Create(Vector128.Create(
                (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128));
            var ones = Vector256.Create((byte)1);
            var last = destination.Length - Vector256<byte>.Count;
            for (; index <= last; index += Vector256<byte>.Count)
            {
                var packed = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, index >> 3));
                // Keep the indices constant here so the JIT selects a lane-local shuffle.
                var spreadBytes = Vector256.ShuffleNative(Vector256.Create(packed).AsByte(),
                    Vector256.Create(
                        (byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
                        18, 18, 18, 18, 18, 18, 18, 18, 19, 19, 19, 19, 19, 19, 19, 19));
                (Vector256.Equals(spreadBytes & bits, bits) & ones).StoreUnsafe(
                    ref Unsafe.Add(ref output, index));
            }
        }
        else if (Vector128.IsHardwareAccelerated && destination.Length >= Vector128<byte>.Count)
        {
            // Cross-platform 128-bit form for ARM64 and pre-AVX2 x86, which otherwise expand a byte
            // at a time through the lookup table below. Vector128.ShuffleNative is pshufb on x86 and tbl on
            // AdvSimd; broadcasting the two packed bytes into every lane lets the control pick which
            // of them each group of eight booleans comes from.
            var spread = Vector128.Create(
                (byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
            var bits = Vector128.Create(
                (byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
            var ones = Vector128.Create((byte)1);
            var last = destination.Length - Vector128<byte>.Count;
            for (; index <= last; index += Vector128<byte>.Count)
            {
                var packed = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, index >> 3));
                var spreadBytes = Vector128.ShuffleNative(Vector128.Create(packed).AsByte(), spread);
                (Vector128.Equals(spreadBytes & bits, bits) & ones).StoreUnsafe(
                    ref Unsafe.Add(ref output, index));
            }
        }

        var lastByte = destination.Length - 8;
        for (; index <= lastByte; index += 8)
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, index),
                ExpandedBooleanBytes[Unsafe.Add(ref source, index >> 3)]);

        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ReadBit(ReadOnlySpan<byte> payload, int bitIndex)
        => ((payload[bitIndex >> 3] >> (bitIndex & 7)) & 1) != 0;

    static ulong[] CreateExpandedBooleanBytes()
    {
        var table = new ulong[256];
        for (var value = 0; value < table.Length; value++)
        {
            var expanded = 0UL;
            for (var bit = 0; bit < 8; bit++)
                expanded |= (ulong)((value >> bit) & 1) << (bit * 8);
            table[value] = expanded;
        }
        return table;
    }
}
