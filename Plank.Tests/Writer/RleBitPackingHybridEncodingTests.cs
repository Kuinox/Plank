using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class RleBitPackingHybridEncodingTests
{
    [Test]
    public void DictionaryIndexesMatchReferenceAcrossBitWidthsAndBoundaries()
    {
        int[] lengths = [0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255];
        for (var bitWidth = 0; bitWidth <= 32; bitWidth++)
        {
            foreach (var length in lengths)
                VerifyDictionaryIndexes(CreateLiteralValues(length, bitWidth), bitWidth);

            if (bitWidth > 0)
                VerifyDictionaryIndexes(CreateMixedValues(bitWidth), bitWidth);
        }
    }

    [Test]
    public void ByteAlignedUncheckedWriterMatchesReferenceForPartialGroups()
    {
        int[] bitWidths = [8, 16, 24, 32];
        int[] lengths = [1, 7, 9, 15, 17];
        foreach (var bitWidth in bitWidths)
            foreach (var length in lengths)
                VerifyDictionaryIndexes(CreateLiteralValues(length, bitWidth), bitWidth);
    }

    [Test]
    public void CheckedWriterRejectsOutOfRangeLiteralsAtCommonBitWidths()
    {
        VerifyCheckedWriterRejects([0, 1, 0, 1, 0, 1, 0, 2], 1);
        VerifyCheckedWriterRejects([0, 1, 2, 3, 4, 5, 6, 256], 8);
        VerifyCheckedWriterRejects([0, 1, 2, 3, 4, 5, 6, 65_536], 16);
        VerifyCheckedWriterRejects([0, 1, 2, 3, 4, 5, 6, 1 << 24], 24);
    }

    [Test]
    public void DictionaryIndexesMatchReferenceAcrossRandomMixedRunAlignments()
    {
        int[] bitWidths = [1, 2, 7, 8, 15, 16, 24, 31, 32];
        foreach (var bitWidth in bitWidths)
        {
            for (var alignment = 0; alignment < 16; alignment++)
                VerifyDictionaryIndexes(CreateRandomMixedValues(2_048, bitWidth, alignment), bitWidth);
        }
    }

    [Test]
    public void BooleansMatchReferenceAcrossLiteralAndRleBoundaries()
    {
        bool[][] inputs =
        [
            [],
            [true],
            [true, false, true, false, true, false, true],
            [true, false, true, false, true, false, true, false],
            [true, true, true, true, true, true, true, true],
            [true, true, true, true, true, true, true, false, false, false, false, false, false, false, false, false],
            CreateBooleanMixedValues(),
            CreateBooleanBoundaryValues()
        ];

        foreach (var input in inputs)
            VerifyBooleans(input);
    }

    [Test]
    public void BooleansMatchReferenceAcrossRandomMixedRunAlignments()
    {
        for (var alignment = 0; alignment < 64; alignment++)
            VerifyBooleans(CreateRandomBooleanMixedValues(4_096, alignment));
    }

    static void VerifyDictionaryIndexes(int[] values, int bitWidth)
    {
        var actual = EncodeDictionaryIndexes(values, bitWidth);
        var expected = EncodeReference(values, bitWidth, includeBitWidthPrefix: true);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"RLE/bit-packed output differs from the reference for bit width {bitWidth} and {values.Length} values.");
    }

    static void VerifyCheckedWriterRejects(ReadOnlySpan<int> values, int bitWidth)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        try
        {
            try
            {
                RleBitPackingHybridEncoding.Write(values, bitWidth, ref writer);
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
        finally
        {
            writer.Dispose();
        }

        throw new InvalidOperationException($"Expected bit width {bitWidth} to reject an out-of-range literal.");
    }

    static byte[] EncodeDictionaryIndexes(ReadOnlySpan<int> values, int bitWidth)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        try
        {
            RleBitPackingHybridEncoding.WriteWithBitWidthPrefixUnchecked(values, bitWidth, ref writer);
            var result = new byte[writer.WrittenLength];
            writer.CopyTo(result);
            return result;
        }
        finally
        {
            writer.Dispose();
        }
    }

    static byte[] EncodeBooleans(ReadOnlySpan<bool> values)
    {
        var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 4096, 4096);
        try
        {
            RleBitPackingHybridEncoding.WriteBooleans(values, ref writer);
            var result = new byte[writer.WrittenLength];
            writer.CopyTo(result);
            return result;
        }
        finally
        {
            writer.Dispose();
        }
    }

    static void VerifyBooleans(bool[] values)
    {
        var actual = EncodeBooleans(values);
        var integerValues = Array.ConvertAll(values, static value => value ? 1 : 0);
        var expected = EncodeReference(integerValues, 1, includeBitWidthPrefix: false);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Boolean RLE/bit-packed output differs from the reference for {values.Length} values.");
    }

    static byte[] EncodeReference(ReadOnlySpan<int> values, int bitWidth, bool includeBitWidthPrefix)
    {
        var output = new List<byte>();
        if (includeBitWidthPrefix)
            output.Add((byte)bitWidth);
        if (values.Length == 0)
            return [.. output];

        var index = 0;
        while (index < values.Length)
        {
            var runLength = CountRunLength(values, index);
            if (runLength >= 8)
            {
                WriteRleReference(values[index], runLength, bitWidth, output);
                index += runLength;
                continue;
            }

            var literalStart = index;
            index += runLength;
            while (index < values.Length)
            {
                runLength = CountRunLength(values, index);
                if (runLength >= 8)
                {
                    var padding = (index - literalStart) & 7;
                    if (padding == 0)
                        break;

                    var take = Math.Min(runLength, 8 - padding);
                    index += take;
                    if (take < runLength)
                        break;
                    continue;
                }

                index += runLength;
            }

            WriteBitPackedReference(values[literalStart..index], bitWidth, output);
        }

        return [.. output];
    }

    static void WriteRleReference(int value, int runLength, int bitWidth, List<byte> output)
    {
        WriteUnsignedVarIntReference(((uint)runLength) << 1, output);
        var byteWidth = (bitWidth + 7) >> 3;
        var unsignedValue = unchecked((uint)value);
        for (var i = 0; i < byteWidth; i++)
            output.Add((byte)(unsignedValue >> (i * 8)));
    }

    static void WriteBitPackedReference(ReadOnlySpan<int> literals, int bitWidth, List<byte> output)
    {
        if (bitWidth == 0)
        {
            WriteRleReference(0, literals.Length, 0, output);
            return;
        }

        var groupCount = (literals.Length + 7) >> 3;
        WriteUnsignedVarIntReference((((uint)groupCount) << 1) | 1u, output);
        var packed = new byte[checked(groupCount * bitWidth)];
        var mask = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        var bitOffset = 0;
        for (var i = 0; i < literals.Length; i++)
        {
            var value = unchecked((uint)literals[i]) & mask;
            for (var bit = 0; bit < bitWidth; bit++)
                if (((value >> bit) & 1) != 0)
                    packed[(bitOffset + bit) >> 3] |= (byte)(1 << ((bitOffset + bit) & 7));
            bitOffset += bitWidth;
        }

        output.AddRange(packed);
    }

    static void WriteUnsignedVarIntReference(uint value, List<byte> output)
    {
        while (value >= 0x80)
        {
            output.Add((byte)(value | 0x80));
            value >>= 7;
        }

        output.Add((byte)value);
    }

    static int CountRunLength(ReadOnlySpan<int> values, int start)
    {
        var value = values[start];
        var length = 1;
        while (start + length < values.Length && values[start + length] == value)
            length++;
        return length;
    }

    static int[] CreateLiteralValues(int length, int bitWidth)
    {
        if (bitWidth == 0)
            return new int[length];

        var values = new int[length];
        var mask = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        uint state = 0x9E3779B9;
        var previous = uint.MaxValue;
        for (var i = 0; i < values.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            var value = state & mask;
            if (value == previous)
                value = (value + 1) & mask;
            values[i] = unchecked((int)value);
            previous = value;
        }

        return values;
    }

    static int[] CreateMixedValues(int bitWidth)
    {
        int[] runLengths = [1, 2, 7, 8, 9, 3, 16, 1, 7, 64, 9, 2];
        var values = new int[runLengths.Sum()];
        var mask = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        var offset = 0;
        for (var run = 0; run < runLengths.Length; run++)
        {
            var value = unchecked(((uint)(run + 1) * 0x9E3779B9u) & mask);
            values.AsSpan(offset, runLengths[run]).Fill(unchecked((int)value));
            offset += runLengths[run];
        }

        return values;
    }

    static int[] CreateRandomMixedValues(int length, int bitWidth, int alignment)
    {
        var values = new int[length];
        var mask = bitWidth == 32 ? uint.MaxValue : (1u << bitWidth) - 1u;
        var state = unchecked(0x9E3779B9u + (uint)alignment * 7919u + (uint)bitWidth);
        var previous = uint.MaxValue;
        var offset = 0;

        while (offset < alignment)
        {
            var value = GetDifferentRandomValue(mask, previous, ref state);
            values[offset++] = unchecked((int)value);
            previous = value;
        }

        while (offset < values.Length)
        {
            var selector = NextRandom(ref state) % 100;
            var runLength = selector switch
            {
                < 50 => 1,
                < 75 => 2 + (int)(NextRandom(ref state) % 6),
                < 90 => 8 + (int)(NextRandom(ref state) % 17),
                _ => 25 + (int)(NextRandom(ref state) % 72)
            };
            var value = GetDifferentRandomValue(mask, previous, ref state);
            values.AsSpan(offset, Math.Min(runLength, values.Length - offset)).Fill(unchecked((int)value));
            offset += runLength;
            previous = value;
        }

        return values;
    }

    static uint GetDifferentRandomValue(uint mask, uint previous, ref uint state)
    {
        var value = NextRandom(ref state) & mask;
        if (value == previous)
            value = (value + 1) & mask;
        return value;
    }

    static uint NextRandom(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    static bool[] CreateBooleanMixedValues()
    {
        int[] runLengths = [1, 2, 7, 8, 9, 3, 16, 1, 7, 64, 9, 2];
        var values = new bool[runLengths.Sum()];
        var offset = 0;
        for (var run = 0; run < runLengths.Length; run++)
        {
            values.AsSpan(offset, runLengths[run]).Fill((run & 1) == 0);
            offset += runLengths[run];
        }

        return values;
    }

    static bool[] CreateBooleanBoundaryValues()
    {
        int[] runLengths = [1, 7, 8, 9, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255];
        var values = new bool[runLengths.Sum()];
        var offset = 0;
        for (var run = 0; run < runLengths.Length; run++)
        {
            values.AsSpan(offset, runLengths[run]).Fill((run & 1) != 0);
            offset += runLengths[run];
        }
        return values;
    }

    static bool[] CreateRandomBooleanMixedValues(int length, int alignment)
    {
        var values = new bool[length];
        var state = unchecked(0x9E3779B9u + (uint)alignment * 7919u);
        var offset = 0;
        var value = false;

        if (alignment != 0)
        {
            values.AsSpan(0, alignment).Fill(value);
            offset = alignment;
            value = !value;
        }

        while (offset < values.Length)
        {
            var selector = NextRandom(ref state) % 100;
            var runLength = selector switch
            {
                < 35 => 1,
                < 60 => 2 + (int)(NextRandom(ref state) % 6),
                < 80 => 8 + (int)(NextRandom(ref state) % 25),
                _ => 33 + (int)(NextRandom(ref state) % 224)
            };
            values.AsSpan(offset, Math.Min(runLength, values.Length - offset)).Fill(value);
            offset += runLength;
            value = !value;
        }

        return values;
    }
}
