using System.Buffers.Binary;
using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class PlainByteArrayDecodingTests
{
    [Test]
    public void ValuesRoundTripAcrossRepetitionPageVersionsAndBoundaries()
    {
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        int[] counts = [0, 1, 31, 32, 33, 257];
        foreach (var pageVersion in pageVersions)
        foreach (var optional in new[] { false, true })
        foreach (var count in counts)
        {
            var expected = CreateValues(count, optional);
            var actual = RoundTrip(expected, optional, pageVersion);
            AssertEqual(expected, actual, optional, pageVersion, count);
        }
    }

    [Test]
    public void CorruptPayloadsAreRejectedAcrossRequiredAndOptionalPages()
    {
        PageHeaderType[] pageTypes = [PageHeaderType.DataPage, PageHeaderType.DataPageV2];
        foreach (var pageType in pageTypes)
        foreach (var optional in new[] { false, true })
        {
            AssertCorrupt([0, 0, 0], valueCount: 1, optional, pageType);
            AssertCorrupt([0x00, 0x00, 0x00, 0x80], valueCount: 1, optional, pageType);
            AssertCorrupt([0x02, 0, 0, 0, 0x2A], valueCount: 1, optional, pageType);
            AssertCorrupt([0, 0, 0, 0, 0x2A], valueCount: 1, optional, pageType);
        }
    }

    static byte[]?[] CreateValues(int count, bool optional)
    {
        var values = new byte[]?[count];
        for (var i = 0; i < values.Length; i++)
        {
            if (optional && (i % 7 == 0 || i is > 15 and < 20))
                continue;
            if (i % 5 == 0)
                values[i] = [];
            else
                values[i] = Enumerable.Range(0, i % 37 + 1)
                    .Select(value => unchecked((byte)(value + i))).ToArray();
        }
        return values;
    }

    static byte[]?[] RoundTrip(byte[]?[] values, bool optional, ParquetDataPageVersion pageVersion)
    {
        var schema = CreateSchema(optional, new FixedRowsPageStrategy(32));
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion
        });
        var serialized = writer.CreateSerializedColumn<byte[]?>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<byte[]?>(values.Length);
        foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
            for (var i = 0; i < buffer.Count; i++)
                actual.Add(buffer.IsNull(i) ? null : buffer.GetValue(i).ToArray());
        return actual.ToArray();
    }

    static void AssertCorrupt(byte[] dataPayload, uint valueCount, bool optional, PageHeaderType pageType)
    {
        var schema = CreateSchema(optional);
        byte[] definitionPayload = optional && valueCount != 0
            ? [checked((byte)(valueCount << 1)), 1]
            : [];
        var payload = new byte[definitionPayload.Length + dataPayload.Length];
        definitionPayload.CopyTo(payload, 0);
        dataPayload.CopyTo(payload, definitionPayload.Length);
        if (pageType == PageHeaderType.DataPage && optional)
        {
            var v1Payload = new byte[sizeof(int) + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(v1Payload, definitionPayload.Length);
            payload.CopyTo(v1Payload, sizeof(int));
            payload = v1Payload;
        }

        var header = new PageHeader(pageType, checked((uint)payload.Length), checked((uint)payload.Length),
            valueCount, EncodingKind.Plain, HeaderLength: 1, RepetitionLevelsByteLength: 0,
            DefinitionLevelsByteLength: pageType == PageHeaderType.DataPageV2
                ? checked((uint)definitionPayload.Length)
                : 0,
            NullCount: 0, IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: EncodingKind.Rle, RowCount: valueCount);
        var buffers = default(ColumnReadBuffers<BinaryValueDescriptor>);
        try
        {
            Assert.Throws<CorruptParquetException>(() =>
            {
                if (optional)
                    ColumnChunkReader.TryDecodeNullablePageIntoNative(
                        header, payload, schema.LeafColumns[0].Column, valueCount, ref buffers,
                        DefaultParquetBufferPool.Shared, out _);
                else
                    ColumnChunkReader.TryDecodeRequiredPageIntoNative(
                        header, payload, schema.LeafColumns[0].Column, valueCount, ref buffers,
                        DefaultParquetBufferPool.Shared, out _);
            });
        }
        finally
        {
            buffers.Dispose();
        }
    }

    static ParquetSchema CreateSchema(bool optional, IPageStrategy? pageStrategy = null)
    {
        var options = new ColumnOptions(
            optional ? ParquetRepetition.Optional : ParquetRepetition.Required,
            ImmutableArray.Create(EncodingKind.Plain));
        return new([
            ColumnDefinition.Leaf("value", ParquetPhysicalType.ByteArray, options,
                pageStrategy: pageStrategy)
        ]);
    }

    static void AssertEqual(ReadOnlySpan<byte[]?> expected, ReadOnlySpan<byte[]?> actual,
        bool optional, ParquetDataPageVersion pageVersion, int count)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"{pageVersion}/optional={optional}/count={count}: expected {expected.Length}, got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] is null != actual[i] is null ||
                expected[i] is { } value && !value.AsSpan().SequenceEqual(actual[i]))
                throw new InvalidOperationException(
                    $"{pageVersion}/optional={optional}/count={count}: mismatch at {i}.");
        }
    }

    sealed class FixedRowsPageStrategy(uint rowsPerPage) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode()
            => DictionaryMode.Disabled;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten)
            => Math.Min(rowsPerPage, totalRowCount - rowsWritten);
    }
}
