using System.Buffers.Binary;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Reading.Physical;
using Plank.Schema;

namespace Plank.Tests.Reading;

internal sealed class BooleanRleDecodingTests
{
    [Test]
    public void RequiredPageDecodesLiteralRun()
    {
        var expected = CreateAlternatingValues(257);

        VerifyDecodedValues(expected, EncodeLiteralRun(expected));
    }

    [Test]
    public void RequiredPageDecodesRepeatedRun()
    {
        var expected = Enumerable.Repeat(true, 64).ToArray();

        VerifyDecodedValues(expected, [0x80, 0x01, 0x01]);
    }

    [Test]
    public void RequiredPageDecodesMixedLiteralAndRepeatedRuns()
    {
        bool[] expected =
        [
            true, false, true, false, true, false, true, false,
            true, true, true, true, true, true, true, true,
            true, true, true, true, true, true, true, true,
            false, true, false, true, false, true, false, true
        ];

        VerifyDecodedValues(expected, [0x03, 0x55, 0x20, 0x01, 0x03, 0xAA]);
    }

    [Test]
    public void RequiredPageDecodesLiteralRunsAtPackedAndVectorBoundaries()
    {
        int[] lengths = [1, 7, 8, 9, 31, 32, 33, 63, 64, 65, 255, 256, 257];
        foreach (var length in lengths)
        {
            var expected = CreateAlternatingValues(length);
            VerifyDecodedValues(expected, EncodeLiteralRun(expected));
        }
    }

    [Test]
    public void RequiredPageRejectsTruncatedLiteralRunBeforeUnpacking()
    {
        int[] valueCounts = [8, 16];
        foreach (var valueCount in valueCounts)
        {
            var exception = Assert.Throws<CorruptParquetException>(() =>
                DecodeRequiredPage(valueCount, [0x05, 0xFF]));

            if (!exception.Message.Contains("claims 2 bytes but only 1 remain", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected corruption message: {exception.Message}");
        }
    }

    [Test]
    public void RequiredPageRejectsEmptyLiteralRun()
    {
        var exception = Assert.Throws<CorruptParquetException>(() =>
            DecodeRequiredPage(8, [0x01, 0x10, 0x01]));

        if (!exception.Message.Contains("empty bit-packed run", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unexpected corruption message: {exception.Message}");
    }

    static void VerifyDecodedValues(bool[] expected, byte[] encoded)
    {
        var actual = DecodeRequiredPage(expected.Length, encoded);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"Decoded Boolean RLE values differ for an input containing {expected.Length} values.");
    }

    static bool[] DecodeRequiredPage(int valueCount, byte[] encoded)
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Boolean)
        ]);
        var column = schema.LeafColumns[0].Column;
        var payload = new byte[sizeof(int) + encoded.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, encoded.Length);
        encoded.CopyTo(payload, sizeof(int));
        var header = new PageHeader(PageHeaderType.DataPage, checked((uint)payload.Length),
            checked((uint)payload.Length), checked((uint)valueCount), EncodingKind.Rle,
            HeaderLength: 1, RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: 0,
            NullCount: 0, IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: checked((uint)valueCount));
        var buffers = default(ColumnReadBuffers<bool>);

        try
        {
            if (!ColumnChunkReader.TryDecodeRequiredPageIntoNative(
                    header, payload, column, checked((ulong)valueCount), ref buffers,
                    DefaultParquetBufferPool.Shared, out var values))
                throw new InvalidOperationException("Expected native Boolean RLE decoding.");
            return values.Values.ToArray();
        }
        finally
        {
            buffers.Dispose();
        }
    }

    static byte[] EncodeLiteralRun(ReadOnlySpan<bool> values)
    {
        var groupCount = (values.Length + 7) / 8;
        if (groupCount >= 64)
            throw new ArgumentOutOfRangeException(nameof(values));
        var encoded = new byte[groupCount + 1];
        encoded[0] = checked((byte)((groupCount << 1) | 1));
        for (var i = 0; i < values.Length; i++)
            if (values[i])
                encoded[1 + (i >> 3)] |= checked((byte)(1 << (i & 7)));
        return encoded;
    }

    static bool[] CreateAlternatingValues(int length)
    {
        var values = new bool[length];
        for (var i = 0; i < values.Length; i++)
            values[i] = (i & 1) == 0;
        return values;
    }
}
