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
        // Reconstructed in Int64 and narrowed on store: the running sum is what
        // the vector paths widen to, and the narrowing is a truncation, because
        // the encoder's deltas wrap in 32 bits. See NarrowInt32.
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
                    if (Avx2.IsSupported && Bmi2.X64.IsSupported && bitWidth is > 0 and <= 16 &&
                        count == MiniBlockSize)
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

    static void DecodeInt32MiniBlockBmi2(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<int> destination)
    {
        ref var destinationStart = ref destination[0];

        if (bitWidth <= 8)
        {
            var laneMask = 0x0101010101010101UL * ((1UL << bitWidth) - 1);
            for (var index = 0; index < MiniBlockSize; index += Vector256<int>.Count)
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
                    ref destinationStart, index);
            }
        }
        else
        {
            var laneMask = 0x0001000100010001UL * ((1UL << bitWidth) - 1);
            for (var index = 0; index < MiniBlockSize; index += Vector256<long>.Count)
            {
                // Four fields may start on a half-byte boundary. Shift the packed word to the
                // first field, then deposit the fields into four UInt16 lanes before widening.
                var bitOffset = index * bitWidth;
                var packedWord = ReadPackedWord(packed, bitOffset / 8) >> (bitOffset & 7);
                var unpacked = Bmi2.X64.ParallelBitDeposit(packedWord, laneMask);
                var residuals = Avx2.ConvertToVector256Int64(
                    Vector128.CreateScalar(unpacked).AsUInt16());
                ReconstructFourInt32(residuals, minDelta, ref previous,
                    ref destinationStart, index);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ReconstructEightInt32(Vector256<long> lower, Vector256<long> upper, long minDelta,
        ref long previous, ref int destination, int index)
    {
        lower = PrefixSum(lower + Vector256.Create(minDelta));
        upper = PrefixSum(upper + Vector256.Create(minDelta));
        lower += Vector256.Create(previous);
        upper += Vector256.Create(lower.GetElement(Vector256<long>.Count - 1));

        Vector256.Narrow(lower, upper).StoreUnsafe(ref destination, (nuint)index);
        previous = upper.GetElement(Vector256<long>.Count - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void ReconstructFourInt32(Vector256<long> residuals, long minDelta,
        ref long previous, ref int destination, int index)
    {
        var values = PrefixSum(residuals + Vector256.Create(minDelta));
        values += Vector256.Create(previous);
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

            Vector256.Narrow(lower, upper).StoreUnsafe(ref destinationStart, (nuint)index);
            previous = upper.GetElement(Vector256<long>.Count - 1);
        }

        if (index <= destination.Length - Vector256<long>.Count)
        {
            var values = PrefixSum(Vector256.LoadUnsafe(ref deltaStart, (nuint)index));
            values += Vector256.Create(previous);
            Vector256.Narrow(values, Vector256<long>.Zero).GetLower()
                .StoreUnsafe(ref destinationStart, (nuint)index);
            previous = values.GetElement(Vector256<long>.Count - 1);
            index += Vector256<long>.Count;
        }

        for (; index < destination.Length; index++)
        {
            previous = unchecked(previous + adjustedDeltas[index]);
            destination[index] = unchecked((int)previous);
        }
    }

    static void DecodeInt32MiniBlock(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<int> destination)
    {
        if (bitWidth == 0)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                previous = unchecked(previous + minDelta);
                destination[i] = unchecked((int)previous);
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
            destination[i] = unchecked((int)previous);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<long> PrefixSum(Vector256<long> values)
    {
        values += Avx2.Permute4x64(values, 0x90) & Vector256.Create(0L, -1L, -1L, -1L);
        values += Avx2.Permute4x64(values, 0x40) & Vector256.Create(0L, 0L, -1L, -1L);
        return values;
    }

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
        ReadInt64Values(ref reader, values);

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

        ReadInt64Values(ref reader, destination);

        return reader.Offset;
    }

    static void ReadInt64Values(ref DeltaBinaryPackedReader reader, Span<long> destination)
    {
        var previous = reader.ReadZigZagInt64();
        destination[0] = previous;
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
                    if (Avx2.IsSupported && Bmi2.X64.IsSupported && bitWidth is > 0 and <= 16 &&
                        count == MiniBlockSize)
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

                for (var i = 0; i < MiniBlockSize; i++)
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

    static void DecodeInt64MiniBlockBmi2(ReadOnlySpan<byte> packed, int bitWidth, long minDelta,
        ref long previous, Span<long> destination)
    {
        ref var destinationStart = ref destination[0];
        if (bitWidth <= 8)
        {
            var laneMask = 0x0101010101010101UL * ((1UL << bitWidth) - 1);
            for (var index = 0; index < MiniBlockSize; index += Vector256<long>.Count * 2)
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
            for (var index = 0; index < MiniBlockSize; index += Vector256<long>.Count)
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

    // DELTA_BINARY_PACKED for INT32 is defined over wrapping 32-bit arithmetic:
    // an encoder takes each delta as a uint32 subtraction, so a run that steps
    // from Int32.MinValue to Int32.MaxValue is an ordinary delta and not an
    // out-of-range value. Reconstruction happens in Int64 because the widened
    // sum is what the vector paths need, and truncating back is what recovers
    // the value the encoder started from.
    static int NarrowInt32(long value)
        => unchecked((int)value);

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
