using System.Buffers.Binary;
using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.Encoding;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class NonPlainTimestampDecodingTests
{
    static readonly EncodingKind[] Encodings =
    [
        EncodingKind.RleDictionary,
        EncodingKind.DeltaBinaryPacked,
        EncodingKind.ByteStreamSplit
    ];

    [Test]
    public void RequiredDateTimesPreserveUnitsKindsAndBoundaries()
    {
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        foreach (var pageVersion in pageVersions)
        foreach (var encoding in Encodings)
        foreach (var unit in Enum.GetValues<TimeUnit>())
        foreach (var isAdjustedToUtc in new[] { false, true })
        {
            var expected = CreateValues(unit, isAdjustedToUtc);
            var actual = RoundTrip(expected, unit, isAdjustedToUtc, encoding, pageVersion);
            AssertEqual(expected, actual, unit, isAdjustedToUtc, encoding, pageVersion);
        }
    }

    [Test]
    public void LargeRequiredPlainAndByteStreamSplitDateTimesPreserveUnitsKindsAndBatchBoundaries()
    {
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        EncodingKind[] encodings = [EncodingKind.Plain, EncodingKind.ByteStreamSplit];
        foreach (var pageVersion in pageVersions)
        foreach (var encoding in encodings)
        foreach (var unit in Enum.GetValues<TimeUnit>())
        foreach (var isAdjustedToUtc in new[] { false, true })
        {
            var expected = CreateLargeValues(unit, isAdjustedToUtc);
            var actual = RoundTrip(expected, unit, isAdjustedToUtc, encoding, pageVersion);
            AssertEqual(expected, actual, unit, isAdjustedToUtc, encoding, pageVersion);
        }
    }

    [Test]
    public void LargeOptionalDateTimesPreserveUnitsKindsNullsAndBatchBoundaries()
    {
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        EncodingKind[] encodings =
            [EncodingKind.Plain, EncodingKind.DeltaBinaryPacked, EncodingKind.ByteStreamSplit];
        foreach (var pageVersion in pageVersions)
        foreach (var encoding in encodings)
        foreach (var unit in Enum.GetValues<TimeUnit>())
        foreach (var isAdjustedToUtc in new[] { false, true })
        {
            var required = CreateLargeValues(unit, isAdjustedToUtc);
            var expected = required.Select((value, index) =>
                index % 7 == 0 ? null : (DateTime?)value).ToArray();
            var actual = RoundTripOptional(expected, unit, isAdjustedToUtc, encoding, pageVersion);
            AssertEqual(expected, actual, unit, isAdjustedToUtc, encoding, pageVersion);
        }
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void AllNullOptionalDeltaBinaryPackedDateTimesRoundTrip(ParquetDataPageVersion pageVersion)
    {
        var expected = new DateTime?[257];
        var actual = RoundTripOptional(expected, TimeUnit.Micros, isAdjustedToUtc: true,
            EncodingKind.DeltaBinaryPacked, pageVersion);

        AssertEqual(expected, actual, TimeUnit.Micros, isAdjustedToUtc: true,
            EncodingKind.DeltaBinaryPacked, pageVersion);
    }

    [Test]
    public void RequiredDictionaryDateTimesPreserveUnitsKindsAndElevenBitIndexes()
    {
        foreach (var pageVersion in new[] { ParquetDataPageVersion.V1, ParquetDataPageVersion.V2 })
        foreach (var unit in Enum.GetValues<TimeUnit>())
        foreach (var isAdjustedToUtc in new[] { false, true })
        {
            var kind = isAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            var ticksPerValue = unit switch
            {
                TimeUnit.Millis => TimeSpan.TicksPerMillisecond,
                TimeUnit.Micros => 10,
                TimeUnit.Nanos => 1,
                _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
            };
            var expected = Enumerable.Range(0, 4_113)
                .Select(index => new DateTime(
                    checked(DateTime.UnixEpoch.Ticks + (index & 2_047) * ticksPerValue), kind))
                .ToArray();

            var actual = RoundTrip(expected, unit, isAdjustedToUtc,
                EncodingKind.RleDictionary, pageVersion);
            AssertEqual(expected, actual, unit, isAdjustedToUtc,
                EncodingKind.RleDictionary, pageVersion);
        }
    }

    [Test]
    public void OptionalDictionaryDateTimesPreserveUnitsKindsNullsAndElevenBitIndexes()
    {
        foreach (var pageVersion in new[] { ParquetDataPageVersion.V1, ParquetDataPageVersion.V2 })
        foreach (var unit in Enum.GetValues<TimeUnit>())
        foreach (var isAdjustedToUtc in new[] { false, true })
        {
            var kind = isAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            var ticksPerValue = unit switch
            {
                TimeUnit.Millis => TimeSpan.TicksPerMillisecond,
                TimeUnit.Micros => 10,
                TimeUnit.Nanos => 1,
                _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
            };
            var expected = Enumerable.Range(0, 4_113)
                .Select(index => index % 7 == 0
                    ? null
                    : (DateTime?)new DateTime(
                        checked(DateTime.UnixEpoch.Ticks + (index & 2_047) * ticksPerValue), kind))
                .ToArray();

            var actual = RoundTripOptional(expected, unit, isAdjustedToUtc,
                EncodingKind.RleDictionary, pageVersion);
            AssertEqual(expected, actual, unit, isAdjustedToUtc,
                EncodingKind.RleDictionary, pageVersion);
        }
    }

    [Test]
    public void RequiredDictionaryDateTimesRejectOutOfRangeVectorizedIndex()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)),
                new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true))
        ]);
        var column = schema.LeafColumns[0].Column;
        var buffers = default(ColumnReadBuffers<DateTime>);
        try
        {
            const int dictionaryLength = 2_047;
            var dictionaryPayload = new byte[dictionaryLength * sizeof(long)];
            for (var i = 0; i < dictionaryLength; i++)
                BinaryPrimitives.WriteInt64LittleEndian(
                    dictionaryPayload.AsSpan(i * sizeof(long)), i);
            var dictionaryHeader = new PageHeader(PageHeaderType.DictionaryPage,
                (uint)dictionaryPayload.Length, (uint)dictionaryPayload.Length, dictionaryLength,
                EncodingKind.Plain, 1, 0, 0, 0, false, EncodingKind.Rle,
                EncodingKind.Rle, dictionaryLength);
            if (!ColumnChunkReader.TryDecodeDictionaryPageIntoNative(dictionaryHeader,
                    dictionaryPayload, column, ref buffers, DefaultParquetBufferPool.Shared))
                throw new InvalidOperationException("Timestamp dictionary page did not use the native path.");

            var invalidIndexes = EncodeDictionaryLiteral(
                [0, 1, 2, 3, 4, 5, 6, dictionaryLength], bitWidth: 11);
            var dataHeader = new PageHeader(PageHeaderType.DataPage,
                (uint)invalidIndexes.Length, (uint)invalidIndexes.Length, 8,
                EncodingKind.RleDictionary, 1, 0, 0, 0, false, EncodingKind.Rle,
                EncodingKind.Rle, 8);
            Assert.Throws<CorruptParquetException>(() =>
                ColumnChunkReader.TryDecodeRequiredPageIntoNative(dataHeader, invalidIndexes, column,
                    rowCount: 8, ref buffers, DefaultParquetBufferPool.Shared, out _));
        }
        finally
        {
            buffers.Dispose();
        }
    }

    [Test]
    public void RequiredDateTimesRejectOutOfRangeNonPlainValues()
    {
        EncodingKind[] encodings = [EncodingKind.DeltaBinaryPacked, EncodingKind.ByteStreamSplit];
        TimeUnit[] units = [TimeUnit.Millis, TimeUnit.Micros];
        foreach (var encoding in encodings)
        foreach (var unit in units)
        {
            var schema = new ParquetSchema([
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int64,
                    logicalType: new LogicalType.Timestamp(unit, IsAdjustedToUtc: true))
            ]);
            var column = schema.LeafColumns[0].Column;
            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
            var buffers = default(ColumnReadBuffers<DateTime>);
            try
            {
                long[] raw = [long.MaxValue];
                if (encoding == EncodingKind.DeltaBinaryPacked)
                    DeltaBinaryPackedEncoding.WriteInt64(raw, ref writer);
                else
                    ByteStreamSplitEncoding.WriteValues(column, raw, ref writer);

                var payload = new byte[writer.WrittenLength];
                writer.CopyTo(payload);
                var header = new PageHeader(PageHeaderType.DataPage,
                    UncompressedPageSize: checked((uint)payload.Length),
                    CompressedPageSize: checked((uint)payload.Length), ValueCount: 1, Encoding: encoding,
                    HeaderLength: 1, RepetitionLevelsByteLength: 0,
                    DefinitionLevelsByteLength: 0, NullCount: 0, IsCompressed: false,
                    RepetitionLevelEncoding: EncodingKind.Rle,
                    DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 1);

                // An out-of-range raw timestamp is corrupt file data; it used to
                // surface as the OverflowException from the tick scaling.
                Assert.Throws<CorruptParquetException>(() =>
                    ColumnChunkReader.TryDecodeRequiredPageIntoNative(
                        header, payload, column, rowCount: 1, ref buffers,
                        DefaultParquetBufferPool.Shared, out _));
            }
            finally
            {
                buffers.Dispose();
                writer.Dispose();
            }
        }
    }

    [Test]
    public void OptionalDeltaBinaryPackedDateTimesRejectOutOfRangeValues()
    {
        foreach (var pageVersion in new[] { ParquetDataPageVersion.V1, ParquetDataPageVersion.V2 })
        {
            var schema = new ParquetSchema([
                ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int64,
                    new ColumnOptions(ParquetRepetition.Optional,
                        ImmutableArray.Create(EncodingKind.DeltaBinaryPacked)),
                    new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true))
            ]);
            var column = schema.LeafColumns[0].Column;
            var writer = new BufferWriter(DefaultParquetBufferPool.Shared, 1024, 1024);
            var buffers = default(ColumnReadBuffers<DateTime?>);
            try
            {
                DeltaBinaryPackedEncoding.WriteInt64([long.MaxValue], ref writer);
                var encodedValues = new byte[writer.WrittenLength];
                writer.CopyTo(encodedValues);

                byte[] payload;
                uint definitionByteLength;
                if (pageVersion == ParquetDataPageVersion.V1)
                {
                    payload = new byte[sizeof(int) + 2 + encodedValues.Length];
                    BinaryPrimitives.WriteInt32LittleEndian(payload, 2);
                    payload[sizeof(int)] = 2; // One-value RLE run.
                    payload[sizeof(int) + 1] = 1; // Present.
                    encodedValues.CopyTo(payload, sizeof(int) + 2);
                    definitionByteLength = 0;
                }
                else
                {
                    payload = new byte[2 + encodedValues.Length];
                    payload[0] = 2; // One-value RLE run.
                    payload[1] = 1; // Present.
                    encodedValues.CopyTo(payload, 2);
                    definitionByteLength = 2;
                }

                var header = new PageHeader(
                    pageVersion == ParquetDataPageVersion.V1 ? PageHeaderType.DataPage : PageHeaderType.DataPageV2,
                    UncompressedPageSize: checked((uint)payload.Length),
                    CompressedPageSize: checked((uint)payload.Length), ValueCount: 1,
                    Encoding: EncodingKind.DeltaBinaryPacked, HeaderLength: 1,
                    RepetitionLevelsByteLength: 0, DefinitionLevelsByteLength: definitionByteLength,
                    NullCount: 0, IsCompressed: false, RepetitionLevelEncoding: EncodingKind.Rle,
                    DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 1);

                Assert.Throws<CorruptParquetException>(() =>
                    ColumnChunkReader.TryDecodeNullablePageIntoNative(header, payload, column,
                        rowCount: 1, ref buffers, DefaultParquetBufferPool.Shared, out _));
            }
            finally
            {
                buffers.Dispose();
                writer.Dispose();
            }
        }
    }

    static DateTime[] RoundTrip(DateTime[] values, TimeUnit unit, bool isAdjustedToUtc,
        EncodingKind encoding, ParquetDataPageVersion pageVersion)
    {
        var options = new ColumnOptions(encodings: ImmutableArray.Create(encoding));
        var pageStrategy = encoding == EncodingKind.RleDictionary
            ? ForceDictionaryPageStrategy.Shared
            : null;
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int64, options,
                new LogicalType.Timestamp(unit, isAdjustedToUtc), pageStrategy)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion
        });
        var serialized = writer.CreateSerializedColumn<DateTime>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<DateTime>();
        foreach (var buffer in reader.RowGroups[0].Column<DateTime>(0))
            actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static DateTime?[] RoundTripOptional(DateTime?[] values, TimeUnit unit, bool isAdjustedToUtc,
        EncodingKind encoding, ParquetDataPageVersion pageVersion)
    {
        var options = new ColumnOptions(ParquetRepetition.Optional,
            ImmutableArray.Create(encoding));
        var pageStrategy = encoding == EncodingKind.RleDictionary
            ? ForceDictionaryPageStrategy.Shared
            : null;
        var schema = new ParquetSchema([
            ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int64, options,
                new LogicalType.Timestamp(unit, isAdjustedToUtc), pageStrategy)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion
        });
        var serialized = writer.CreateSerializedColumn<DateTime?>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<DateTime?>(values.Length);
        foreach (var buffer in reader.RowGroups[0].Column<DateTime?>(0))
            actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static DateTime[] CreateValues(TimeUnit unit, bool isAdjustedToUtc)
    {
        var kind = isAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        var epoch = DateTime.SpecifyKind(DateTime.UnixEpoch, kind);
        return unit switch
        {
            TimeUnit.Millis =>
            [
                DateTime.SpecifyKind(DateTime.MinValue, kind),
                epoch.AddMilliseconds(-1),
                epoch,
                epoch.AddMilliseconds(1),
                new DateTime(DateTime.MaxValue.Ticks - TimeSpan.TicksPerMillisecond + 1, kind)
            ],
            TimeUnit.Micros =>
            [
                DateTime.SpecifyKind(DateTime.MinValue, kind),
                epoch.AddTicks(-10),
                epoch,
                epoch.AddTicks(10),
                new DateTime(DateTime.MaxValue.Ticks - 9, kind)
            ],
            TimeUnit.Nanos =>
            [
                epoch.AddTicks(long.MinValue / 100),
                epoch.AddTicks(-1),
                epoch,
                epoch.AddTicks(1),
                epoch.AddTicks(long.MaxValue / 100)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit,
                "Time unit must be a defined TimeUnit value.")
        };
    }

    static byte[] EncodeDictionaryLiteral(ReadOnlySpan<int> indexes, int bitWidth)
    {
        var groupCount = (indexes.Length + 7) / 8;
        var padded = new int[groupCount * 8];
        indexes.CopyTo(padded);
        var payload = new byte[2 + (padded.Length * bitWidth + 7) / 8];
        payload[0] = checked((byte)bitWidth);
        payload[1] = checked((byte)(groupCount * 2 + 1));
        ulong bitBuffer = 0;
        var bufferedBits = 0;
        var byteIndex = 2;
        foreach (var index in padded)
        {
            bitBuffer |= (ulong)(uint)index << bufferedBits;
            bufferedBits += bitWidth;
            while (bufferedBits >= 8)
            {
                payload[byteIndex++] = (byte)bitBuffer;
                bitBuffer >>= 8;
                bufferedBits -= 8;
            }
        }
        if (bufferedBits != 0)
            payload[byteIndex] = (byte)bitBuffer;
        return payload;
    }

    static DateTime[] CreateLargeValues(TimeUnit unit, bool isAdjustedToUtc)
    {
        var kind = isAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        var ticksPerValue = unit switch
        {
            TimeUnit.Millis => TimeSpan.TicksPerMillisecond,
            TimeUnit.Micros => 10,
            TimeUnit.Nanos => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
        };
        var values = new DateTime[40_003];
        for (var i = 0; i < values.Length; i++)
            values[i] = new DateTime(
                checked(DateTime.UnixEpoch.Ticks + (i - values.Length / 2L) * ticksPerValue), kind);
        return values;
    }

    static void AssertEqual(ReadOnlySpan<DateTime> expected, ReadOnlySpan<DateTime> actual,
        TimeUnit unit, bool isAdjustedToUtc, EncodingKind encoding, ParquetDataPageVersion pageVersion)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"{pageVersion}/{encoding}/{unit}/adjusted={isAdjustedToUtc}: " +
                $"expected {expected.Length} values, got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
            if (actual[i].Ticks != expected[i].Ticks || actual[i].Kind != expected[i].Kind)
                throw new InvalidOperationException(
                    $"{pageVersion}/{encoding}/{unit}/adjusted={isAdjustedToUtc}: value {i} expected " +
                    $"ticks={expected[i].Ticks}, kind={expected[i].Kind}; got " +
                    $"ticks={actual[i].Ticks}, kind={actual[i].Kind}.");
    }

    static void AssertEqual(ReadOnlySpan<DateTime?> expected, ReadOnlySpan<DateTime?> actual,
        TimeUnit unit, bool isAdjustedToUtc, EncodingKind encoding, ParquetDataPageVersion pageVersion)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"{pageVersion}/{encoding}/{unit}/adjusted={isAdjustedToUtc}: " +
                $"expected {expected.Length} values, got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i].HasValue != expected[i].HasValue)
                throw new InvalidOperationException(
                    $"{pageVersion}/{encoding}/{unit}/adjusted={isAdjustedToUtc}: value {i} nullability differs.");
            if (actual[i] is { } actualValue && expected[i] is { } expectedValue &&
                (actualValue.Ticks != expectedValue.Ticks || actualValue.Kind != expectedValue.Kind))
                throw new InvalidOperationException(
                    $"{pageVersion}/{encoding}/{unit}/adjusted={isAdjustedToUtc}: value {i} expected " +
                    $"ticks={expectedValue.Ticks}, kind={expectedValue.Kind}; got " +
                    $"ticks={actualValue.Ticks}, kind={actualValue.Kind}.");
        }
    }
}
