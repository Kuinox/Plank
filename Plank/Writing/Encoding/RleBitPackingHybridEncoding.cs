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
        var destination = writer.GetSpan(byteCount);
        if (BitConverter.IsLittleEndian && (bitWidth & 7) == 0)
        {
            WriteByteAlignedLiteralsUnchecked(literals, bitWidth >> 3, destination[..byteCount]);
            writer.Advance(byteCount);
            return;
        }

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
