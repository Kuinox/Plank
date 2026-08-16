using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Plank.Writing.Encoding;

static class RleBitPackingHybridEncoding
{
    /// <summary>
    /// Validating entry point. The run-splitting and packing are shared with
    /// <see cref="WriteWithBitWidthPrefixUnchecked"/>; the only difference is that every value is
    /// range-checked first, so callers that cannot guarantee the invariant get an exception instead
    /// of silently truncated output.
    /// </summary>
    internal static void WriteWithBitWidthPrefix(ReadOnlySpan<int> values, int bitWidth, ref BufferWriter writer)
    {
        ValidateValues(values, bitWidth);
        WriteWithBitWidthPrefixUnchecked(values, bitWidth, ref writer);
    }

    /// <inheritdoc cref="WriteWithBitWidthPrefix"/>
    internal static void Write(ReadOnlySpan<int> values, int bitWidth, ref BufferWriter writer)
    {
        ValidateValues(values, bitWidth);
        WriteUnchecked(values, bitWidth, ref writer);
    }

    /// <summary>
    /// Rejects values that do not fit <paramref name="bitWidth"/>, before any output is produced.
    /// The dictionary-index callers satisfy this by construction, which is why they take the
    /// unchecked path.
    /// </summary>
    static void ValidateValues(ReadOnlySpan<int> values, int bitWidth)
    {
        if ((uint)bitWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, "Bit width must be between 0 and 32.");
        if (bitWidth == 32)
            return;

        if (bitWidth == 0)
        {
            for (var i = 0; i < values.Length; i++)
                if (values[i] != 0)
                    throw new InvalidOperationException("Non-zero value cannot be encoded with bit width 0.");
            return;
        }

        var maxValue = (1u << bitWidth) - 1u;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value < 0)
                throw new InvalidOperationException(
                    $"Value '{value}' cannot be encoded with bit width {bitWidth}.");
            if ((uint)value > maxValue)
                throw new InvalidOperationException(
                    $"Value '{value}' cannot be encoded with bit width {bitWidth}. Max is {maxValue}.");
        }
    }

    internal static void WriteBooleans(ReadOnlySpan<bool> values, ref BufferWriter writer)
    {
        if (values.Length == 0)
            return;

        var index = 0;
        while (index < values.Length)
        {
            var runLength = CountBooleanRunLength(values, index);
            if (runLength >= 8)
            {
                WriteBooleanRleRun(values[index], runLength, ref writer);
                index += runLength;
                continue;
            }

            var literalStart = index;
            index += runLength;
            var previousRunWasSingle = runLength == 1;

            while (index < values.Length)
            {
                if (previousRunWasSingle)
                {
                    var skipped = SkipDistinctAdjacentBooleanValues(values, index);
                    if (skipped != 0)
                    {
                        index += skipped;
                        continue;
                    }
                }

                runLength = CountBooleanRunLength(values, index);
                if (runLength >= 8)
                {
                    var literalLength = index - literalStart;
                    var padding = literalLength & 7;
                    if (padding == 0)
                        break;

                    var take = Math.Min(runLength, 8 - padding);
                    index += take;
                    if (take < runLength)
                        break;
                    previousRunWasSingle = false;
                    continue;
                }
                previousRunWasSingle = runLength == 1;
                index += runLength;
            }

            WriteBooleanBitPackedRun(values[literalStart..index], ref writer);
        }
    }

    internal static void WriteWithBitWidthPrefixUnchecked(ReadOnlySpan<int> values, int bitWidth, ref BufferWriter writer)
    {
        if ((uint)bitWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, "Bit width must be between 0 and 32.");

        var header = writer.GetSpan(1);
        header[0] = (byte)bitWidth;
        writer.Advance(1);
        WriteUnchecked(values, bitWidth, ref writer);
    }

    internal static void WriteUnchecked(ReadOnlySpan<int> values, int bitWidth, ref BufferWriter writer)
    {
        if ((uint)bitWidth > 32)
            throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, "Bit width must be between 0 and 32.");
        if (values.Length == 0)
            return;

        var index = 0;
        while (index < values.Length)
        {
            var runLength = CountRunLength(values, index);
            if (runLength >= 8)
            {
                EncodingPrimitives.WriteRleRun(values[index], runLength, bitWidth, ref writer);
                index += runLength;
                continue;
            }

            var literalStart = index;
            index += runLength;
            var previousRunWasSingle = runLength == 1;

            while (index < values.Length)
            {
                if (previousRunWasSingle)
                {
                    var skipped = SkipDistinctAdjacentValues(values, index);
                    if (skipped != 0)
                    {
                        index += skipped;
                        continue;
                    }
                }

                runLength = CountRunLength(values, index);
                if (runLength >= 8)
                {
                    var literalLength = index - literalStart;
                    var padding = literalLength & 7;
                    if (padding == 0)
                        break;

                    var take = Math.Min(runLength, 8 - padding);
                    index += take;
                    if (take < runLength)
                        break;
                    previousRunWasSingle = false;
                    continue;
                }

                previousRunWasSingle = runLength == 1;
                index += runLength;
            }

            WriteBitPackedRunUnchecked(values[literalStart..index], bitWidth, ref writer);
        }
    }

    static int CountRunLength(ReadOnlySpan<int> values, int start)
    {
        var value = values[start];
        ref var input = ref MemoryMarshal.GetReference(values);
        var index = start + 1;

        // Establish the eight-value RLE threshold scalarly, then scan genuinely long runs a vector at a time.
        var scalarEnd = Math.Min(values.Length, index + 7);
        while (index < scalarEnd && Unsafe.Add(ref input, index) == value)
            index++;
        if (index < scalarEnd || index == values.Length)
            return index - start;

        if (Vector512.IsHardwareAccelerated && Vector512<int>.IsSupported)
        {
            var expected = Vector512.Create(value);
            var lastVectorStart = values.Length - Vector512<int>.Count;
            while (index <= lastVectorStart)
            {
                var current = Vector512.LoadUnsafe(ref input, (nuint)index);
                var differentBits = ~Vector512.Equals(current, expected).ExtractMostSignificantBits()
                    & ((1u << Vector512<int>.Count) - 1u);
                if (differentBits != 0)
                    return index - start + BitOperations.TrailingZeroCount(differentBits);
                index += Vector512<int>.Count;
            }
        }
        if (Vector256.IsHardwareAccelerated && Vector256<int>.IsSupported)
        {
            var expected = Vector256.Create(value);
            var lastVectorStart = values.Length - Vector256<int>.Count;
            while (index <= lastVectorStart)
            {
                var current = Vector256.LoadUnsafe(ref input, (nuint)index);
                var differentBits = ~Vector256.Equals(current, expected).ExtractMostSignificantBits()
                    & ((1u << Vector256<int>.Count) - 1u);
                if (differentBits != 0)
                    return index - start + BitOperations.TrailingZeroCount(differentBits);
                index += Vector256<int>.Count;
            }
        }

        while (index < values.Length && Unsafe.Add(ref input, index) == value)
            index++;
        return index - start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int CountBooleanRunLength(ReadOnlySpan<bool> values, int start)
    {
        ref var input = ref Unsafe.As<bool, byte>(ref MemoryMarshal.GetReference(values));
        var expectedValue = Unsafe.Add(ref input, start);
        var index = start + 1;

        if (Vector512.IsHardwareAccelerated && Vector512<byte>.IsSupported)
        {
            var expected = Vector512.Create(expectedValue);
            var lastVectorStart = values.Length - Vector512<byte>.Count;
            while (index <= lastVectorStart)
            {
                var current = Vector512.LoadUnsafe(ref input, (nuint)index);
                var differentBits = ~Vector512.Equals(current, expected).ExtractMostSignificantBits();
                if (differentBits != 0)
                    return index - start + BitOperations.TrailingZeroCount(differentBits);
                index += Vector512<byte>.Count;
            }
        }
        if (Vector256.IsHardwareAccelerated && Vector256<byte>.IsSupported)
        {
            var expected = Vector256.Create(expectedValue);
            var lastVectorStart = values.Length - Vector256<byte>.Count;
            while (index <= lastVectorStart)
            {
                var current = Vector256.LoadUnsafe(ref input, (nuint)index);
                var differentBits = ~Vector256.Equals(current, expected).ExtractMostSignificantBits();
                if (differentBits != 0)
                    return index - start + BitOperations.TrailingZeroCount(differentBits);
                index += Vector256<byte>.Count;
            }
        }

        while (index < values.Length && Unsafe.Add(ref input, index) == expectedValue)
            index++;
        return index - start;
    }

    static int SkipDistinctAdjacentBooleanValues(ReadOnlySpan<bool> values, int start)
    {
        ref var input = ref Unsafe.As<bool, byte>(ref MemoryMarshal.GetReference(values));
        var index = start;

        if (Vector512.IsHardwareAccelerated && Vector512<byte>.IsSupported)
        {
            var lastVectorStart = values.Length - Vector512<byte>.Count - 1;
            while (index <= lastVectorStart)
            {
                var current = Vector512.LoadUnsafe(ref input, (nuint)index);
                var next = Vector512.LoadUnsafe(ref input, (nuint)(index + 1));
                var equalBits = Vector512.Equals(current, next).ExtractMostSignificantBits();
                if (equalBits != 0)
                    return index - start + BitOperations.TrailingZeroCount(equalBits);
                index += Vector512<byte>.Count;
            }
        }
        if (Vector256.IsHardwareAccelerated && Vector256<byte>.IsSupported)
        {
            var lastVectorStart = values.Length - Vector256<byte>.Count - 1;
            while (index <= lastVectorStart)
            {
                var current = Vector256.LoadUnsafe(ref input, (nuint)index);
                var next = Vector256.LoadUnsafe(ref input, (nuint)(index + 1));
                var equalBits = Vector256.Equals(current, next).ExtractMostSignificantBits();
                if (equalBits != 0)
                    return index - start + BitOperations.TrailingZeroCount(equalBits);
                index += Vector256<byte>.Count;
            }
        }

        return index - start;
    }

    static int SkipDistinctAdjacentValues(ReadOnlySpan<int> values, int start)
    {
        ref var input = ref MemoryMarshal.GetReference(values);
        var index = start;

        // Comparing overlapping vectors identifies the first possible run while skipping literal-only regions.
        if (Vector512.IsHardwareAccelerated && Vector512<int>.IsSupported)
        {
            var lastVectorStart = values.Length - Vector512<int>.Count - 1;
            while (index <= lastVectorStart)
            {
                var current = Vector512.LoadUnsafe(ref input, (nuint)index);
                var next = Vector512.LoadUnsafe(ref input, (nuint)(index + 1));
                var equalBits = Vector512.Equals(current, next).ExtractMostSignificantBits();
                if (equalBits != 0)
                    return index - start + BitOperations.TrailingZeroCount(equalBits);
                index += Vector512<int>.Count;
            }
        }
        if (Vector256.IsHardwareAccelerated && Vector256<int>.IsSupported)
        {
            var lastVectorStart = values.Length - Vector256<int>.Count - 1;
            while (index <= lastVectorStart)
            {
                var current = Vector256.LoadUnsafe(ref input, (nuint)index);
                var next = Vector256.LoadUnsafe(ref input, (nuint)(index + 1));
                var equalBits = Vector256.Equals(current, next).ExtractMostSignificantBits();
                if (equalBits != 0)
                    return index - start + BitOperations.TrailingZeroCount(equalBits);
                index += Vector256<int>.Count;
            }
        }

        return index - start;
    }

    static void WriteBooleanRleRun(bool value, int runLength, ref BufferWriter writer)
    {
        EncodingPrimitives.WriteUnsignedVarInt(((uint)runLength) << 1, ref writer);
        var encoded = writer.GetSpan(1);
        encoded[0] = value ? (byte)1 : (byte)0;
        writer.Advance(1);
    }

    static void WriteBitPackedRunUnchecked(ReadOnlySpan<int> literals, int bitWidth, ref BufferWriter writer)
    {
        if (literals.Length == 0)
            return;

        if (bitWidth == 0)
        {
            EncodingPrimitives.WriteRleRun(0, literals.Length, 0, ref writer);
            return;
        }

        var groupCount = (literals.Length + 7) >> 3;
        EncodingPrimitives.WriteUnsignedVarInt((((uint)groupCount) << 1) | 1u, ref writer);

        var byteCount = checked(groupCount * bitWidth);
        if (BitConverter.IsLittleEndian && (bitWidth & 7) == 0)
        {
            var byteAlignedDestination = writer.GetSpan(byteCount);
            WriteByteAlignedLiteralsUnchecked(literals, bitWidth >> 3, byteAlignedDestination[..byteCount]);
            writer.Advance(byteCount);
            return;
        }
        if (BitConverter.IsLittleEndian && bitWidth < 8)
        {
            // Eight values occupy exactly bitWidth bytes. Requesting seven spare bytes lets every
            // group use one unaligned 64-bit store; the next group overwrites the unused high bytes.
            var narrowDestination = writer.GetSpan(checked(byteCount + sizeof(ulong) - 1));
            WriteNarrowLiteralsUnchecked(literals, bitWidth, narrowDestination);
            writer.Advance(byteCount);
            return;
        }
        if (BitConverter.IsLittleEndian && bitWidth < 16)
        {
            // Split eight values into two four-value words. Four values at widths 9..15 fit in a
            // ulong, then two shifts join those words into the low/high halves of the bit stream.
            // This removes the byte-at-a-time state machine used by the general 17..32-bit path.
            var mediumDestination = writer.GetSpan(checked(byteCount + 2 * sizeof(ulong) - 1));
            WriteMediumLiteralsUnchecked(literals, bitWidth, mediumDestination);
            writer.Advance(byteCount);
            return;
        }

        var destination = writer.GetSpan(byteCount);
        var mask = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        ulong bitBuffer = 0;
        var bufferedBits = 0;
        var outputOffset = 0;
        var totalLiterals = groupCount * 8;
        for (var i = 0; i < totalLiterals; i++)
        {
            var value = i < literals.Length ? unchecked((uint)literals[i]) : 0u;
            bitBuffer |= (ulong)(value & mask) << bufferedBits;
            bufferedBits += bitWidth;

            while (bufferedBits >= 8)
            {
                destination[outputOffset++] = (byte)bitBuffer;
                bitBuffer >>= 8;
                bufferedBits -= 8;
            }
        }

        if (bufferedBits > 0)
            destination[outputOffset++] = (byte)bitBuffer;

        writer.Advance(outputOffset);
    }

    static void WriteNarrowLiteralsUnchecked(ReadOnlySpan<int> literals, int bitWidth, Span<byte> destination)
    {
        var mask = (1u << bitWidth) - 1u;
        ref var source = ref MemoryMarshal.GetReference(literals);
        ref var output = ref MemoryMarshal.GetReference(destination);
        var inputOffset = 0;
        var outputOffset = 0;
        while (inputOffset <= literals.Length - 8)
        {
            var packed = PackEightNarrow(ref Unsafe.Add(ref source, inputOffset), mask, bitWidth);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), packed);
            inputOffset += 8;
            outputOffset += bitWidth;
        }

        if (inputOffset == literals.Length)
            return;

        Span<int> tail = stackalloc int[8];
        tail.Clear();
        literals[inputOffset..].CopyTo(tail);
        var tailPacked = PackEightNarrow(ref MemoryMarshal.GetReference(tail), mask, bitWidth);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, outputOffset), tailPacked);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong PackEightNarrow(ref int source, uint mask, int bitWidth)
        => ((uint)source & mask)
           | ((ulong)((uint)Unsafe.Add(ref source, 1) & mask) << bitWidth)
           | ((ulong)((uint)Unsafe.Add(ref source, 2) & mask) << (2 * bitWidth))
           | ((ulong)((uint)Unsafe.Add(ref source, 3) & mask) << (3 * bitWidth))
           | ((ulong)((uint)Unsafe.Add(ref source, 4) & mask) << (4 * bitWidth))
           | ((ulong)((uint)Unsafe.Add(ref source, 5) & mask) << (5 * bitWidth))
           | ((ulong)((uint)Unsafe.Add(ref source, 6) & mask) << (6 * bitWidth))
           | ((ulong)((uint)Unsafe.Add(ref source, 7) & mask) << (7 * bitWidth));

    static void WriteMediumLiteralsUnchecked(ReadOnlySpan<int> literals, int bitWidth, Span<byte> destination)
    {
        var mask = (1u << bitWidth) - 1u;
        ref var source = ref MemoryMarshal.GetReference(literals);
        ref var output = ref MemoryMarshal.GetReference(destination);
        var inputOffset = 0;
        var outputOffset = 0;
        while (inputOffset <= literals.Length - 8)
        {
            PackEightMedium(ref Unsafe.Add(ref source, inputOffset), mask, bitWidth,
                out var low, out var high);
            ref var groupOutput = ref Unsafe.Add(ref output, outputOffset);
            Unsafe.WriteUnaligned(ref groupOutput, low);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref groupOutput, sizeof(ulong)), high);
            inputOffset += 8;
            outputOffset += bitWidth;
        }

        if (inputOffset == literals.Length)
            return;

        Span<int> tail = stackalloc int[8];
        tail.Clear();
        literals[inputOffset..].CopyTo(tail);
        PackEightMedium(ref MemoryMarshal.GetReference(tail), mask, bitWidth,
            out var tailLow, out var tailHigh);
        ref var tailOutput = ref Unsafe.Add(ref output, outputOffset);
        Unsafe.WriteUnaligned(ref tailOutput, tailLow);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref tailOutput, sizeof(ulong)), tailHigh);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void PackEightMedium(ref int source, uint mask, int bitWidth, out ulong low, out ulong high)
    {
        var first = ((uint)source & mask)
                    | ((ulong)((uint)Unsafe.Add(ref source, 1) & mask) << bitWidth)
                    | ((ulong)((uint)Unsafe.Add(ref source, 2) & mask) << (2 * bitWidth))
                    | ((ulong)((uint)Unsafe.Add(ref source, 3) & mask) << (3 * bitWidth));
        var second = ((uint)Unsafe.Add(ref source, 4) & mask)
                     | ((ulong)((uint)Unsafe.Add(ref source, 5) & mask) << bitWidth)
                     | ((ulong)((uint)Unsafe.Add(ref source, 6) & mask) << (2 * bitWidth))
                     | ((ulong)((uint)Unsafe.Add(ref source, 7) & mask) << (3 * bitWidth));
        var firstBitCount = 4 * bitWidth;
        low = first | second << firstBitCount;
        high = second >> (64 - firstBitCount);
    }

    static void WriteByteAlignedLiteralsUnchecked(ReadOnlySpan<int> literals, int byteWidth, Span<byte> destination)
    {
        ref var source = ref MemoryMarshal.GetReference(literals);
        ref var output = ref MemoryMarshal.GetReference(destination);
        switch (byteWidth)
        {
            case 1:
                for (nuint i = 0; i < (uint)literals.Length; i++)
                    Unsafe.Add(ref output, i) = unchecked((byte)Unsafe.Add(ref source, i));
                break;
            case 2:
                for (nuint i = 0; i < (uint)literals.Length; i++)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, i * 2), unchecked((ushort)Unsafe.Add(ref source, i)));
                break;
            case 3:
                for (nuint i = 0; i < (uint)literals.Length; i++)
                {
                    var value = unchecked((uint)Unsafe.Add(ref source, i));
                    ref var encoded = ref Unsafe.Add(ref output, i * 3);
                    encoded = (byte)value;
                    Unsafe.Add(ref encoded, 1) = (byte)(value >> 8);
                    Unsafe.Add(ref encoded, 2) = (byte)(value >> 16);
                }
                break;
            case 4:
                MemoryMarshal.AsBytes(literals).CopyTo(destination);
                break;
        }

        var encodedByteCount = literals.Length * byteWidth;
        destination.Slice(encodedByteCount, destination.Length - encodedByteCount).Clear();
    }

    static void WriteBooleanBitPackedRun(ReadOnlySpan<bool> literals, ref BufferWriter writer)
    {
        if (literals.Length == 0)
            return;

        var groupCount = (literals.Length + 7) >> 3;
        EncodingPrimitives.WriteUnsignedVarInt((((uint)groupCount) << 1) | 1u, ref writer);

        EncodingPrimitives.PackBooleans(literals, writer.GetSpan(groupCount));
        writer.Advance(groupCount);
    }

}
