using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
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
}
