using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class Dictionary11BitDecodingTests
{
    [Test]
    public void RequiredAndOptionalInt32AndInt64RoundTripAcrossPageVersions()
    {
        foreach (var pageVersion in new[] { ParquetDataPageVersion.V1, ParquetDataPageVersion.V2 })
        {
            var int32Values = Enumerable.Range(0, 4_113)
                .Select(index => unchecked((index & 2_047) * 1_000_003)).ToArray();
            var int64Values = Enumerable.Range(0, 4_113)
                .Select(index => unchecked((long)(index & 2_047) * 1_000_000_007L)).ToArray();
            AssertRoundTrip(int32Values, ParquetPhysicalType.Int32, pageVersion);
            AssertRoundTrip(int64Values, ParquetPhysicalType.Int64, pageVersion);
            AssertRoundTrip(int32Values.Select((value, index) => index % 7 == 0 ? null : (int?)value).ToArray(),
                ParquetPhysicalType.Int32, pageVersion);
            AssertRoundTrip(int64Values.Select((value, index) => index % 7 == 0 ? null : (long?)value).ToArray(),
                ParquetPhysicalType.Int64, pageVersion);
        }
    }

    [Test]
    public void LiteralDecoderHandlesVectorAndScalarBoundariesAndMixedRleRuns()
    {
        var intDictionary = Enumerable.Range(0, 2_048).Select(index => index * 17).ToArray();
        var longDictionary = Enumerable.Range(0, 2_048).Select(index => (long)index * 19).ToArray();
        foreach (var count in new[] { 0, 1, 7, 8, 9, 31, 32, 33 })
        {
            var indexes = Enumerable.Range(0, count).Select(index => index * 61 & 2_047).ToArray();
            AssertDecoded(intDictionary, ParquetPhysicalType.Int32, EncodeLiteral(indexes, 11), indexes);
            AssertDecoded(longDictionary, ParquetPhysicalType.Int64, EncodeLiteral(indexes, 11), indexes);
        }

        int[] mixedIndexes = [23, 23, 23, 23, 23, 23, 23, 23, 0, 511, 1_023, 1_024, 1_535, 2_047, 9, 17];
        var mixedPayload = new byte[1 + 1 + 2 + 1 + 11];
        mixedPayload[0] = 11;
        mixedPayload[1] = 16;
        mixedPayload[2] = 23;
        mixedPayload[3] = 0;
        mixedPayload[4] = 3;
        Pack(mixedIndexes.AsSpan(8), 11).CopyTo(mixedPayload, 5);
        AssertDecoded(intDictionary, ParquetPhysicalType.Int32, mixedPayload, mixedIndexes);
        AssertDecoded(longDictionary, ParquetPhysicalType.Int64, mixedPayload, mixedIndexes);
    }

    [Test]
    public void WideLiteralDecoderHandlesVectorAndScalarBoundaries()
    {
        foreach (var bitWidth in new[] { 19, 20 })
        {
            var dictionaryLength = 1 << bitWidth;
            var dictionary = Enumerable.Range(0, dictionaryLength)
                .Select(index => unchecked((long)index * 1_000_000_007L)).ToArray();
            foreach (var count in new[] { 8, 9, 31, 32, 33 })
            {
                var indexes = Enumerable.Range(0, count)
                    .Select(index => unchecked(index * 104_729) & (dictionaryLength - 1)).ToArray();
                indexes[^1] = dictionaryLength - 1;
                AssertDecoded(dictionary, ParquetPhysicalType.Int64,
                    EncodeLiteral(indexes, bitWidth), indexes);
            }
        }
    }

    [Test]
    public void CorruptIndexesAndTruncatedLiteralsAreRejectedWhileFallbacksRemainCorrect()
    {
        var intDictionary = Enumerable.Range(0, 2_047).ToArray();
        int[] invalidVector = [0, 1, 2, 3, 4, 5, 6, 2_047];
        Assert.Throws<CorruptParquetException>(() =>
            DecodeRequired(intDictionary, ParquetPhysicalType.Int32, EncodeLiteral(invalidVector, 11), 8));

        int[] invalidTail = [0, 1, 2, 3, 4, 5, 6, 7, 2_047];
        Assert.Throws<CorruptParquetException>(() =>
            DecodeRequired(intDictionary, ParquetPhysicalType.Int32, EncodeLiteral(invalidTail, 11), 9));

        foreach (var bitWidth in new[] { 19, 20 })
        {
            var dictionaryLength = (1 << bitWidth) - 1;
            var dictionary = new long[dictionaryLength];
            var wideInvalidVector = Enumerable.Range(0, 8).ToArray();
            wideInvalidVector[^1] = dictionaryLength;
            Assert.Throws<CorruptParquetException>(() =>
                DecodeRequired(dictionary, ParquetPhysicalType.Int64,
                    EncodeLiteral(wideInvalidVector, bitWidth), wideInvalidVector.Length));

            var wideInvalidTail = Enumerable.Range(0, 9).ToArray();
            wideInvalidTail[^1] = dictionaryLength;
            Assert.Throws<CorruptParquetException>(() =>
                DecodeRequired(dictionary, ParquetPhysicalType.Int64,
                    EncodeLiteral(wideInvalidTail, bitWidth), wideInvalidTail.Length));

            var wideTruncated = EncodeLiteral(Enumerable.Range(0, 8).ToArray(), bitWidth)[..^1];
            Assert.Throws<CorruptParquetException>(() =>
                DecodeRequired(dictionary, ParquetPhysicalType.Int64, wideTruncated, 8));
        }

        var truncated = EncodeLiteral(Enumerable.Range(0, 8).ToArray(), 11)[..^1];
        Assert.Throws<CorruptParquetException>(() =>
            DecodeRequired(intDictionary, ParquetPhysicalType.Int32, truncated, 8));

        var tenBitDictionary = Enumerable.Range(0, 1_024).Select(index => index * 29).ToArray();
        int[] tenBitIndexes = [0, 1, 511, 512, 1_023, 17, 33, 65, 129];
        AssertDecoded(tenBitDictionary, ParquetPhysicalType.Int32,
            EncodeLiteral(tenBitIndexes, 10), tenBitIndexes);

        var doubleDictionary = Enumerable.Range(0, 2_048).Select(index => index * 0.25).ToArray();
        int[] doubleIndexes = [0, 1, 1_023, 1_024, 2_047, 17, 33, 65, 129];
        AssertDecoded(doubleDictionary, ParquetPhysicalType.Double,
            EncodeLiteral(doubleIndexes, 11), doubleIndexes);
    }

    static void AssertRoundTrip<T>(T[] expected, ParquetPhysicalType physicalType,
        ParquetDataPageVersion pageVersion)
    {
        var optional = default(T) is null;
        var options = new ColumnOptions(optional ? ParquetRepetition.Optional : ParquetRepetition.Required,
            ImmutableArray.Create(EncodingKind.RleDictionary));
        var schema = new ParquetSchema([
            optional
                ? ColumnDefinition.OptionalLeaf("value", physicalType, options,
                    pageStrategy: ForceDictionaryPageStrategy.Shared)
                : ColumnDefinition.RequiredLeaf("value", physicalType, options,
                    pageStrategy: ForceDictionaryPageStrategy.Shared)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<T>(expected.Length);
        foreach (var buffer in reader.RowGroups[0].Column<T>(0))
            actual.AddRange(buffer.Values);
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"{typeof(T).Name}/{pageVersion} dictionary values did not round-trip.");
    }

    static void AssertDecoded<T>(T[] dictionary, ParquetPhysicalType physicalType, byte[] payload,
        int[] indexes) where T : unmanaged
    {
        var actual = DecodeRequired(dictionary, physicalType, payload, indexes.Length);
        var expected = indexes.Select(index => dictionary[index]).ToArray();
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"{typeof(T).Name} dictionary values did not decode correctly.");
    }

    static T[] DecodeRequired<T>(T[] dictionary, ParquetPhysicalType physicalType, byte[] payload,
        int valueCount) where T : unmanaged
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", physicalType,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ]);
        var column = schema.LeafColumns[0].Column;
        var buffers = default(ColumnReadBuffers<T>);
        try
        {
            var dictionaryPayload = MemoryMarshal.AsBytes(dictionary.AsSpan()).ToArray();
            var dictionaryHeader = CreateHeader(PageHeaderType.DictionaryPage, dictionary.Length,
                EncodingKind.Plain, dictionaryPayload.Length);
            if (!ColumnChunkReader.TryDecodeDictionaryPageIntoNative(dictionaryHeader, dictionaryPayload,
                    column, ref buffers, DefaultParquetBufferPool.Shared))
                throw new InvalidOperationException("Dictionary page was not decoded by the native path.");
            var dataHeader = CreateHeader(PageHeaderType.DataPage, valueCount,
                EncodingKind.RleDictionary, payload.Length);
            if (!ColumnChunkReader.TryDecodeRequiredPageIntoNative(dataHeader, payload, column,
                    checked((uint)valueCount), ref buffers, DefaultParquetBufferPool.Shared, out var values))
                throw new InvalidOperationException("Data page was not decoded by the native path.");
            return values.Values.ToArray();
        }
        finally
        {
            buffers.Dispose();
        }
    }

    static PageHeader CreateHeader(PageHeaderType type, int valueCount, EncodingKind encoding,
        int payloadLength)
        => new(type, checked((uint)payloadLength), checked((uint)payloadLength), checked((uint)valueCount), encoding,
            HeaderLength: 1, RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: 0, NullCount: 0,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: checked((uint)valueCount));

    static byte[] EncodeLiteral(ReadOnlySpan<int> indexes, int bitWidth)
    {
        var groupCount = (indexes.Length + 7) / 8;
        var padded = new int[groupCount * 8];
        indexes.CopyTo(padded);
        var packed = Pack(padded, bitWidth);
        var payload = new byte[2 + packed.Length];
        payload[0] = checked((byte)bitWidth);
        payload[1] = checked((byte)(groupCount * 2 + 1));
        packed.CopyTo(payload, 2);
        return payload;
    }

    static byte[] Pack(ReadOnlySpan<int> indexes, int bitWidth)
    {
        var result = new byte[(indexes.Length * bitWidth + 7) / 8];
        ulong buffer = 0;
        var bufferedBits = 0;
        var byteIndex = 0;
        foreach (var index in indexes)
        {
            buffer |= (ulong)(uint)index << bufferedBits;
            bufferedBits += bitWidth;
            while (bufferedBits >= 8)
            {
                result[byteIndex++] = (byte)buffer;
                buffer >>= 8;
                bufferedBits -= 8;
            }
        }
        if (bufferedBits != 0)
            result[byteIndex] = (byte)buffer;
        return result;
    }
}
