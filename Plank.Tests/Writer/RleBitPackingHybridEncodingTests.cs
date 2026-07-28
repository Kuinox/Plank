using Plank.Writing;
using Plank.Writing.Encoding;

namespace Plank.Tests.Writer;

internal sealed class RleBitPackingHybridEncodingTests
{
    [Test]
    public void DictionaryIndexesMatchReferenceAcrossBitWidthsAndBoundaries()
    {
        int[] bitWidths = [0, 1, 2, 7, 8, 9, 16, 24, 31, 32];
        int[] lengths = [0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 127];
        foreach (var bitWidth in bitWidths)
        {
            foreach (var length in lengths)
                VerifyDictionaryIndexes(CreateLiteralValues(length, bitWidth), bitWidth);

            if (bitWidth > 0)
                VerifyDictionaryIndexes(CreateMixedValues(bitWidth), bitWidth);
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
            CreateBooleanMixedValues()
        ];

        foreach (var input in inputs)
        {
            var actual = EncodeBooleans(input);
            var integerValues = Array.ConvertAll(input, static value => value ? 1 : 0);
            var expected = EncodeReference(integerValues, 1, includeBitWidthPrefix: false);
            if (!actual.SequenceEqual(expected))
                throw new InvalidOperationException(
                    $"Boolean RLE/bit-packed output differs from the reference for {input.Length} values.");
        }
    }

    static void VerifyDictionaryIndexes(int[] values, int bitWidth)
    {
        var actual = EncodeDictionaryIndexes(values, bitWidth);
        var expected = EncodeReference(values, bitWidth, includeBitWidthPrefix: true);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"RLE/bit-packed output differs from the reference for bit width {bitWidth} and {values.Length} values.");
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
}
