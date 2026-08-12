using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Plank.Reading.Logical.Internal;

static class DeltaBinaryPackedDecoder
{
    const uint BlockSize = 128;
    const uint MiniBlockCount = 4;
    const int MiniBlockSize = 32;
    const int PackedBytesPerBitWidth = MiniBlockSize / 8;
    const int PackedWordLookahead = sizeof(ulong) - 1;

    internal static int[] ReadInt32(ReadOnlySpan<byte> payload)
    {
        var (values, _) = ReadInt32Core(payload);
        return values;
    }

    internal static long[] ReadInt64(ReadOnlySpan<byte> payload)
    {
        var (values, _) = ReadInt64Core(payload);
        return values;
    }

    internal static int ReadInt32(ReadOnlySpan<byte> payload, Span<int> destination)
        => ReadInt32Core(payload, destination);

    internal static int ReadNarrowInt32<T>(ReadOnlySpan<byte> payload, Span<T> destination)
        where T : unmanaged
        => ReadNarrowInt32Core(payload, destination);

    internal static int ReadInt64(ReadOnlySpan<byte> payload, Span<long> destination)
        => ReadInt64Core(payload, destination);

    internal static int ReadNonNegativeInt32WithConsumedBytes(ReadOnlySpan<byte> payload, Span<int> destination)
    {
        var consumedBytes = ReadInt32Core(payload, destination);
        for (var i = 0; i < destination.Length; i++)
            if (destination[i] < 0)
                throw new CorruptParquetException($"Delta encoded length {destination[i]} is negative.");
        return consumedBytes;
    }

    internal static (uint[] Values, int ConsumedBytes) ReadUInt32WithConsumedBytes(ReadOnlySpan<byte> payload)
    {
        var (signed, consumedBytes) = ReadInt32Core(payload);
        var result = new uint[signed.Length];
        for (var i = 0; i < signed.Length; i++)
        {
            if (signed[i] < 0)
                throw new CorruptParquetException(
                    $"Delta encoded length {signed[i]} is negative.");
            result[i] = (uint)signed[i];
        }
        return (result, consumedBytes);
    }

    static (int[] Values, int ConsumedBytes) ReadInt32Core(ReadOnlySpan<byte> payload)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");

        // Each block encodes 128 values in at minimum 5 bytes (1 minDelta varint + 4 bitwidths, bitWidth=0).
        // A corrupt header claiming more values than the payload could hold would cause a huge allocation.
        var maxPossibleValues = (uint)(payload.Length - reader.Offset) * 26U + 2U;
        if (valueCount > maxPossibleValues)
            throw new CorruptParquetException(
                $"Delta binary packed value count {valueCount} exceeds what {payload.Length - reader.Offset} bytes could encode.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return ([], reader.Offset);
        }

        var values = new int[checked((int)valueCount)];
        ReadInt32Values(ref reader, values);

