using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
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
    public void TemporalProjectionsRequireMatchingAnnotations()
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Number", ParquetPhysicalType.Int32),
            ColumnDefinition.RequiredLeaf("Instant", ParquetPhysicalType.Int64,
                logicalType: new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true)),
            ColumnDefinition.RequiredLeaf("Clock", ParquetPhysicalType.Int64,
                logicalType: new LogicalType.Time(TimeUnit.Micros, IsAdjustedToUtc: false)),
            ColumnDefinition.RequiredLeaf("Count", ParquetPhysicalType.Int64),
            ColumnDefinition.RequiredLeaf("Legacy", ParquetPhysicalType.Int96)
        ]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream))
        {
            var group = writer.StartRowGroup();
            var number = group.CreateSerializedColumn<int>(schema.LeafColumns[0]);
            var instant = group.CreateSerializedColumn<long>(schema.LeafColumns[1]);
            var clock = group.CreateSerializedColumn<long>(schema.LeafColumns[2]);
            var count = group.CreateSerializedColumn<long>(schema.LeafColumns[3]);
            var legacy = group.CreateSerializedColumn<byte[]>(schema.LeafColumns[4]);
            number.Serialize([42]);
            instant.Serialize([7L]);
            clock.Serialize([0L]);
            count.Serialize([9L]);
            legacy.Serialize([new byte[12]]);
            group.Write(number);
            group.Write(instant);
            group.Write(clock);
            group.Write(count);
            group.Write(legacy);
            writer.CloseFile();
        }
        var bytes = stream.ToArray();

        using (var reader = schema.CreateReader(new MemoryReadSource(bytes)))
        {
            var rowGroup = reader.RowGroups[0];
            AssertRejected(() => _ = rowGroup.Column<DateOnly>(0), "logical type 'Date'");
            AssertRejected(() => _ = rowGroup.Column<TimeOnly?>(1), "logical type 'Time'");
            AssertRejected(() => _ = rowGroup.Column<DateTime>(2), "logical type 'Timestamp'");
            AssertRejected(() => _ = rowGroup.Column<DateTimeOffset?>(2), "logical type 'Timestamp'");
            AssertRejected(() => _ = rowGroup.Column<DateTime>(3), "logical type 'Timestamp'");
            AssertRejected(() => _ = rowGroup.Column<DateOnly>(3), "physical type Int64");
            AssertRejected(() => _ = rowGroup.Column<DateTime>(4), "physical type Int96");
            AssertSingle(rowGroup.Column<int>(0), 42);
            AssertSingle(rowGroup.Column<long>(1), 7L);
            AssertSingle(rowGroup.Column<long>(3), 9L);
        }

        var converted = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("Number", ParquetPhysicalType.Int32, converter: new DayConverter())
        ]);
        using (var reader = converted.CreateReader(new MemoryReadSource(bytes)))
        {
            var rowGroup = reader.RowGroups[0];
            AssertRejected(() => _ = rowGroup.Column<Day>(0), "logical type 'Date'");
        }
    }

    readonly record struct Day(DateOnly Value);

    sealed class DayConverter : ParquetValueConverter<Day, DateOnly>
    {
        public override DateOnly ConvertToPhysical(Day value) => value.Value;
        public override Day ConvertFromPhysical(DateOnly value) => new(value);
    }

    static void AssertRejected(Action open, string expectedMessage)
    {
        var exception = Assert.Throws<InvalidOperationException>(open);
        if (!exception.Message.Contains(expectedMessage, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Expected a message containing \"{expectedMessage}\", got \"{exception.Message}\".");
    }

    static void AssertSingle<T>(RowGroupColumn<T> column, T expected)
    {
        var actual = new List<T>();
        foreach (var buffer in column)
            actual.AddRange(buffer.Values);
        if (actual.Count != 1 || !EqualityComparer<T>.Default.Equals(actual[0], expected))
            throw new InvalidOperationException(
                $"Expected [{expected}], got [{string.Join(", ", actual)}].");
    }

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
    public void RequiredPlainDateTimeValuesPreserveUnitsKindsAndEpochBoundaries()
    {
        ParquetDataPageVersion[] pageVersions =
            [ParquetDataPageVersion.V1, ParquetDataPageVersion.V2];
        foreach (var pageVersion in pageVersions)
            foreach (var unit in Enum.GetValues<TimeUnit>())
                foreach (var isAdjustedToUtc in new[] { false, true })
                {
                    var expected = CreateRequiredTimestampValues(unit, isAdjustedToUtc);
                    var schema = CreateSchema(ParquetPhysicalType.Int64,
                        new LogicalType.Timestamp(unit, isAdjustedToUtc), EncodingKind.Plain);
                    var actual = RoundTrip(schema, expected, pageVersion);
                    AssertDateTimesEqual(expected, actual, unit, isAdjustedToUtc, pageVersion);
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

            var timeSchema = CreateSchema(ParquetPhysicalType.Int32,
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

    static DateTime[] CreateRequiredTimestampValues(TimeUnit unit, bool isAdjustedToUtc)
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

    static void AssertDateTimesEqual(ReadOnlySpan<DateTime> expected, ReadOnlySpan<DateTime> actual,
        TimeUnit unit, bool isAdjustedToUtc, ParquetDataPageVersion pageVersion)
    {
        if (actual.Length != expected.Length)
            throw new InvalidOperationException(
                $"{pageVersion}/{unit}/adjusted={isAdjustedToUtc}: expected {expected.Length} values, got {actual.Length}.");
        for (var i = 0; i < expected.Length; i++)
            if (actual[i].Ticks != expected[i].Ticks || actual[i].Kind != expected[i].Kind)
                throw new InvalidOperationException(
                    $"{pageVersion}/{unit}/adjusted={isAdjustedToUtc}: value {i} expected " +
                    $"ticks={expected[i].Ticks}, kind={expected[i].Kind}; got " +
                    $"ticks={actual[i].Ticks}, kind={actual[i].Kind}.");
    }

    static void AssertSequenceEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual,
        EncodingKind encoding)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"{encoding}: expected [{string.Join(", ", expected.ToArray())}], got [{string.Join(", ", actual.ToArray())}].");
    }
}
