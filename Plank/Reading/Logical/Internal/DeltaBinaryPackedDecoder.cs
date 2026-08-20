using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Plank.Reading.Logical.Internal;

static class DeltaBinaryPackedDecoder
{
    // The unit every decoder below works in. It is not the mini-block size — the
    // format only requires a mini-block to be a multiple of 32 values — but 32
    // values at any bit width occupy a whole number of bytes, so a mini-block is
    // always a whole number of these and decodes as a run of them.
    const int MiniBlockChunk = 32;
    const int PackedBytesPerBitWidth = MiniBlockChunk / 8;
    const int PackedWordLookahead = sizeof(ulong) - 1;

    // Bounds the stack buffer the per-block bit widths are read into. Writers in
    // the wild use 4; this is room to spare rather than a considered limit.
    const int MaxMiniBlockCount = 64;

    /// <summary>How a page divides its values into blocks and mini-blocks.</summary>
    readonly record struct BlockLayout(int MiniBlockCount, int MiniBlockSize);

    /// <summary>Reads the block size and mini-block count a page declares.</summary>
    /// <remarks>
    /// This used to require exactly 128 and 4. Nothing in the format fixes them:
    /// the block size is a multiple of 128 and the mini-block size — the block
    /// size divided by the number of mini-blocks — is a multiple of 32, and that
    /// is all. Implementations differ inside that. parquet-mr and Arrow both use
    /// 128/4 for INT32, but Arrow uses 256/4 for INT64, so pinning 128/4 rejected
    /// every int64 delta column Arrow has ever written — all of them, on every
    /// format version, whatever the values.
    /// </remarks>
    static BlockLayout ReadBlockLayout(ref DeltaBinaryPackedReader reader)
    {
        var blockSize = ReadHeaderVarUInt32(ref reader, "block size");
        var miniBlockCount = ReadHeaderVarUInt32(ref reader, "mini-block count");

        if (blockSize == 0 || blockSize % 128 != 0)
            throw new CorruptParquetException(
                $"Delta binary packed block size {blockSize} is not a positive multiple of 128.");
        if (miniBlockCount == 0 || blockSize % miniBlockCount != 0)
            throw new CorruptParquetException(
                $"Delta binary packed block size {blockSize} is not divisible by its mini-block count {miniBlockCount}.");

        var miniBlockSize = blockSize / miniBlockCount;
        if (miniBlockSize % MiniBlockChunk != 0)
            throw new CorruptParquetException(
                $"Delta binary packed mini-block size {miniBlockSize} is not a multiple of {MiniBlockChunk}.");
        if (miniBlockCount > MaxMiniBlockCount)
            throw new NotSupportedException(
                $"Delta binary packed decoding supports at most {MaxMiniBlockCount} mini-blocks per block, "
                + $"and this page declares {miniBlockCount}.");

        return new BlockLayout((int)miniBlockCount, (int)miniBlockSize);
    }

    // A block costs at least one min-delta varint plus one bit-width byte per
    // mini-block, so this is the most values the bytes left could possibly hold.
    // It exists to stop a corrupt value count from driving a huge allocation.
    static uint MaxPossibleValues(BlockLayout layout, int remainingBytes)
    {
        var blockSize = layout.MiniBlockCount * layout.MiniBlockSize;
        var perByte = (blockSize + layout.MiniBlockCount) / (1 + layout.MiniBlockCount);
        return (uint)remainingBytes * (uint)perByte + 2U;
    }

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
        var layout = ReadBlockLayout(ref reader);
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");

        var maxPossibleValues = MaxPossibleValues(layout, payload.Length - reader.Offset);
        if (valueCount > maxPossibleValues)
            throw new CorruptParquetException(
                $"Delta binary packed value count {valueCount} exceeds what {payload.Length - reader.Offset} bytes could encode.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return ([], reader.Offset);
        }

        var values = new int[checked((int)valueCount)];
        ReadInt32Values(ref reader, values, layout);

