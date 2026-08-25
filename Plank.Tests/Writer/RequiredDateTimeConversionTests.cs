using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Writer;

internal sealed class RequiredDateTimeConversionTests
{
    static readonly EncodingKind[] Encodings =
    [
        EncodingKind.Plain,
        EncodingKind.RleDictionary,
        EncodingKind.DeltaBinaryPacked,
        EncodingKind.ByteStreamSplit
    ];

    [Test]
    public void RequiredAndNullableDateTimesPreserveUnitsKindsBoundariesAndPages()
    {
        foreach (var pageVersion in Enum.GetValues<ParquetDataPageVersion>())
        foreach (var encoding in Encodings)
        foreach (var unit in Enum.GetValues<TimeUnit>())
        foreach (var adjustedToUtc in new[] { false, true })
        {
            var required = CreateValues(unit, adjustedToUtc);
            var nullable = CreateNullableValues(required);
            var schema = CreateSchema(unit, adjustedToUtc, encoding);

            using var stream = new MemoryStream();
            var writer = schema.CreateWriter(stream, new ParquetWriterOptions
            {
                Compression = CompressionKind.None,
                DataPageVersion = pageVersion,
                WritePageIndexes = true
            });
            var requiredColumn = writer.CreateSerializedColumn<DateTime>(schema.LeafColumns[0]);
            var nullableColumn = writer.CreateSerializedColumn<DateTime?>(schema.LeafColumns[1]);
            requiredColumn.Serialize(required);
            nullableColumn.Serialize(nullable);
            var rowGroup = writer.StartRowGroup();
            rowGroup.Write(requiredColumn);
            rowGroup.Write(nullableColumn);
            writer.CloseFile();

            using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
            AssertSequence(required, Read<DateTime>(reader, 0), pageVersion, encoding, unit, adjustedToUtc,
                "required");
            AssertSequence(nullable, Read<DateTime?>(reader, 1), pageVersion, encoding, unit, adjustedToUtc,
                "nullable");
        }
    }

    [Test]
    public void NanosecondDateTimesRejectValuesOutsidePhysicalRange()
    {
        foreach (var adjustedToUtc in new[] { false, true })
        {
            var kind = adjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
            var epochTicks = DateTime.UnixEpoch.Ticks;
            AssertOverflow(new DateTime(checked(epochTicks + long.MaxValue / 100 + 1), kind), adjustedToUtc);
            AssertOverflow(new DateTime(checked(epochTicks + long.MinValue / 100 - 1), kind), adjustedToUtc);
        }
    }

    [Test]
    public void DateTimesRejectKindsThatDoNotMatchTimestampAdjustment()
    {
        AssertKindRejected(new DateTime(DateTime.UnixEpoch.Ticks, DateTimeKind.Unspecified), adjustedToUtc: true);
        AssertKindRejected(new DateTime(DateTime.UnixEpoch.Ticks, DateTimeKind.Utc), adjustedToUtc: false);
        AssertKindRejected(new DateTime(DateTime.UnixEpoch.Ticks, DateTimeKind.Local), adjustedToUtc: true);
        AssertKindRejected(new DateTime(DateTime.UnixEpoch.Ticks, DateTimeKind.Local), adjustedToUtc: false);
    }