        return (values, reader.Offset);
    }

    static int ReadInt32Core(ReadOnlySpan<byte> payload, Span<int> destination)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");
        if ((uint)destination.Length != valueCount)
            throw new CorruptParquetException(
                $"DeltaBinaryPacked encoded value count {valueCount} does not match expected {destination.Length}.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return reader.Offset;
        }

        ReadInt32Values(ref reader, destination);

        return reader.Offset;
    }

    static void ReadInt32Values(ref DeltaBinaryPackedReader reader, Span<int> destination)
    {
        // Int32 deltas can be wider than Int32, so reconstruct in Int64 and narrow only when storing.
        var previous = reader.ReadZigZagInt64();
        destination[0] = NarrowInt32(previous);
        var index = 1;
        Span<byte> bitWidths = stackalloc byte[checked((int)MiniBlockCount)];
        Span<long> adjustedDeltas = Avx2.IsSupported ? stackalloc long[MiniBlockSize] : default;

        while (index < destination.Length)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0; i < bitWidths.Length; i++)
                bitWidths[i] = reader.ReadByte();

            for (var miniBlock = 0; miniBlock < bitWidths.Length; miniBlock++)
            {
                var bitWidth = bitWidths[miniBlock];
                if (bitWidth > 64)
                    throw new CorruptParquetException(
                        $"Delta binary packed mini-block bit width {bitWidth} exceeds 64.");

                var count = Math.Min(MiniBlockSize, destination.Length - index);
                if (bitWidth <= 56)
                {
                    var packed = reader.ReadBytesWithLookahead(
                        bitWidth * PackedBytesPerBitWidth, PackedWordLookahead);
                    if (Avx2.IsSupported && bitWidth <= 16)
                        DecodeInt32MiniBlockVectorized(packed, bitWidth, minDelta, ref previous,
                            adjustedDeltas, destination.Slice(index, count));
                    else
                        DecodeInt32MiniBlock(packed, bitWidth, minDelta, ref previous,
                            destination.Slice(index, count));
                    index += count;
                    continue;
                }

                for (var i = 0; i < MiniBlockSize; i++)
                {
                    var delta = reader.ReadPackedUnsigned(bitWidth);
                    if (i >= count)
                        continue;
                    previous = unchecked(previous + minDelta + (long)delta);
                    destination[index++] = NarrowInt32(previous);
                }
            }
        }
    }

    static void DecodeInt32MiniBlockVectorized(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<long> adjustedDeltas, Span<int> destination)
    {
        if (bitWidth == 0)
        {
            adjustedDeltas[..destination.Length].Fill(minDelta);
            ReconstructInt32MiniBlock(adjustedDeltas[..destination.Length], ref previous, destination);
            return;
        }

        var mask = (1UL << bitWidth) - 1;
        var packedByteCount = bitWidth * PackedBytesPerBitWidth;
        var byteOffset = 0;
        ulong bitBuffer = 0;
        var bufferedBits = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            if (bufferedBits < bitWidth)
            {
                var bytesToLoad = Math.Min((64 - bufferedBits) / 8,
                    packedByteCount - byteOffset);
                var loaded = ReadPackedWord(packed, byteOffset);
                if (bytesToLoad < sizeof(ulong))
                    loaded &= (1UL << (bytesToLoad * 8)) - 1;
                bitBuffer |= loaded << bufferedBits;
                byteOffset += bytesToLoad;
                bufferedBits += bytesToLoad * 8;
            }

            var delta = bitBuffer & mask;
            bitBuffer >>= bitWidth;
            bufferedBits -= bitWidth;
            adjustedDeltas[i] = unchecked(minDelta + (long)delta);
        }
        ReconstructInt32MiniBlock(adjustedDeltas[..destination.Length], ref previous, destination);
    }

    static void ReconstructInt32MiniBlock(ReadOnlySpan<long> adjustedDeltas,
        ref long previous, Span<int> destination)
    {
        if (destination.IsEmpty)
            return;

        long overflow = 0;
        var index = 0;
        ref var deltaStart = ref Unsafe.AsRef(in adjustedDeltas[0]);
        ref var destinationStart = ref destination[0];
        for (; index <= destination.Length - Vector256<int>.Count; index += Vector256<int>.Count)
        {
            var lower = PrefixSum(Vector256.LoadUnsafe(ref deltaStart, (nuint)index));
            var upper = PrefixSum(Vector256.LoadUnsafe(
                ref deltaStart, (nuint)(index + Vector256<long>.Count)));
            lower += Vector256.Create(previous);
            upper += Vector256.Create(lower.GetElement(Vector256<long>.Count - 1));

            var narrowed = Vector256.Narrow(lower, upper);
            if (ContainsInt32Overflow(lower) || ContainsInt32Overflow(upper))
                overflow = -1;
            narrowed.StoreUnsafe(ref destinationStart, (nuint)index);
            previous = upper.GetElement(Vector256<long>.Count - 1);
        }

        if (index <= destination.Length - Vector256<long>.Count)
        {
            var values = PrefixSum(Vector256.LoadUnsafe(ref deltaStart, (nuint)index));
            values += Vector256.Create(previous);
            var narrowed = Vector256.Narrow(values, Vector256<long>.Zero);
            if (ContainsInt32Overflow(values))
                overflow = -1;
            narrowed.GetLower().StoreUnsafe(ref destinationStart, (nuint)index);
            previous = values.GetElement(Vector256<long>.Count - 1);
            index += Vector256<long>.Count;
        }

        for (; index < destination.Length; index++)
        {
            previous = unchecked(previous + adjustedDeltas[index]);
            overflow |= previous ^ (long)(int)previous;
            destination[index] = unchecked((int)previous);
        }
        ThrowIfInt32Overflow(overflow);
    }

    static void DecodeInt32MiniBlock(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<int> destination)
    {
        long overflow = 0;
        if (bitWidth == 0)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                previous = unchecked(previous + minDelta);
                overflow |= previous ^ (long)(int)previous;
                destination[i] = unchecked((int)previous);
            }
            ThrowIfInt32Overflow(overflow);
            return;
        }

        var mask = (1UL << bitWidth) - 1;
        var packedByteCount = bitWidth * PackedBytesPerBitWidth;
        var byteOffset = 0;
        ulong bitBuffer = 0;
        var bufferedBits = 0;
        for (var i = 0; i < destination.Length; i++)
        {
            if (bufferedBits < bitWidth)
            {
                var bytesToLoad = Math.Min((64 - bufferedBits) / 8,
                    packedByteCount - byteOffset);
                var loaded = ReadPackedWord(packed, byteOffset);
                if (bytesToLoad < sizeof(ulong))
                    loaded &= (1UL << (bytesToLoad * 8)) - 1;
                bitBuffer |= loaded << bufferedBits;
                byteOffset += bytesToLoad;
                bufferedBits += bytesToLoad * 8;
            }

            var delta = bitBuffer & mask;
            bitBuffer >>= bitWidth;
            bufferedBits -= bitWidth;
            previous = unchecked(previous + minDelta + (long)delta);
            overflow |= previous ^ (long)(int)previous;
            destination[i] = unchecked((int)previous);
        }
        ThrowIfInt32Overflow(overflow);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<long> PrefixSum(Vector256<long> values)
    {
        values += Avx2.Permute4x64(values, 0x90) & Vector256.Create(0L, -1L, -1L, -1L);
        values += Avx2.Permute4x64(values, 0x40) & Vector256.Create(0L, 0L, -1L, -1L);
        return values;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool ContainsInt32Overflow(Vector256<long> values)
        => !Vector256.EqualsAll(values, Vector256.ShiftRightArithmetic(values << 32, 32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong ReadPackedWord(ReadOnlySpan<byte> packed, int byteOffset)
    {
        if (byteOffset <= packed.Length - sizeof(ulong))
            return BinaryPrimitives.ReadUInt64LittleEndian(packed.Slice(byteOffset, sizeof(ulong)));

        ulong value = 0;
        for (var i = byteOffset; i < packed.Length; i++)
            value |= (ulong)packed[i] << ((i - byteOffset) * 8);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ThrowIfInt32Overflow(long overflow)
    {
        if (overflow != 0)
            throw new CorruptParquetException(
                "Delta binary packed mini-block contains a value outside the Int32 range.");
    }

    static int ReadNarrowInt32Core<T>(ReadOnlySpan<byte> payload, Span<T> destination)
        where T : unmanaged
    {
        if (typeof(T) != typeof(byte) && typeof(T) != typeof(ushort))
            throw new InvalidOperationException($"Cannot narrow Int32 values into '{typeof(T)}'.");

        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");
        if ((uint)destination.Length != valueCount)
            throw new CorruptParquetException(
                $"DeltaBinaryPacked encoded value count {valueCount} does not match expected {destination.Length}.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return reader.Offset;
        }

        var previous = reader.ReadZigZagInt64();
        StoreNarrowInt32(destination, 0, NarrowInt32(previous));
        var index = 1U;
        var miniBlockSize = blockSize / miniBlockCount;
        Span<byte> bitWidths = stackalloc byte[checked((int)MiniBlockCount)];

        while (index < valueCount)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0U; i < MiniBlockCount; i++)
                bitWidths[checked((int)i)] = reader.ReadByte();

            for (var miniBlock = 0U; miniBlock < MiniBlockCount; miniBlock++)
            {
                var bitWidth = bitWidths[checked((int)miniBlock)];
                for (var i = 0U; i < miniBlockSize; i++)
                {
                    var delta = bitWidth == 0 ? 0UL : reader.ReadPackedUnsigned(bitWidth);
                    if (index < valueCount)
                    {
                        previous = unchecked(previous + minDelta + (long)delta);
                        StoreNarrowInt32(destination, checked((int)index++), NarrowInt32(previous));
                    }
                }
            }
        }

        return reader.Offset;
    }

    static (long[] Values, int ConsumedBytes) ReadInt64Core(ReadOnlySpan<byte> payload)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");

        var maxPossibleValues = (uint)(payload.Length - reader.Offset) * 26U + 2U;
        if (valueCount > maxPossibleValues)
            throw new CorruptParquetException(
                $"Delta binary packed value count {valueCount} exceeds what {payload.Length - reader.Offset} bytes could encode.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return ([], reader.Offset);
        }

        var values = new long[checked((int)valueCount)];
        values[0] = reader.ReadZigZagInt64();
        var index = 1U;
        var previous = values[0];
        var miniBlockSize = blockSize / miniBlockCount;
        Span<byte> bitWidths = stackalloc byte[checked((int)MiniBlockCount)];

        while (index < valueCount)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0U; i < MiniBlockCount; i++)
                bitWidths[checked((int)i)] = reader.ReadByte();

            for (var miniBlock = 0U; miniBlock < MiniBlockCount; miniBlock++)
            {
                var bitWidth = bitWidths[checked((int)miniBlock)];
                for (var i = 0U; i < miniBlockSize; i++)
                {
                    var delta = bitWidth == 0 ? 0UL : reader.ReadPackedUnsigned(bitWidth);
                    if (index < valueCount)
                    {
                        previous = unchecked(previous + minDelta + (long)delta);
                        values[checked((int)index++)] = previous;
                    }
                }
            }
        }

        return (values, reader.Offset);
    }

    static int ReadInt64Core(ReadOnlySpan<byte> payload, Span<long> destination)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if (blockSize != BlockSize || miniBlockCount != MiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding currently supports block size {BlockSize} and mini-block count {MiniBlockCount} only.");
        if ((uint)destination.Length != valueCount)
            throw new CorruptParquetException(
                $"DeltaBinaryPacked encoded value count {valueCount} does not match expected {destination.Length}.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return reader.Offset;
        }

        destination[0] = reader.ReadZigZagInt64();
        var index = 1U;
        var previous = destination[0];
        var miniBlockSize = blockSize / miniBlockCount;
        Span<byte> bitWidths = stackalloc byte[checked((int)MiniBlockCount)];

        while (index < valueCount)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0U; i < MiniBlockCount; i++)
                bitWidths[checked((int)i)] = reader.ReadByte();

            for (var miniBlock = 0U; miniBlock < MiniBlockCount; miniBlock++)
            {
                var bitWidth = bitWidths[checked((int)miniBlock)];
                for (var i = 0U; i < miniBlockSize; i++)
                {
                    var delta = bitWidth == 0 ? 0UL : reader.ReadPackedUnsigned(bitWidth);
                    if (index < valueCount)
                    {
                        previous = unchecked(previous + minDelta + (long)delta);
                        destination[checked((int)index++)] = previous;
                    }
                }
            }
        }

        return reader.Offset;
    }

    static int NarrowInt32(long value)
    {
        if (value < int.MinValue || value > int.MaxValue)
            throw new CorruptParquetException($"Delta binary packed Int32 value {value} is outside the Int32 range.");

        return (int)value;
    }

    static void StoreNarrowInt32<T>(Span<T> destination, int index, int value)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            Unsafe.As<Span<T>, Span<byte>>(ref destination)[index] = unchecked((byte)value);
            return;
        }

        Unsafe.As<Span<T>, Span<ushort>>(ref destination)[index] = unchecked((ushort)value);
    }

    static uint ReadHeaderVarUInt32(ref DeltaBinaryPackedReader reader, string fieldName)
    {
        var value = reader.ReadUnsignedVarInt();
        if (value > uint.MaxValue)
            throw new CorruptParquetException(
                $"Delta binary packed {fieldName} {value} exceeds the supported maximum.");
        return (uint)value;
    }
}
