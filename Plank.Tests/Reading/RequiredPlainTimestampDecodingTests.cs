using System.Buffers.Binary;
using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Writing.Thrift;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class RequiredPlainTimestampDecodingTests
{
    [Test]
    public void RawValuesPreserveUnitsKindsAndBoundariesAcrossPageVersions()
    {
        PageHeaderType[] pageTypes = [PageHeaderType.DataPage, PageHeaderType.DataPageV2];
        foreach (var pageType in pageTypes)
        foreach (var unit in Enum.GetValues<TimeUnit>())
        foreach (var adjustedToUtc in new[] { false, true })
        {
            long[] rawValues = unit switch
            {
                TimeUnit.Millis => [-62_135_596_800_000L, -1, 0, 1, 253_402_300_799_999L],
                TimeUnit.Micros => [-62_135_596_800_000_000L, -1, 0, 1, 253_402_300_799_999_999L],
                TimeUnit.Nanos => [long.MinValue, -100L, -1, 0, 1, 100, long.MaxValue],
                _ => throw new ArgumentOutOfRangeException(nameof(unit), unit,
                    "Time unit must be a defined TimeUnit value.")
            };
            var actual = Decode(rawValues, unit, adjustedToUtc, pageType);
            var kind = adjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;

            for (var i = 0; i < rawValues.Length; i++)
            {
                var scaledTicks = unit switch
                {
                    TimeUnit.Millis => checked(rawValues[i] * TimeSpan.TicksPerMillisecond),
                    TimeUnit.Micros => checked(rawValues[i] * 10),
                    TimeUnit.Nanos => rawValues[i] / 100,
                    _ => throw new ArgumentOutOfRangeException(nameof(unit), unit,
                        "Time unit must be a defined TimeUnit value.")
                };
                var expected = new DateTime(checked(DateTime.UnixEpoch.Ticks + scaledTicks), kind);
                if (actual[i].Ticks != expected.Ticks || actual[i].Kind != expected.Kind)
                    throw new InvalidOperationException(
                        $"{pageType}/{unit}/adjusted={adjustedToUtc}: raw value {rawValues[i]} " +
                        $"decoded as ticks={actual[i].Ticks}, kind={actual[i].Kind}; expected " +
                        $"ticks={expected.Ticks}, kind={expected.Kind}.");
            }
        }
    }

    [Test]
    public void OutOfRangeRawValuesAreRejectedAcrossPageVersionsAndKinds()
    {
        PageHeaderType[] pageTypes = [PageHeaderType.DataPage, PageHeaderType.DataPageV2];
        foreach (var pageType in pageTypes)
        foreach (var adjustedToUtc in new[] { false, true })
        {
            AssertRejected([-62_135_596_800_001], TimeUnit.Millis, adjustedToUtc, pageType);
            AssertRejected([253_402_300_800_000], TimeUnit.Millis, adjustedToUtc, pageType);
            AssertRejected([-62_135_596_800_000_001], TimeUnit.Micros, adjustedToUtc, pageType);
            AssertRejected([253_402_300_800_000_000], TimeUnit.Micros, adjustedToUtc, pageType);

            // Values so large that scaling to ticks wraps, rather than merely landing outside the
            // representable range. These take the overflow check itself rather than the range check
            // that follows it, on both sides of zero and at the exact first value that overflows.
            foreach (var unit in (ReadOnlySpan<TimeUnit>)[TimeUnit.Millis, TimeUnit.Micros])
            {
                var multiplier = unit == TimeUnit.Millis ? TimeSpan.TicksPerMillisecond : 10;
                AssertRejected([long.MaxValue], unit, adjustedToUtc, pageType);
                AssertRejected([long.MinValue], unit, adjustedToUtc, pageType);
                AssertRejected([(long.MaxValue / multiplier) + 1], unit, adjustedToUtc, pageType);
                AssertRejected([(long.MinValue / multiplier) - 1], unit, adjustedToUtc, pageType);
            }
        }
    }

    [Test]
    public void PayloadShorterThanDeclaredValueCountIsRejectedAcrossPageVersions()
    {
        foreach (var pageType in new[] { PageHeaderType.DataPage, PageHeaderType.DataPageV2 })
            Assert.Throws<CorruptParquetException>(() => ReadFile(
                CreateFile([0], TimeUnit.Micros, true, pageType, 2)));
    }

    [Test]
    public void MicrosecondVectorsPreserveBoundaryValuesAndTails()
    {
        long[] boundaries = [-62_135_596_800_000_000L, -1, 0, 1, 253_402_300_799_999_999L];
        foreach (var pageType in new[] { PageHeaderType.DataPage, PageHeaderType.DataPageV2 })
        foreach (var utc in new[] { false, true })
        for (var length = 1; length <= 33; length++)
        {
            var raw = Enumerable.Range(0, length).Select(i => boundaries[i % boundaries.Length]).ToArray();
            var actual = Decode(raw, TimeUnit.Micros, utc, pageType);
            for (var i = 0; i < length; i++)
            {
                var expected = new DateTime(raw[i] * 10 + DateTime.UnixEpoch.Ticks,
                    utc ? DateTimeKind.Utc : DateTimeKind.Unspecified);
                if (actual[i].Ticks != expected.Ticks || actual[i].Kind != expected.Kind)
                    throw new InvalidOperationException($"Microsecond vector mismatch at {i} of {length}.");
            }
        }
    }

    [Test]
    public void MicrosecondVectorsRejectInvalidValuesInEveryLaneAndTail()
    {
        long[] invalidValues = [-62_135_596_800_000_001L, 253_402_300_800_000_000L,
            long.MinValue, long.MaxValue];
        foreach (var pageType in new[] { PageHeaderType.DataPage, PageHeaderType.DataPageV2 })
        foreach (var utc in new[] { false, true })
        foreach (var invalid in invalidValues)
        for (var index = 0; index < 17; index++)
        {
            var raw = new long[17];
            raw[index] = invalid;
            AssertRejected(raw, TimeUnit.Micros, utc, pageType);
        }
    }

    static DateTime[] Decode(long[] rawValues, TimeUnit unit, bool adjustedToUtc, PageHeaderType pageType)
        => ReadFile(CreateFile(rawValues, unit, adjustedToUtc, pageType, rawValues.Length));

    static DateTime[] ReadFile(byte[] file)
    {
        using var reader = new ParquetReader();
        reader.Reset(new MemoryStream(file));
        var result = new List<DateTime>();
        foreach (var buffer in reader.RowGroups[0].Column<DateTime>(0))
            result.AddRange(buffer.Values);
        return result.ToArray();
    }

    static byte[] CreateFile(long[] rawValues, TimeUnit unit, bool adjustedToUtc,
        PageHeaderType pageType, int declaredCount)
    {
        var schema = CreateSchema(unit, adjustedToUtc);
        using var output = new MemoryStream();
        using (var writer = schema.CreateWriter(output, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageType == PageHeaderType.DataPage ? ParquetDataPageVersion.V1 : ParquetDataPageVersion.V2
        }))
        {
            var column = writer.CreateSerializedColumn<DateTime>(schema.LeafColumns[0]);
            var epoch = DateTime.SpecifyKind(DateTime.UnixEpoch,
                adjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified);
            column.Serialize(Enumerable.Repeat(epoch, declaredCount).ToArray());
            ref var page = ref column.Pages[0];
            page.Content.Reset();
            foreach (var raw in rawValues)
            {
                BinaryPrimitives.WriteInt64LittleEndian(page.Content.GetSpan(sizeof(long)), raw);
                page.Content.Advance(sizeof(long));
            }
            var length = page.Content.WrittenLength;
            page.UncompressedContentSize = length;
            page.Header.Reset();
            if (pageType == PageHeaderType.DataPage)
                ParquetMetadataThriftWriter.WriteDataPageHeaderV1(ref page.Header, (uint)declaredCount,
                    EncodingKind.Plain, length, length, default, null);
            else
                ParquetMetadataThriftWriter.WriteDataPageHeaderV2(ref page.Header, (uint)declaredCount,
                    (uint)declaredCount, 0, 0, 0, EncodingKind.Plain, length, length, false, default, null);
            writer.StartRowGroup().Write(column);
            writer.CloseFile();
        }
        return output.ToArray();
    }

    static ParquetSchema CreateSchema(TimeUnit unit, bool adjustedToUtc)
        => new([
            ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.Plain)),
                new LogicalType.Timestamp(unit, adjustedToUtc))
        ]);

    static void AssertRejected(long[] rawValues, TimeUnit unit, bool adjustedToUtc, PageHeaderType pageType)
    {
        try
        {
            Decode(rawValues, unit, adjustedToUtc, pageType);
        }
        // The reader reports an out-of-range raw timestamp as corrupt data; it
        // used to let ArgumentOutOfRangeException or OverflowException escape
        // from the DateTime constructor and the tick scaling.
        catch (CorruptParquetException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{pageType}/{unit}/adjusted={adjustedToUtc}: out-of-range raw timestamp was accepted.");
    }
}
