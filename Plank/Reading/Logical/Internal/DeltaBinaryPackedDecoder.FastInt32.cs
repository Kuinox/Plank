// Generated from DeltaBinaryPackedDecoder.FastInt32.tt. Run:
// dotnet t4 Plank/Reading/Logical/Internal/DeltaBinaryPackedDecoder.FastInt32.tt -o Plank/Reading/Logical/Internal/DeltaBinaryPackedDecoder.FastInt32.cs
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Plank.Reading.Logical.Internal;

static partial class DeltaBinaryPackedDecoder
{
    static void DecodeInt32MiniBlockFast(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<int> destination)
    {
        switch (bitWidth)
        {
            case 1: DecodeInt32MiniBlockFastW1(packed, minDelta, ref previous, destination); return;
            case 2: DecodeInt32MiniBlockFastW2(packed, minDelta, ref previous, destination); return;
            case 3: DecodeInt32MiniBlockFastW3(packed, minDelta, ref previous, destination); return;
            case 4: DecodeInt32MiniBlockFastW4(packed, minDelta, ref previous, destination); return;
            case 5: DecodeInt32MiniBlockFastW5(packed, minDelta, ref previous, destination); return;
            case 6: DecodeInt32MiniBlockFastW6(packed, minDelta, ref previous, destination); return;
            case 7: DecodeInt32MiniBlockFastW7(packed, minDelta, ref previous, destination); return;
            case 8: DecodeInt32MiniBlockFastW8(packed, minDelta, ref previous, destination); return;
            case 9: DecodeInt32MiniBlockFastW9(packed, minDelta, ref previous, destination); return;
            case 10: DecodeInt32MiniBlockFastW10(packed, minDelta, ref previous, destination); return;
            case 11: DecodeInt32MiniBlockFastW11(packed, minDelta, ref previous, destination); return;
            case 12: DecodeInt32MiniBlockFastW12(packed, minDelta, ref previous, destination); return;
            case 13: DecodeInt32MiniBlockFastW13(packed, minDelta, ref previous, destination); return;
            case 14: DecodeInt32MiniBlockFastW14(packed, minDelta, ref previous, destination); return;
            case 15: DecodeInt32MiniBlockFastW15(packed, minDelta, ref previous, destination); return;
            case 16: DecodeInt32MiniBlockFastW16(packed, minDelta, ref previous, destination); return;
            case 17: DecodeInt32MiniBlockFastW17(packed, minDelta, ref previous, destination); return;
            case 18: DecodeInt32MiniBlockFastW18(packed, minDelta, ref previous, destination); return;
            case 19: DecodeInt32MiniBlockFastW19(packed, minDelta, ref previous, destination); return;
            case 20: DecodeInt32MiniBlockFastW20(packed, minDelta, ref previous, destination); return;
            case 21: DecodeInt32MiniBlockFastW21(packed, minDelta, ref previous, destination); return;
            case 22: DecodeInt32MiniBlockFastW22(packed, minDelta, ref previous, destination); return;
            case 23: DecodeInt32MiniBlockFastW23(packed, minDelta, ref previous, destination); return;
            case 24: DecodeInt32MiniBlockFastW24(packed, minDelta, ref previous, destination); return;
            case 25: DecodeInt32MiniBlockFastW25(packed, minDelta, ref previous, destination); return;
            case 26: DecodeInt32MiniBlockFastW26(packed, minDelta, ref previous, destination); return;
            case 27: DecodeInt32MiniBlockFastW27(packed, minDelta, ref previous, destination); return;
            case 28: DecodeInt32MiniBlockFastW28(packed, minDelta, ref previous, destination); return;
            case 29: DecodeInt32MiniBlockFastW29(packed, minDelta, ref previous, destination); return;
            case 30: DecodeInt32MiniBlockFastW30(packed, minDelta, ref previous, destination); return;
            case 31: DecodeInt32MiniBlockFastW31(packed, minDelta, ref previous, destination); return;
            case 32: DecodeInt32MiniBlockFastW32(packed, minDelta, ref previous, destination); return;
            case 33: DecodeInt32MiniBlockFastW33(packed, minDelta, ref previous, destination); return;
            case 34: DecodeInt32MiniBlockFastW34(packed, minDelta, ref previous, destination); return;
            case 35: DecodeInt32MiniBlockFastW35(packed, minDelta, ref previous, destination); return;
            case 36: DecodeInt32MiniBlockFastW36(packed, minDelta, ref previous, destination); return;
            case 37: DecodeInt32MiniBlockFastW37(packed, minDelta, ref previous, destination); return;
            case 38: DecodeInt32MiniBlockFastW38(packed, minDelta, ref previous, destination); return;
            case 39: DecodeInt32MiniBlockFastW39(packed, minDelta, ref previous, destination); return;
            case 40: DecodeInt32MiniBlockFastW40(packed, minDelta, ref previous, destination); return;
            case 41: DecodeInt32MiniBlockFastW41(packed, minDelta, ref previous, destination); return;
            case 42: DecodeInt32MiniBlockFastW42(packed, minDelta, ref previous, destination); return;
            case 43: DecodeInt32MiniBlockFastW43(packed, minDelta, ref previous, destination); return;
            case 44: DecodeInt32MiniBlockFastW44(packed, minDelta, ref previous, destination); return;
            case 45: DecodeInt32MiniBlockFastW45(packed, minDelta, ref previous, destination); return;
            case 46: DecodeInt32MiniBlockFastW46(packed, minDelta, ref previous, destination); return;
            case 47: DecodeInt32MiniBlockFastW47(packed, minDelta, ref previous, destination); return;
            case 48: DecodeInt32MiniBlockFastW48(packed, minDelta, ref previous, destination); return;
            case 49: DecodeInt32MiniBlockFastW49(packed, minDelta, ref previous, destination); return;
            case 50: DecodeInt32MiniBlockFastW50(packed, minDelta, ref previous, destination); return;
            case 51: DecodeInt32MiniBlockFastW51(packed, minDelta, ref previous, destination); return;
            case 52: DecodeInt32MiniBlockFastW52(packed, minDelta, ref previous, destination); return;
            case 53: DecodeInt32MiniBlockFastW53(packed, minDelta, ref previous, destination); return;
            case 54: DecodeInt32MiniBlockFastW54(packed, minDelta, ref previous, destination); return;
            case 55: DecodeInt32MiniBlockFastW55(packed, minDelta, ref previous, destination); return;
            case 56: DecodeInt32MiniBlockFastW56(packed, minDelta, ref previous, destination); return;
            default: throw new ArgumentOutOfRangeException(nameof(bitWidth));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW1(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 1;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            current = unchecked(current + minDelta + (long)(word0 & 0x1UL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 1) & 0x1UL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 2) & 0x1UL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 3) & 0x1UL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 4) & 0x1UL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 5) & 0x1UL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 6) & 0x1UL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 7) & 0x1UL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW2(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 2;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            current = unchecked(current + minDelta + (long)(word0 & 0x3UL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 2) & 0x3UL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 4) & 0x3UL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 6) & 0x3UL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 8) & 0x3UL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 10) & 0x3UL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 12) & 0x3UL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 14) & 0x3UL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW3(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 3;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            current = unchecked(current + minDelta + (long)(word0 & 0x7UL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 3) & 0x7UL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 6) & 0x7UL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 9) & 0x7UL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 12) & 0x7UL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 15) & 0x7UL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 18) & 0x7UL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 21) & 0x7UL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW4(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 4;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            current = unchecked(current + minDelta + (long)(word0 & 0xFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 4) & 0xFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 8) & 0xFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 12) & 0xFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 16) & 0xFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 20) & 0xFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 24) & 0xFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 28) & 0xFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW5(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 5;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 5) & 0x1FUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 10) & 0x1FUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 15) & 0x1FUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 20) & 0x1FUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 25) & 0x1FUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 30) & 0x1FUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 35) & 0x1FUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW6(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 6;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 6) & 0x3FUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 12) & 0x3FUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 18) & 0x3FUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 24) & 0x3FUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 30) & 0x3FUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 36) & 0x3FUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 42) & 0x3FUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW7(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 7;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 7) & 0x7FUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 14) & 0x7FUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 21) & 0x7FUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 28) & 0x7FUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 35) & 0x7FUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 42) & 0x7FUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 49) & 0x7FUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW8(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 8;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 8) & 0xFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 16) & 0xFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 24) & 0xFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 32) & 0xFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 40) & 0xFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 48) & 0xFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 56) & 0xFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW9(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 9;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 9) & 0x1FFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 18) & 0x1FFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 27) & 0x1FFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 36) & 0x1FFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 45) & 0x1FFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 54) & 0x1FFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 63) | (word1 << 1)) & 0x1FFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW10(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 10;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 10) & 0x3FFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 20) & 0x3FFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 30) & 0x3FFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 40) & 0x3FFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 50) & 0x3FFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 60) | (word1 << 4)) & 0x3FFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 6) & 0x3FFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW11(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 11;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 11) & 0x7FFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 22) & 0x7FFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 33) & 0x7FFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 44) & 0x7FFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 55) | (word1 << 9)) & 0x7FFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 2) & 0x7FFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 13) & 0x7FFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW12(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 12;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 12) & 0xFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 24) & 0xFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 36) & 0xFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 48) & 0xFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 60) | (word1 << 4)) & 0xFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 8) & 0xFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 20) & 0xFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW13(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 13;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 13) & 0x1FFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 26) & 0x1FFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 39) & 0x1FFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 52) | (word1 << 12)) & 0x1FFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 1) & 0x1FFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 14) & 0x1FFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 27) & 0x1FFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW14(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 14;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 14) & 0x3FFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 28) & 0x3FFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 42) & 0x3FFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 56) | (word1 << 8)) & 0x3FFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 6) & 0x3FFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 20) & 0x3FFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 34) & 0x3FFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW15(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 15;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 15) & 0x7FFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 30) & 0x7FFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 45) & 0x7FFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 60) | (word1 << 4)) & 0x7FFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 11) & 0x7FFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 26) & 0x7FFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 41) & 0x7FFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW16(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 16;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 16) & 0xFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 32) & 0xFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 48) & 0xFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(word1 & 0xFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 16) & 0xFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 32) & 0xFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 48) & 0xFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW17(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 17;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 17) & 0x1FFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 34) & 0x1FFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 51) | (word1 << 13)) & 0x1FFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 4) & 0x1FFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 21) & 0x1FFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 38) & 0x1FFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 55) | (word2 << 9)) & 0x1FFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW18(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 18;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 18) & 0x3FFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 36) & 0x3FFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 54) | (word1 << 10)) & 0x3FFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 8) & 0x3FFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 26) & 0x3FFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 44) & 0x3FFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 62) | (word2 << 2)) & 0x3FFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW19(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 19;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 19) & 0x7FFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 38) & 0x7FFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 57) | (word1 << 7)) & 0x7FFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 12) & 0x7FFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 31) & 0x7FFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 50) | (word2 << 14)) & 0x7FFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 5) & 0x7FFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW20(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 20;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 20) & 0xFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 40) & 0xFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 60) | (word1 << 4)) & 0xFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 16) & 0xFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 36) & 0xFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 56) | (word2 << 8)) & 0xFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 12) & 0xFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW21(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 21;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 21) & 0x1FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 42) & 0x1FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 63) | (word1 << 1)) & 0x1FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 20) & 0x1FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 41) & 0x1FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 62) | (word2 << 2)) & 0x1FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 19) & 0x1FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW22(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 22;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 22) & 0x3FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 44) | (word1 << 20)) & 0x3FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 2) & 0x3FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 24) & 0x3FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 46) | (word2 << 18)) & 0x3FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 4) & 0x3FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 26) & 0x3FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW23(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 23;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 23) & 0x7FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 46) | (word1 << 18)) & 0x7FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 5) & 0x7FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 28) & 0x7FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 51) | (word2 << 13)) & 0x7FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 10) & 0x7FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 33) & 0x7FFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW24(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 24;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 24) & 0xFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 48) | (word1 << 16)) & 0xFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 8) & 0xFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 32) & 0xFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 56) | (word2 << 8)) & 0xFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 16) & 0xFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 40) & 0xFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW25(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 25;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 25) & 0x1FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 50) | (word1 << 14)) & 0x1FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 11) & 0x1FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 36) & 0x1FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 61) | (word2 << 3)) & 0x1FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 22) & 0x1FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 47) | (word3 << 17)) & 0x1FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW26(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 26;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 26) & 0x3FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 52) | (word1 << 12)) & 0x3FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 14) & 0x3FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 40) | (word2 << 24)) & 0x3FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 2) & 0x3FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 28) & 0x3FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 54) | (word3 << 10)) & 0x3FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW27(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 27;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 27) & 0x7FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 54) | (word1 << 10)) & 0x7FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 17) & 0x7FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 44) | (word2 << 20)) & 0x7FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 7) & 0x7FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 34) & 0x7FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 61) | (word3 << 3)) & 0x7FFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW28(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 28;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 28) & 0xFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 56) | (word1 << 8)) & 0xFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 20) & 0xFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 48) | (word2 << 16)) & 0xFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 12) & 0xFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 40) | (word3 << 24)) & 0xFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 4) & 0xFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW29(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 29;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 29) & 0x1FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 58) | (word1 << 6)) & 0x1FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 23) & 0x1FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 52) | (word2 << 12)) & 0x1FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 17) & 0x1FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 46) | (word3 << 18)) & 0x1FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 11) & 0x1FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW30(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 30;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 30) & 0x3FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 60) | (word1 << 4)) & 0x3FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 26) & 0x3FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 56) | (word2 << 8)) & 0x3FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 22) & 0x3FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 52) | (word3 << 12)) & 0x3FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 18) & 0x3FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW31(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 31;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 31) & 0x7FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 62) | (word1 << 2)) & 0x7FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 29) & 0x7FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 60) | (word2 << 4)) & 0x7FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 27) & 0x7FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 58) | (word3 << 6)) & 0x7FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 25) & 0x7FFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW32(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 32;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word0 >> 32) & 0xFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(word1 & 0xFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 32) & 0xFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(word2 & 0xFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 32) & 0xFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(word3 & 0xFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 32) & 0xFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW33(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 33;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 33) | (word1 << 31)) & 0x1FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 2) & 0x1FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 35) | (word2 << 29)) & 0x1FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 4) & 0x1FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 37) | (word3 << 27)) & 0x1FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 6) & 0x1FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 39) | (word4 << 25)) & 0x1FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW34(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 34;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 34) | (word1 << 30)) & 0x3FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 4) & 0x3FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 38) | (word2 << 26)) & 0x3FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 8) & 0x3FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 42) | (word3 << 22)) & 0x3FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 12) & 0x3FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 46) | (word4 << 18)) & 0x3FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW35(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 35;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 35) | (word1 << 29)) & 0x7FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 6) & 0x7FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 41) | (word2 << 23)) & 0x7FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 12) & 0x7FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 47) | (word3 << 17)) & 0x7FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 18) & 0x7FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 53) | (word4 << 11)) & 0x7FFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW36(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 36;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 36) | (word1 << 28)) & 0xFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 8) & 0xFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 44) | (word2 << 20)) & 0xFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 16) & 0xFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 52) | (word3 << 12)) & 0xFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 24) & 0xFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 60) | (word4 << 4)) & 0xFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW37(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 37;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 37) | (word1 << 27)) & 0x1FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 10) & 0x1FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 47) | (word2 << 17)) & 0x1FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 20) & 0x1FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 57) | (word3 << 7)) & 0x1FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 30) | (word4 << 34)) & 0x1FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word4 >> 3) & 0x1FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW38(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 38;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 38) | (word1 << 26)) & 0x3FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 12) & 0x3FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 50) | (word2 << 14)) & 0x3FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 24) & 0x3FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 62) | (word3 << 2)) & 0x3FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 36) | (word4 << 28)) & 0x3FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word4 >> 10) & 0x3FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW39(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 39;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 39) | (word1 << 25)) & 0x7FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 14) & 0x7FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 53) | (word2 << 11)) & 0x7FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 28) | (word3 << 36)) & 0x7FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 3) & 0x7FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 42) | (word4 << 22)) & 0x7FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word4 >> 17) & 0x7FFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW40(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 40;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 40) | (word1 << 24)) & 0xFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 16) & 0xFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 56) | (word2 << 8)) & 0xFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 32) | (word3 << 32)) & 0xFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 8) & 0xFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 48) | (word4 << 16)) & 0xFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word4 >> 24) & 0xFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW41(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 41;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 41) | (word1 << 23)) & 0x1FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 18) & 0x1FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 59) | (word2 << 5)) & 0x1FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 36) | (word3 << 28)) & 0x1FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 13) & 0x1FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 54) | (word4 << 10)) & 0x1FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 31) | (word5 << 33)) & 0x1FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW42(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 42;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 42) | (word1 << 22)) & 0x3FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word1 >> 20) & 0x3FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 62) | (word2 << 2)) & 0x3FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 40) | (word3 << 24)) & 0x3FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 18) & 0x3FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 60) | (word4 << 4)) & 0x3FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 38) | (word5 << 26)) & 0x3FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW43(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 43;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 43) | (word1 << 21)) & 0x7FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 22) | (word2 << 42)) & 0x7FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 1) & 0x7FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 44) | (word3 << 20)) & 0x7FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 23) | (word4 << 41)) & 0x7FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word4 >> 2) & 0x7FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 45) | (word5 << 19)) & 0x7FFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW44(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 44;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 44) | (word1 << 20)) & 0xFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 24) | (word2 << 40)) & 0xFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 4) & 0xFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 48) | (word3 << 16)) & 0xFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 28) | (word4 << 36)) & 0xFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word4 >> 8) & 0xFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 52) | (word5 << 12)) & 0xFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW45(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 45;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 45) | (word1 << 19)) & 0x1FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 26) | (word2 << 38)) & 0x1FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 7) & 0x1FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 52) | (word3 << 12)) & 0x1FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 33) | (word4 << 31)) & 0x1FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word4 >> 14) & 0x1FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 59) | (word5 << 5)) & 0x1FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW46(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 46;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 46) | (word1 << 18)) & 0x3FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 28) | (word2 << 36)) & 0x3FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 10) & 0x3FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 56) | (word3 << 8)) & 0x3FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 38) | (word4 << 26)) & 0x3FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 20) | (word5 << 44)) & 0x3FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word5 >> 2) & 0x3FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW47(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 47;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 47) | (word1 << 17)) & 0x7FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 30) | (word2 << 34)) & 0x7FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 13) & 0x7FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 60) | (word3 << 4)) & 0x7FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 43) | (word4 << 21)) & 0x7FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 26) | (word5 << 38)) & 0x7FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word5 >> 9) & 0x7FFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW48(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 48;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 48) | (word1 << 16)) & 0xFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 32) | (word2 << 32)) & 0xFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word2 >> 16) & 0xFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(word3 & 0xFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 48) | (word4 << 16)) & 0xFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 32) | (word5 << 32)) & 0xFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word5 >> 16) & 0xFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW49(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 49;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            var word6 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 48));
            if (!BitConverter.IsLittleEndian) word6 = BinaryPrimitives.ReverseEndianness(word6);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 49) | (word1 << 15)) & 0x1FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 34) | (word2 << 30)) & 0x1FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 19) | (word3 << 45)) & 0x1FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 4) & 0x1FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 53) | (word4 << 11)) & 0x1FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 38) | (word5 << 26)) & 0x1FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word5 >> 23) | (word6 << 41)) & 0x1FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW50(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 50;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            var word6 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 48));
            if (!BitConverter.IsLittleEndian) word6 = BinaryPrimitives.ReverseEndianness(word6);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 50) | (word1 << 14)) & 0x3FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 36) | (word2 << 28)) & 0x3FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 22) | (word3 << 42)) & 0x3FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 8) & 0x3FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 58) | (word4 << 6)) & 0x3FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 44) | (word5 << 20)) & 0x3FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word5 >> 30) | (word6 << 34)) & 0x3FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW51(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 51;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            var word6 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 48));
            if (!BitConverter.IsLittleEndian) word6 = BinaryPrimitives.ReverseEndianness(word6);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 51) | (word1 << 13)) & 0x7FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 38) | (word2 << 26)) & 0x7FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 25) | (word3 << 39)) & 0x7FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word3 >> 12) & 0x7FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 63) | (word4 << 1)) & 0x7FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 50) | (word5 << 14)) & 0x7FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word5 >> 37) | (word6 << 27)) & 0x7FFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW52(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 52;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            var word6 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 48));
            if (!BitConverter.IsLittleEndian) word6 = BinaryPrimitives.ReverseEndianness(word6);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 52) | (word1 << 12)) & 0xFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 40) | (word2 << 24)) & 0xFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 28) | (word3 << 36)) & 0xFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 16) | (word4 << 48)) & 0xFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word4 >> 4) & 0xFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 56) | (word5 << 8)) & 0xFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word5 >> 44) | (word6 << 20)) & 0xFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW53(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 53;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            var word6 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 48));
            if (!BitConverter.IsLittleEndian) word6 = BinaryPrimitives.ReverseEndianness(word6);
            current = unchecked(current + minDelta + (long)(word0 & 0x1FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 53) | (word1 << 11)) & 0x1FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 42) | (word2 << 22)) & 0x1FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 31) | (word3 << 33)) & 0x1FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 20) | (word4 << 44)) & 0x1FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word4 >> 9) & 0x1FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 62) | (word5 << 2)) & 0x1FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word5 >> 51) | (word6 << 13)) & 0x1FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW54(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 54;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            var word6 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 48));
            if (!BitConverter.IsLittleEndian) word6 = BinaryPrimitives.ReverseEndianness(word6);
            current = unchecked(current + minDelta + (long)(word0 & 0x3FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 54) | (word1 << 10)) & 0x3FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 44) | (word2 << 20)) & 0x3FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 34) | (word3 << 30)) & 0x3FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 24) | (word4 << 40)) & 0x3FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 14) | (word5 << 50)) & 0x3FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word5 >> 4) & 0x3FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word5 >> 58) | (word6 << 6)) & 0x3FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW55(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 55;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            var word6 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 48));
            if (!BitConverter.IsLittleEndian) word6 = BinaryPrimitives.ReverseEndianness(word6);
            current = unchecked(current + minDelta + (long)(word0 & 0x7FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 55) | (word1 << 9)) & 0x7FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 46) | (word2 << 18)) & 0x7FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 37) | (word3 << 27)) & 0x7FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 28) | (word4 << 36)) & 0x7FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 19) | (word5 << 45)) & 0x7FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word5 >> 10) | (word6 << 54)) & 0x7FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word6 >> 1) & 0x7FFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    static void DecodeInt32MiniBlockFastW56(ReadOnlySpan<byte> packed, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(packed);
        ref var target = ref MemoryMarshal.GetReference(destination);
        var current = previous;
        for (var group = 0; group < 4; group++)
        {
            var byteOffset = group * 56;
            var word0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 0));
            if (!BitConverter.IsLittleEndian) word0 = BinaryPrimitives.ReverseEndianness(word0);
            var word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 8));
            if (!BitConverter.IsLittleEndian) word1 = BinaryPrimitives.ReverseEndianness(word1);
            var word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 16));
            if (!BitConverter.IsLittleEndian) word2 = BinaryPrimitives.ReverseEndianness(word2);
            var word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 24));
            if (!BitConverter.IsLittleEndian) word3 = BinaryPrimitives.ReverseEndianness(word3);
            var word4 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 32));
            if (!BitConverter.IsLittleEndian) word4 = BinaryPrimitives.ReverseEndianness(word4);
            var word5 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 40));
            if (!BitConverter.IsLittleEndian) word5 = BinaryPrimitives.ReverseEndianness(word5);
            var word6 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, byteOffset + 48));
            if (!BitConverter.IsLittleEndian) word6 = BinaryPrimitives.ReverseEndianness(word6);
            current = unchecked(current + minDelta + (long)(word0 & 0xFFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 0) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word0 >> 56) | (word1 << 8)) & 0xFFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 1) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word1 >> 48) | (word2 << 16)) & 0xFFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 2) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word2 >> 40) | (word3 << 24)) & 0xFFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 3) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word3 >> 32) | (word4 << 32)) & 0xFFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 4) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word4 >> 24) | (word5 << 40)) & 0xFFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 5) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)(((word5 >> 16) | (word6 << 48)) & 0xFFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 6) = unchecked((int)current);
            current = unchecked(current + minDelta + (long)((word6 >> 8) & 0xFFFFFFFFFFFFFFUL));
            Unsafe.Add(ref target, group * 8 + 7) = unchecked((int)current);
        }
        previous = current;
    }

}