        return (values, reader.Offset);
    }

    static int ReadInt32Core(ReadOnlySpan<byte> payload, Span<int> destination)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var layout = ReadBlockLayout(ref reader);
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if ((uint)destination.Length != valueCount)
            throw new CorruptParquetException(
                $"DeltaBinaryPacked encoded value count {valueCount} does not match expected {destination.Length}.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return reader.Offset;
        }

        ReadInt32Values(ref reader, destination, layout);

        return reader.Offset;
    }

    static void ReadInt32Values(ref DeltaBinaryPackedReader reader, Span<int> destination,
        BlockLayout layout)
    {
        // Int32 deltas can be wider than Int32, so reconstruct in Int64 and narrow only when storing.
        var previous = reader.ReadZigZagInt64();
        destination[0] = NarrowInt32(previous);
        var index = 1;
        Span<byte> bitWidthStorage = stackalloc byte[MaxMiniBlockCount];
        var bitWidths = bitWidthStorage[..layout.MiniBlockCount];
        Span<long> adjustedDeltas = Avx2.IsSupported ? stackalloc long[MiniBlockChunk] : default;

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

                // A mini-block holds a whole number of 32-value chunks, and 32
                // values at any bit width occupy a whole number of bytes, so a
                // wider mini-block decodes as a run of chunks of exactly the
                // shape the decoders below already handle.
                for (var chunk = 0; chunk < layout.MiniBlockSize; chunk += MiniBlockChunk)
                {
                    var count = Math.Min(MiniBlockChunk, destination.Length - index);
                    if (bitWidth <= 56)
                    {
                        var packed = reader.ReadBytesWithLookahead(
                            bitWidth * PackedBytesPerBitWidth, PackedWordLookahead);
                        if (Avx2.IsSupported && Bmi2.X64.IsSupported && bitWidth is > 0 and <= 16 &&
                            count == MiniBlockChunk)
                            DecodeInt32MiniBlockBmi2(packed, bitWidth, minDelta, ref previous,
                                destination.Slice(index, count));
                        else if (Avx2.IsSupported && bitWidth <= 16)
                            DecodeInt32MiniBlockVectorized(packed, bitWidth, minDelta, ref previous,
                                adjustedDeltas, destination.Slice(index, count));
                        else
                            DecodeInt32MiniBlock(packed, bitWidth, minDelta, ref previous,
                                destination.Slice(index, count));
                        index += count;
                        continue;
                    }

                    for (var i = 0; i < MiniBlockChunk; i++)
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
    }

    static void DecodeInt32MiniBlockBmi2(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var destinationStart = ref destination[0];
        long overflow = 0;

        if (bitWidth <= 8)
        {
            var laneMask = 0x0101010101010101UL * ((1UL << bitWidth) - 1);
            for (var index = 0; index < MiniBlockChunk; index += Vector256<int>.Count)
            {
                // Eight packed fields occupy exactly bitWidth bytes. PDEP places each field in
                // the low bits of a byte so two widening loads produce the Int64 delta lanes.
                var packedWord = ReadPackedWord(packed, index * bitWidth / 8);
                var unpacked = Bmi2.X64.ParallelBitDeposit(packedWord, laneMask);
                var unpackedBytes = Vector128.CreateScalar(unpacked).AsByte();
                var lower = Avx2.ConvertToVector256Int64(unpackedBytes);
                var upper = Avx2.ConvertToVector256Int64(
                    Sse2.ShiftRightLogical128BitLane(unpackedBytes, sizeof(uint)));
                ReconstructEightInt32(lower, upper, minDelta, ref previous,
                    ref destinationStart, index, ref overflow);
            }
        }
        else
        {
            var laneMask = 0x0001000100010001UL * ((1UL << bitWidth) - 1);
            for (var index = 0; index < MiniBlockChunk; index += Vector256<long>.Count)
            {
                // Four fields may start on a half-byte boundary. Shift the packed word to the
                // first field, then deposit the fields into four UInt16 lanes before widening.
                var bitOffset = index * bitWidth;
                var packedWord = ReadPackedWord(packed, bitOffset / 8) >> (bitOffset & 7);
                var unpacked = Bmi2.X64.ParallelBitDeposit(packedWord, laneMask);
                var residuals = Avx2.ConvertToVector256Int64(
                    Vector128.CreateScalar(unpacked).AsUInt16());
                ReconstructFourInt32(residuals, minDelta, ref previous,
                    ref destinationStart, index, ref overflow);
            }
        }

        ThrowIfInt32Overflow(overflow);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ReconstructEightInt32(Vector256<long> lower, Vector256<long> upper, long minDelta,
        ref long previous, ref int destination, int index, ref long overflow)
    {
        lower = PrefixSum(lower + Vector256.Create(minDelta));
        upper = PrefixSum(upper + Vector256.Create(minDelta));
        lower += Vector256.Create(previous);
        upper += Vector256.Create(lower.GetElement(Vector256<long>.Count - 1));

        if (ContainsInt32Overflow(lower) || ContainsInt32Overflow(upper))
            overflow = -1;
        Vector256.Narrow(lower, upper).StoreUnsafe(ref destination, (nuint)index);
        previous = upper.GetElement(Vector256<long>.Count - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ReconstructFourInt32(Vector256<long> residuals, long minDelta,
        ref long previous, ref int destination, int index, ref long overflow)
    {
        var values = PrefixSum(residuals + Vector256.Create(minDelta));
        values += Vector256.Create(previous);
        if (ContainsInt32Overflow(values))
            overflow = -1;
        Vector256.Narrow(values, Vector256<long>.Zero).GetLower()
            .StoreUnsafe(ref destination, (nuint)index);
        previous = values.GetElement(Vector256<long>.Count - 1);
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
        var layout = ReadBlockLayout(ref reader);
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
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
        Span<byte> bitWidthStorage = stackalloc byte[MaxMiniBlockCount];
        var bitWidths = bitWidthStorage[..layout.MiniBlockCount];

        while (index < valueCount)
        {
            var minDelta = reader.ReadZigZagInt64();
            for (var i = 0; i < bitWidths.Length; i++)
                bitWidths[i] = reader.ReadByte();

            for (var miniBlock = 0; miniBlock < bitWidths.Length; miniBlock++)
            {
                var bitWidth = bitWidths[miniBlock];
                for (var i = 0; i < layout.MiniBlockSize; i++)
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
        var layout = ReadBlockLayout(ref reader);
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");

        var maxPossibleValues = MaxPossibleValues(layout, payload.Length - reader.Offset);
        if (valueCount > maxPossibleValues)
            throw new CorruptParquetException(
                $"Delta binary packed value count {valueCount} exceeds what {payload.Length - reader.Offset} bytes could encode.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return ([], reader.Offset);
        }

        var values = new long[checked((int)valueCount)];
        ReadInt64Values(ref reader, values, layout);

        return (values, reader.Offset);
    }

    static int ReadInt64Core(ReadOnlySpan<byte> payload, Span<long> destination)
    {
        var reader = new DeltaBinaryPackedReader(payload);
        var layout = ReadBlockLayout(ref reader);
        var valueCount = ReadHeaderVarUInt32(ref reader, "value count");
        if ((uint)destination.Length != valueCount)
            throw new CorruptParquetException(
                $"DeltaBinaryPacked encoded value count {valueCount} does not match expected {destination.Length}.");

        if (valueCount == 0)
        {
            _ = reader.ReadUnsignedVarInt();
            return reader.Offset;
        }

        ReadInt64Values(ref reader, destination, layout);

        return reader.Offset;
    }

    static void ReadInt64Values(ref DeltaBinaryPackedReader reader, Span<long> destination,
        BlockLayout layout)
    {
        var previous = reader.ReadZigZagInt64();
        destination[0] = previous;
        var index = 1;
        Span<byte> bitWidthStorage = stackalloc byte[MaxMiniBlockCount];
        var bitWidths = bitWidthStorage[..layout.MiniBlockCount];
        Span<long> adjustedDeltas = Avx2.IsSupported ? stackalloc long[MiniBlockChunk] : default;

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

                // A mini-block holds a whole number of 32-value chunks, and 32
                // values at any bit width occupy a whole number of bytes, so a
                // wider mini-block decodes as a run of chunks of exactly the
                // shape the decoders below already handle.
                for (var chunk = 0; chunk < layout.MiniBlockSize; chunk += MiniBlockChunk)
                {
                    var count = Math.Min(MiniBlockChunk, destination.Length - index);
                    if (bitWidth <= 56)
                    {
                        var packed = reader.ReadBytesWithLookahead(
                            bitWidth * PackedBytesPerBitWidth, PackedWordLookahead);
                        if (Avx2.IsSupported && Bmi2.X64.IsSupported && bitWidth is > 0 and <= 16 &&
                            count == MiniBlockChunk)
                            DecodeInt64MiniBlockBmi2(packed, bitWidth, minDelta, ref previous,
                                destination.Slice(index, count));
                        else if (Avx2.IsSupported && bitWidth <= 16)
                            DecodeInt64MiniBlockVectorized(packed, bitWidth, minDelta, ref previous,
                                adjustedDeltas, destination.Slice(index, count));
                        else
                            DecodeInt64MiniBlock(packed, bitWidth, minDelta, ref previous,
                                destination.Slice(index, count));
                        index += count;
                        continue;
                    }

                    for (var i = 0; i < MiniBlockChunk; i++)
                    {
                        var delta = reader.ReadPackedUnsigned(bitWidth);
                        if (i >= count)
                            continue;
                        previous = unchecked(previous + minDelta + (long)delta);
                        destination[index++] = previous;
                    }
                }
            }
        }
    }

    static void DecodeInt64MiniBlockBmi2(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<long> destination)
    {
        ref var destinationStart = ref destination[0];
        if (bitWidth <= 8)
        {
            var laneMask = 0x0101010101010101UL * ((1UL << bitWidth) - 1);
            for (var index = 0; index < MiniBlockChunk; index += Vector256<long>.Count * 2)
            {
                // Eight packed fields occupy exactly bitWidth bytes. Deposit each field into
                // a byte lane, widen the lanes, then reconstruct both prefix-sum vectors.
                var packedWord = ReadPackedWord(packed, index * bitWidth / 8);
                var unpacked = Bmi2.X64.ParallelBitDeposit(packedWord, laneMask);
                var unpackedBytes = Vector128.CreateScalar(unpacked).AsByte();
                var lower = Avx2.ConvertToVector256Int64(unpackedBytes);
                var upper = Avx2.ConvertToVector256Int64(
                    Sse2.ShiftRightLogical128BitLane(unpackedBytes, sizeof(uint)));
                ReconstructEightInt64(lower, upper, minDelta, ref previous,
                    ref destinationStart, index);
            }
        }
        else
        {
            var laneMask = 0x0001000100010001UL * ((1UL << bitWidth) - 1);
            for (var index = 0; index < MiniBlockChunk; index += Vector256<long>.Count)
            {
                // Four fields may start between bytes. Align the first field, then deposit
                // all four into UInt16 lanes before widening them to Int64.
                var bitOffset = index * bitWidth;
                var packedWord = ReadPackedWord(packed, bitOffset / 8) >> (bitOffset & 7);
                var residuals = Avx2.ConvertToVector256Int64(
                    Vector128.CreateScalar(Bmi2.X64.ParallelBitDeposit(packedWord, laneMask)).AsUInt16());
                ReconstructFourInt64(residuals, minDelta, ref previous,
                    ref destinationStart, index);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ReconstructEightInt64(Vector256<long> lower, Vector256<long> upper, long minDelta,
        ref long previous, ref long destination, int index)
    {
        lower = PrefixSum(lower + Vector256.Create(minDelta));
        upper = PrefixSum(upper + Vector256.Create(minDelta));
        lower += Vector256.Create(previous);
        upper += Vector256.Create(lower.GetElement(Vector256<long>.Count - 1));
        lower.StoreUnsafe(ref destination, (nuint)index);
        upper.StoreUnsafe(ref destination, (nuint)(index + Vector256<long>.Count));
        previous = upper.GetElement(Vector256<long>.Count - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ReconstructFourInt64(Vector256<long> residuals, long minDelta,
        ref long previous, ref long destination, int index)
    {
        var values = PrefixSum(residuals + Vector256.Create(minDelta));
        values += Vector256.Create(previous);
        values.StoreUnsafe(ref destination, (nuint)index);
        previous = values.GetElement(Vector256<long>.Count - 1);
    }

    static void DecodeInt64MiniBlockVectorized(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<long> adjustedDeltas, Span<long> destination)
    {
        if (bitWidth == 0)
        {
            adjustedDeltas[..destination.Length].Fill(minDelta);
            ReconstructInt64MiniBlock(adjustedDeltas[..destination.Length], ref previous, destination);
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
        ReconstructInt64MiniBlock(adjustedDeltas[..destination.Length], ref previous, destination);
    }

    static void ReconstructInt64MiniBlock(ReadOnlySpan<long> adjustedDeltas,
        ref long previous, Span<long> destination)
    {
        if (destination.IsEmpty)
            return;

        var index = 0;
        ref var deltaStart = ref Unsafe.AsRef(in adjustedDeltas[0]);
        ref var destinationStart = ref destination[0];
        for (; index <= destination.Length - Vector256<long>.Count; index += Vector256<long>.Count)
        {
            var values = PrefixSum(Vector256.LoadUnsafe(ref deltaStart, (nuint)index));
            values += Vector256.Create(previous);
            values.StoreUnsafe(ref destinationStart, (nuint)index);
            previous = values.GetElement(Vector256<long>.Count - 1);
        }

        for (; index < destination.Length; index++)
        {
            previous = unchecked(previous + adjustedDeltas[index]);
            destination[index] = previous;
        }
    }

    static void DecodeInt64MiniBlock(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<long> destination)
    {
        if (bitWidth == 0)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                previous = unchecked(previous + minDelta);
                destination[i] = previous;
            }
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
            destination[i] = previous;
        }
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