    [Test]
    public void RequiredForcedDictionaryAvoidsFullColumnTimestampScratchAndRoundTrips()
    {
        const int rowCount = 5_003;
        var epoch = DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Unspecified);
        var unique = new[]
        {
            epoch.AddMilliseconds(-2),
            epoch,
            epoch.AddMilliseconds(1),
            epoch.AddMilliseconds(7),
            epoch.AddMilliseconds(31)
        };
        var values = new DateTime[rowCount];
        for (var i = 0; i < values.Length; i++)
            values[i] = unique[(i * 3) % unique.Length];

        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("timestamp", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: [EncodingKind.RleDictionary]),
                new LogicalType.Timestamp(TimeUnit.Micros, false), ForceDictionaryPageStrategy.Shared)
        ]);
        var pool = new ScratchGuardBufferPool
        {
            ForbiddenMinimumByteLength = checked((uint)values.Length * sizeof(long))
        };
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            BufferPool = pool,
            BufferChunkSizeBytes = 4_096,
            InitialPageBufferBytes = 4_096,
            InitialColumnBufferBytes = 4_096,
            Compression = CompressionKind.None,
            DataPageVersion = ParquetDataPageVersion.V1,
            WritePageIndexes = false
        });
        var serialized = writer.CreateSerializedColumn<DateTime>(schema.LeafColumns[0]);
        serialized.Serialize(values);
        pool.ForbiddenMinimumByteLength = null;

        var expectedMin = TimestampConversion.FromDateTimeTicks(unique.Min().Ticks, TimeUnit.Micros);
        var expectedMax = TimestampConversion.FromDateTimeTicks(unique.Max().Ticks, TimeUnit.Micros);
        if (serialized.Statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.Int64
            || serialized.Statistics.MinBits != expectedMin || serialized.Statistics.MaxBits != expectedMax)
            throw new InvalidOperationException("Direct timestamp dictionary statistics were not preserved.");

        var rowGroup = writer.StartRowGroup();
        rowGroup.Write(serialized);
        writer.CloseFile();

        using var reader = schema.CreateReader(new MemoryReadSource(stream.ToArray()));
        AssertSequence(values, Read<DateTime>(reader, 0), ParquetDataPageVersion.V1,
            EncodingKind.RleDictionary, TimeUnit.Micros, adjustedToUtc: false, "required-direct");
    }

    [Test]
    public void RequiredForcedDictionaryDirectPathValidatesKindAndNanosecondRange()
    {
        AssertDirectDictionaryRejected<InvalidOperationException>(
            new DateTime(DateTime.UnixEpoch.Ticks, DateTimeKind.Utc), TimeUnit.Micros, adjustedToUtc: false);
        AssertDirectDictionaryRejected<OverflowException>(
            DateTime.UnixEpoch.AddTicks(long.MaxValue / 100 + 1), TimeUnit.Nanos, adjustedToUtc: true);
    }

    static ParquetSchema CreateSchema(TimeUnit unit, bool adjustedToUtc, EncodingKind encoding)
    {
        var options = new ColumnOptions(encodings: ImmutableArray.Create(encoding));
        var pageStrategy = new FixedRowsPageStrategy(33,
            encoding == EncodingKind.RleDictionary ? DictionaryMode.Forced : DictionaryMode.Disabled);
        var logicalType = new LogicalType.Timestamp(unit, adjustedToUtc);
        return new([
            ColumnDefinition.RequiredLeaf("required", ParquetPhysicalType.Int64, options, logicalType,
                pageStrategy),
            ColumnDefinition.OptionalLeaf("nullable", ParquetPhysicalType.Int64, options, logicalType,
                pageStrategy)
        ]);
    }

    static DateTime[] CreateValues(TimeUnit unit, bool adjustedToUtc)
    {
        var kind = adjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified;
        var epoch = DateTime.SpecifyKind(DateTime.UnixEpoch, kind);
        var boundary = unit switch
        {
            TimeUnit.Millis => TimeSpan.TicksPerMillisecond,
            TimeUnit.Micros => 10,
            TimeUnit.Nanos => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
        var values = new DateTime[257];
        for (var i = 0; i < values.Length; i++)
            values[i] = epoch.AddTicks((i - 128L) * boundary);

        values[0] = unit switch
        {
            TimeUnit.Millis => DateTime.SpecifyKind(DateTime.MinValue, kind),
            TimeUnit.Micros => DateTime.SpecifyKind(DateTime.MinValue, kind),
            TimeUnit.Nanos => epoch.AddTicks(long.MinValue / 100),
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
        values[^1] = unit switch
        {
            TimeUnit.Millis => new DateTime(DateTime.MaxValue.Ticks - TimeSpan.TicksPerMillisecond + 1, kind),
            TimeUnit.Micros => new DateTime(DateTime.MaxValue.Ticks - 9, kind),
            TimeUnit.Nanos => epoch.AddTicks(long.MaxValue / 100),
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
        return values;
    }

    static DateTime?[] CreateNullableValues(ReadOnlySpan<DateTime> required)
    {
        var nullable = new DateTime?[required.Length];
        for (var i = 0; i < nullable.Length; i++)
            if (i % 17 != 0 && i % 33 != 0)
                nullable[i] = required[i];
        return nullable;
    }

    static T[] Read<T>(ParquetReader reader, int columnIndex)
    {
        var actual = new List<T>();
        foreach (var buffer in reader.RowGroups[0].Column<T>(columnIndex))
            actual.AddRange(buffer.Values);
        return actual.ToArray();
    }

    static void AssertSequence<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual,
        ParquetDataPageVersion pageVersion, EncodingKind encoding, TimeUnit unit, bool adjustedToUtc,
        string repetition)
    {
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException(
                $"{pageVersion}/{encoding}/{unit}/adjusted={adjustedToUtc}/{repetition} did not round trip.");
    }

    static void AssertOverflow(DateTime value, bool adjustedToUtc)
    {
        var schema = CreateSchema(TimeUnit.Nanos, adjustedToUtc, EncodingKind.Plain);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var serialized = writer.CreateSerializedColumn<DateTime>(schema.LeafColumns[0]);
        Assert.Throws<OverflowException>(() => serialized.Serialize([value]));
    }

    static void AssertKindRejected(DateTime value, bool adjustedToUtc)
    {
        var schema = CreateSchema(TimeUnit.Micros, adjustedToUtc, EncodingKind.Plain);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var serialized = writer.CreateSerializedColumn<DateTime>(schema.LeafColumns[0]);
        Assert.Throws<InvalidOperationException>(() => serialized.Serialize([value]));
    }

    static void AssertDirectDictionaryRejected<TException>(DateTime value, TimeUnit unit, bool adjustedToUtc)
        where TException : Exception
    {
        var schema = new ParquetSchema([
            ColumnDefinition.RequiredLeaf("timestamp", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: [EncodingKind.RleDictionary]),
                new LogicalType.Timestamp(unit, adjustedToUtc), ForceDictionaryPageStrategy.Shared)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions { WritePageIndexes = false });
        var serialized = writer.CreateSerializedColumn<DateTime>(schema.LeafColumns[0]);
        Assert.Throws<TException>(() => serialized.Serialize([value]));
    }

    sealed class ScratchGuardBufferPool : IParquetBufferPool
    {
        internal uint? ForbiddenMinimumByteLength;

        public ParquetBuffer Rent(uint minimumByteLength)
        {
            if (minimumByteLength == ForbiddenMinimumByteLength)
                throw new InvalidOperationException(
                    $"Timestamp serialization rented forbidden full-column scratch ({minimumByteLength} bytes).");
            return DefaultParquetBufferPool.Shared.Rent(minimumByteLength);
        }
    }

    sealed class FixedRowsPageStrategy(uint rowsPerPage, DictionaryMode dictionaryMode) : IPageStrategy
    {
        public DictionaryMode GetDictionaryMode()
            => dictionaryMode;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public uint GetNextDataPageRowCount(uint totalRowCount, uint rowsWritten)
            => Math.Min(rowsPerPage, totalRowCount - rowsWritten);
    }
}
