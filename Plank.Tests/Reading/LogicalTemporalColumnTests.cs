using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class LogicalTemporalColumnTests
{
    static readonly EncodingKind[] TemporalEncodings =
    [
        EncodingKind.Plain,
        EncodingKind.DeltaBinaryPacked,
        EncodingKind.ByteStreamSplit,
        EncodingKind.RleDictionary
    ];

    [Test]
    public void DateOnlyValuesRoundTripRelativeToUnixEpoch()
    {
        DateOnly[] expected =
        [
            new(1969, 12, 31),
            new(1970, 1, 1),
            new(2000, 2, 29),
            new(2026, 7, 27)
        ];
        foreach (var encoding in TemporalEncodings)
        {
            var schema = CreateSchema(ParquetPhysicalType.Int32, new LogicalType.Date(), encoding);
            var actual = RoundTrip(schema, expected);
            AssertSequenceEqual(expected, actual, encoding);
        }
    }

    [Test]
    public void TimeOnlyValuesRoundTrip()
    {
        TimeOnly[] expected =
        [
            TimeOnly.MinValue,
            new(1, 2, 3, 4),
            new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(7890)),
            new(TimeOnly.MaxValue.Ticks - 9)
        ];
        foreach (var encoding in TemporalEncodings)
        {
            var schema = CreateSchema(ParquetPhysicalType.Int64,
                new LogicalType.Time(TimeUnit.Micros, IsAdjustedToUtc: false), encoding);
            var actual = RoundTrip(schema, expected);
            AssertSequenceEqual(expected, actual, encoding);
        }
    }

    [Test]
    public void NanosecondTimestampValuesRoundTrip()
    {
        DateTime[] expected =
        [
            DateTime.UnixEpoch.AddTicks(-12_345),
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddTicks(98_765_432)
        ];
        foreach (var encoding in TemporalEncodings)
        {
            var schema = CreateSchema(ParquetPhysicalType.Int64,
                new LogicalType.Timestamp(TimeUnit.Nanos, IsAdjustedToUtc: true), encoding);
            var actual = RoundTrip(schema, expected);
            AssertSequenceEqual(expected, actual, encoding);
        }
    }

    [Test]
    public void NanosecondTimestampOffsetValuesRoundTrip()
    {
        DateTimeOffset[] expected =
        [
            DateTimeOffset.UnixEpoch.AddTicks(-12_345),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddTicks(98_765_432)
        ];
        foreach (var encoding in TemporalEncodings)
        {
            var schema = CreateSchema(ParquetPhysicalType.Int64,
                new LogicalType.Timestamp(TimeUnit.Nanos, IsAdjustedToUtc: true), encoding);
            var actual = RoundTrip(schema, expected);
            AssertSequenceEqual(expected, actual, encoding);
        }
    }

    [Test]
    public void NullableTemporalValuesRoundTrip()
    {
        DateOnly?[] expectedDates = [new DateOnly(1969, 12, 31), null, new DateOnly(2026, 7, 27)];
        TimeOnly?[] expectedTimes = [TimeOnly.MinValue, null, new TimeOnly(12, 34, 56)];
        DateTime?[] expectedTimestamps =
            [DateTime.UnixEpoch.AddTicks(-12_345), null, DateTime.UnixEpoch.AddTicks(98_765_432)];

        foreach (var encoding in TemporalEncodings)
        {
            var dateSchema = CreateSchema(ParquetPhysicalType.Int32, new LogicalType.Date(), encoding,
                optional: true);
            AssertSequenceEqual(expectedDates, RoundTrip(dateSchema, expectedDates), encoding);

            var timeSchema = CreateSchema(ParquetPhysicalType.Int64,
                new LogicalType.Time(TimeUnit.Millis, IsAdjustedToUtc: false), encoding, optional: true);
            AssertSequenceEqual(expectedTimes, RoundTrip(timeSchema, expectedTimes), encoding);

            var timestampSchema = CreateSchema(ParquetPhysicalType.Int64,
                new LogicalType.Timestamp(TimeUnit.Nanos, IsAdjustedToUtc: true), encoding, optional: true);
            AssertSequenceEqual(expectedTimestamps, RoundTrip(timestampSchema, expectedTimestamps), encoding);
        }
    }

    [Test]
    public void NullablePlainTimestampsPreserveUnitsKindsAndNullsAcrossPageVersions()
    {
        TimeUnit[] units = [TimeUnit.Millis, TimeUnit.Micros, TimeUnit.Nanos];
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        foreach (var pageVersion in pageVersions)
        foreach (var unit in units)
        foreach (var adjustedToUtc in new[] { false, true })
        {
            var kind = adjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            var ticksPerValue = unit switch
            {
                TimeUnit.Millis => TimeSpan.TicksPerMillisecond,
                TimeUnit.Micros => 10,
                TimeUnit.Nanos => 1,
                _ => throw new ArgumentOutOfRangeException(nameof(unit))
            };
            var expected = new DateTime?[257];
            for (var i = 0; i < expected.Length; i++)
            {
                if (i < 17 || i > 239 || i % 19 == 0)
                    continue;
                var ticks = checked(DateTime.UnixEpoch.Ticks + (i - 128L) * ticksPerValue);
                expected[i] = new DateTime(ticks, kind);
            }

            var schema = CreateSchema(ParquetPhysicalType.Int64,
                new LogicalType.Timestamp(unit, adjustedToUtc), EncodingKind.Plain, optional: true);
            var actual = RoundTrip(schema, expected, pageVersion);
            AssertSequenceEqual(expected, actual, EncodingKind.Plain);
            if (actual.Any(value => value.HasValue && value.Value.Kind != kind))
                throw new InvalidOperationException(
                    $"{pageVersion}/{unit}: expected DateTime kind {kind}.");
        }
    }

    static ParquetSchema CreateSchema(ParquetPhysicalType physicalType, LogicalType logicalType,
        EncodingKind encoding, bool optional = false)
    {
        var options = new ColumnOptions(encodings: ImmutableArray.Create(encoding));
        var pageStrategy = encoding == EncodingKind.RleDictionary
            ? ForceDictionaryPageStrategy.Shared
            : null;
        var column = optional
            ? ColumnDefinition.OptionalLeaf("value", physicalType, options, logicalType, pageStrategy)
            : ColumnDefinition.RequiredLeaf("value", physicalType, options, logicalType, pageStrategy);
        return new([column]);
    }

    static T[] RoundTrip<T>(ParquetSchema schema, T[] values,
        ParquetDataPageVersion pageVersion = ParquetDataPageVersion.V1)
    {
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = pageVersion
        });
        var serialized = writer.CreateSerializedColumn<T>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        writer.StartRowGroup().Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        var actual = new List<T>();
        foreach (var buffer in reader.RowGroups[0].Column<T>(0))
            actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static void AssertSequenceEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual,
        EncodingKind encoding)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"{encoding}: expected [{string.Join(", ", expected.ToArray())}], got [{string.Join(", ", actual.ToArray())}].");
    }
}
