using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class NullableNumericDecodingTests
{
    static readonly EncodingKind[] Encodings =
    [
        EncodingKind.Plain,
        EncodingKind.RleDictionary,
        EncodingKind.ByteStreamSplit
    ];

    [Test]
    public void NullableNumericsPreserveValuesAcrossEncodingsPagesAndNullPatterns()
    {
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        foreach (var pageVersion in pageVersions)
        foreach (var encoding in Encodings)
        foreach (var pattern in Enum.GetValues<NullPattern>())
        {
            AssertEqual(CreateValues<int>(pattern, CreateInt32),
                RoundTrip(ParquetPhysicalType.Int32, CreateValues<int>(pattern, CreateInt32),
                    encoding, pageVersion), encoding, pageVersion, pattern);
            AssertEqual(CreateValues<long>(pattern, CreateInt64),
                RoundTrip(ParquetPhysicalType.Int64, CreateValues<long>(pattern, CreateInt64),
                    encoding, pageVersion), encoding, pageVersion, pattern);
            AssertEqual(CreateValues<float>(pattern, CreateFloat),
                RoundTrip(ParquetPhysicalType.Float, CreateValues<float>(pattern, CreateFloat),
                    encoding, pageVersion), encoding, pageVersion, pattern);
            AssertEqual(CreateValues<double>(pattern, CreateDouble),
                RoundTrip(ParquetPhysicalType.Double, CreateValues<double>(pattern, CreateDouble),
                    encoding, pageVersion), encoding, pageVersion, pattern);
        }
    }

    [Test]
    public void NullableInt32ReadsLegacyBitPackedDefinitionLevels()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32, EncodingKind.Plain);
        var column = schema.LeafColumns[0].Column;
        byte[] payload = new byte[1 + 4 * sizeof(int)];
        payload[0] = 0b1011_0010;
        int[] physicalValues = [10, 20, 30, 40];
        for (var i = 0; i < physicalValues.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1 + i * sizeof(int)), physicalValues[i]);
        var header = CreateHeader(PageHeaderType.DataPage, payload.Length, valueCount: 8,
            EncodingKind.Plain, definitionByteLength: 0, nullCount: 4,
            definitionEncoding: EncodingKind.BitPacked);
        var buffers = default(ColumnReadBuffers<int?>);
        try
        {
            var decoded = ColumnChunkReader.TryDecodeNullablePageIntoNative(header, payload, column,
                rowCount: 8, ref buffers, DefaultParquetBufferPool.Shared, out var buffer);
            if (!decoded)
                throw new InvalidOperationException("The native nullable numeric path declined the page.");
            int?[] expected = [10, null, 20, 30, null, null, 40, null];
            AssertEqual(expected, buffer.Values, EncodingKind.Plain, ParquetDataPageVersion.V1,
                NullPattern.Mixed);
        }
        finally
        {
            buffers.Dispose();
        }
    }

    [Test]
    public void NullableNumericsRejectCorruptDefinitionLevels()
    {
        var schema = CreateSchema(ParquetPhysicalType.Int32, EncodingKind.Plain);
        var column = schema.LeafColumns[0].Column;

        AssertCorrupt(column, definitionPayload: [0x03], valueCount: 8, nullCount: 8);
        AssertCorrupt(column, definitionPayload: [0x10, 0x02], valueCount: 8, nullCount: 0);
        AssertCorrupt(column, definitionPayload: [0x08, 0x01], valueCount: 4, nullCount: 1,
            physicalPayload: new byte[4 * sizeof(int)]);
    }

    [Test]
    public void NullableNumericRetainedPageSurvivesAdvancementAndReaderDisposal()
    {
        var schema = CreateSchema(ParquetPhysicalType.Double, EncodingKind.ByteStreamSplit);
        var expected = CreateValues<double>(NullPattern.Mixed, CreateDouble);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = ParquetDataPageVersion.V2
        });
        var serialized = writer.CreateSerializedColumn<double?>(schema.LeafColumns[0]);
        serialized.Serialize(expected);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        ParquetBuffer retained = default;
        double?[] firstPage;
        try
        {
            var input = new MemoryReadSource(stream.ToArray());
            using (var reader = schema.CreateReader(input))
            {
                var buffers = reader.RowGroups[0].Column<double?>(0).GetEnumerator();
                try
                {
                    if (!buffers.MoveNext())
                        throw new InvalidOperationException("Expected a nullable numeric value buffer.");
                    firstPage = buffers.Current.Values.ToArray();
                    retained = buffers.Current.Retain();
                    if (!buffers.MoveNext())
                        throw new InvalidOperationException("Expected multiple nullable numeric pages.");
                }
                finally
                {
                    buffers.Dispose();
                }
            }

            if (!retained.AsSpan<double?>().SequenceEqual(firstPage))
                throw new InvalidOperationException(
                    "Retained nullable numeric page changed after advancing and disposing the reader.");
        }
        finally
        {
            retained.Dispose();
        }
    }

    [Test]
    public async Task LargeNullableLongAndDoublePagesPreserveBitsOnWorkerThreads()
    {
        const int count = 65_539;
        var longs = CreateLargeValues<long>(count, index => unchecked((long)((ulong)index *
            0x9E37_79B9_7F4A_7C15UL)));
        var noNullLongs = CreateLargeValues<long>(count, index => unchecked((long)((ulong)index *
            0xD6E8_FEB8_6659_FD93UL)), includeNulls: false);
        var allNullLongs = new long?[count];
        var doubles = CreateLargeValues<double>(count, index => BitConverter.Int64BitsToDouble(index switch
        {
            1 => unchecked((long)0x8000_0000_0000_0000UL),
            2 => 0x7FF8_0000_0000_0001L,
            3 => 0x7FF8_0000_0000_0002L,
            4 => 0x7FF0_0000_0000_0000L,
            5 => unchecked((long)0xFFF0_0000_0000_0000UL),
            _ => unchecked((long)(0x3FF0_0000_0000_0000UL ^
                                  ((ulong)index * 0x0000_0001_0001_0001UL)))
        }));

        foreach (var encoding in Encodings)
        {
            var actualLongs = await Task.Run(() => RoundTripSinglePage(
                ParquetPhysicalType.Int64, longs, encoding)).ConfigureAwait(false);
            AssertEqual(longs, actualLongs,
                encoding, ParquetDataPageVersion.V2, NullPattern.Mixed);
            var actualDoubles = await Task.Run(() => RoundTripSinglePage(
                ParquetPhysicalType.Double, doubles, encoding)).ConfigureAwait(false);
            AssertEqual(doubles, actualDoubles,
                encoding, ParquetDataPageVersion.V2, NullPattern.Mixed);
            var actualNoNullLongs = await Task.Run(() => RoundTripSinglePage(
                ParquetPhysicalType.Int64, noNullLongs, encoding)).ConfigureAwait(false);
            AssertEqual(noNullLongs, actualNoNullLongs,
                encoding, ParquetDataPageVersion.V2, NullPattern.NoNulls);
            var actualAllNullLongs = await Task.Run(() => RoundTripSinglePage(
                ParquetPhysicalType.Int64, allNullLongs, encoding)).ConfigureAwait(false);
            AssertEqual(allNullLongs, actualAllNullLongs,
                encoding, ParquetDataPageVersion.V2, NullPattern.AllNull);
        }
    }

    [Test]
    public async Task ConcurrentLargeNullablePagesAreVisibleAfterDecodeAndRetention()
    {
        const int count = 524_291;
        const int readerCount = 8;
        var expected = CreateLargeValues<double>(count, index => BitConverter.Int64BitsToDouble(index switch
        {
            1 => unchecked((long)0x8000_0000_0000_0000UL),
            2 => 0x7FF8_0000_0000_0001L,
            3 => 0x7FF8_0000_0000_0002L,
            _ => unchecked((long)((ulong)index * 0xD6E8_FEB8_6659_FD93UL))
        }));
        var (schema, bytes) = WriteSinglePage(ParquetPhysicalType.Double, expected,
            EncodingKind.ByteStreamSplit);
        var pool = new OffsetBufferPool();
        using var start = new ManualResetEventSlim();
        var reads = new Task<ParquetBuffer>[readerCount];
        for (var i = 0; i < reads.Length; i++)
            reads[i] = Task.Run(() =>
            {
                start.Wait();
                return ReadRetainedSinglePage<double>(schema, bytes, pool);
            });
        start.Set();

        var retained = await Task.WhenAll(reads).ConfigureAwait(false);
        try
        {
            foreach (var buffer in retained)
                AssertEqual(expected, buffer.AsSpan<double?>(), EncodingKind.ByteStreamSplit,
                    ParquetDataPageVersion.V2, NullPattern.Mixed);
        }
        finally
        {
            foreach (var buffer in retained)
                buffer.Dispose();
        }
    }

    static void AssertCorrupt(Column column, byte[] definitionPayload, uint valueCount, uint nullCount,
        byte[]? physicalPayload = null)
    {
        physicalPayload ??= [];
        var payload = new byte[definitionPayload.Length + physicalPayload.Length];
        definitionPayload.CopyTo(payload, 0);
        physicalPayload.CopyTo(payload, definitionPayload.Length);
        var header = CreateHeader(PageHeaderType.DataPageV2, payload.Length, valueCount,
            EncodingKind.Plain, checked((uint)definitionPayload.Length), nullCount,
            EncodingKind.Rle);
        var buffers = default(ColumnReadBuffers<int?>);
        try
        {
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryDecodeNullablePageIntoNative(header, payload, column,
                    rowCount: valueCount, ref buffers, DefaultParquetBufferPool.Shared, out _));
        }
        finally
        {
            buffers.Dispose();
        }
    }

    static ParquetSchema CreateSchema(ParquetPhysicalType physicalType, EncodingKind encoding)
    {
        var options = new ColumnOptions(ParquetRepetition.Optional,
            ImmutableArray.Create(encoding));
        return new([
            ColumnDefinition.OptionalLeaf("value", physicalType, options,
                pageStrategy: new FixedRowsPageStrategy(rowsPerPage: 31,
                    dictionary: encoding == EncodingKind.RleDictionary))
        ]);
    }

    static ParquetSchema CreateSinglePageSchema(ParquetPhysicalType physicalType, EncodingKind encoding)
        => new([
            ColumnDefinition.OptionalLeaf("value", physicalType,
                new ColumnOptions(ParquetRepetition.Optional, ImmutableArray.Create(encoding)),
                pageStrategy: encoding == EncodingKind.RleDictionary
                    ? ForceDictionaryPageStrategy.Shared
                    : new FixedRowsPageStrategy(uint.MaxValue, dictionary: false))
        ]);

    static T?[] RoundTripSinglePage<T>(ParquetPhysicalType physicalType, T?[] values,
        EncodingKind encoding)
        where T : struct
    {
        var (schema, bytes) = WriteSinglePage(physicalType, values, encoding);
        using var reader = schema.CreateReader(new MemoryReadSource(bytes));
        var actual = new List<T?>(values.Length);
        foreach (var buffer in reader.RowGroups[0].Column<T?>(0))
            actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static (ParquetSchema Schema, byte[] Bytes) WriteSinglePage<T>(ParquetPhysicalType physicalType,
        T?[] values, EncodingKind encoding)
        where T : struct
    {
        var schema = CreateSinglePageSchema(physicalType, encoding);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = ParquetDataPageVersion.V2
        });
        var serialized = writer.CreateSerializedColumn<T?>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();
        return (schema, stream.ToArray());
    }

    static ParquetBuffer ReadRetainedSinglePage<T>(ParquetSchema schema, byte[] bytes,
        IParquetBufferPool? bufferPool = null)
        where T : struct
    {
        var options = bufferPool is null ? null : new ParquetReaderOptions { BufferPool = bufferPool };
        using var reader = schema.CreateReader(new MemoryReadSource(bytes), options);
        var buffers = reader.RowGroups[0].Column<T?>(0).GetEnumerator();
        try
        {
            if (!buffers.MoveNext())
                throw new InvalidOperationException("Expected one nullable numeric page.");
            return buffers.Current.Retain();
        }
        finally
        {
            buffers.Dispose();
        }
    }

    static T?[] CreateLargeValues<T>(int count, Func<int, T> factory, bool includeNulls = true)
        where T : struct
    {
        var values = new T?[count];
        for (var i = 0; i < values.Length; i++)
            if (!includeNulls ||
                i % 31 != 0 && i is not 1_023 and not 32_768 && i != values.Length - 1)
                values[i] = factory(i);
        return values;
    }

    static T?[] RoundTrip<T>(ParquetPhysicalType physicalType, T?[] values, EncodingKind encoding,
        ParquetDataPageVersion pageVersion)
        where T : struct
    {
        var schema = CreateSchema(physicalType, encoding);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion
        });
        var serialized = writer.CreateSerializedColumn<T?>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<T?>(values.Length);
        foreach (var buffer in reader.RowGroups[0].Column<T?>(0))
            actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static T?[] CreateValues<T>(NullPattern pattern, Func<int, T> factory)
        where T : struct
    {
        var values = new T?[257];
        for (var i = 0; i < values.Length; i++)
        {
            if (pattern == NullPattern.AllNull ||
                pattern == NullPattern.Mixed &&
                (i < 17 || i >= 241 || i % 7 == 0 || i is >= 93 and < 128))
                continue;
            values[i] = factory(i);
        }
        return values;
    }

    static int CreateInt32(int index)
        => unchecked((int)((uint)index * 0x9E37_79B9u) ^ (index % 19));

    static long CreateInt64(int index)
        => unchecked((long)((ulong)(uint)index * 0x9E37_79B9_7F4A_7C15UL) ^ (uint)(index % 23));

    static float CreateFloat(int index)
        => (index - 128) * 0.125f + index % 11 * 0.0009765625f;

    static double CreateDouble(int index)
        => (index - 128L) * 0.125 + index % 13 * 0.00000095367431640625;

    static void AssertEqual<T>(ReadOnlySpan<T?> expected, ReadOnlySpan<T?> actual,
        EncodingKind encoding, ParquetDataPageVersion pageVersion, NullPattern pattern)
        where T : struct
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"{pageVersion}/{encoding}/{typeof(T).Name}/{pattern}: expected {expected.Length} values, " +
                $"got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i].HasValue != actual[i].HasValue)
                throw new InvalidOperationException(
                    $"{pageVersion}/{encoding}/{typeof(T).Name}/{pattern}: null mismatch at {i}.");
            if (expected[i] is { } expectedValue && actual[i] is { } actualValue &&
                !BitsEqual(expectedValue, actualValue))
                throw new InvalidOperationException(
                    $"{pageVersion}/{encoding}/{typeof(T).Name}/{pattern}: value mismatch at {i}.");
        }
    }

    static bool BitsEqual<T>(T left, T right)
        where T : struct
    {
        if (typeof(T) == typeof(float))
            return BitConverter.SingleToInt32Bits((float)(object)left) ==
                BitConverter.SingleToInt32Bits((float)(object)right);
        if (typeof(T) == typeof(double))
            return BitConverter.DoubleToInt64Bits((double)(object)left) ==
                BitConverter.DoubleToInt64Bits((double)(object)right);
        return EqualityComparer<T>.Default.Equals(left, right);
    }

    static PageHeader CreateHeader(PageHeaderType type, int payloadLength, uint valueCount,
        EncodingKind encoding, uint definitionByteLength, uint nullCount,
        EncodingKind definitionEncoding)
        => new(type, checked((uint)payloadLength), checked((uint)payloadLength), valueCount, encoding,
            HeaderLength: 1, RepetitionLevelsByteLength: 0,
            DefinitionLevelsByteLength: definitionByteLength, NullCount: nullCount,
            IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
            DefinitionLevelEncoding: definitionEncoding, RowCount: valueCount);

    enum NullPattern
    {
        NoNulls,
        AllNull,
        Mixed
    }

    sealed class FixedRowsPageStrategy(uint rowsPerPage, bool dictionary) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode()
            => dictionary ? DictionaryMode.Forced : DictionaryMode.Disabled;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
            => currentPageRowCount >= rowsPerPage;
    }

    sealed class OffsetBufferPool : IParquetBufferPool
    {
        public ParquetBuffer Rent(uint minimumByteLength)
        {
            var allocationByteLength = checked((int)minimumByteLength + 128);
            var allocation = Marshal.AllocHGlobal(allocationByteLength);
            var allocationOffset = (int)((long)allocation & 63);
            var payloadOffset = 64 + ((16 - allocationOffset) & 63);
            return ParquetBuffer.Create(allocation, allocationByteLength, payloadOffset,
                checked((int)minimumByteLength), Marshal.FreeHGlobal);
        }
    }
}
