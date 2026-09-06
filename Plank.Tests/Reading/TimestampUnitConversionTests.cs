using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Reading;

internal sealed class TimestampUnitConversionTests
{
    static readonly EncodingKind[] Encodings =
        [EncodingKind.Plain, EncodingKind.DeltaBinaryPacked, EncodingKind.ByteStreamSplit];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void RawBoundariesAndSubTickValuesSurviveBatches(bool optional)
    {
        foreach (var version in new[] { ParquetDataPageVersion.V1, ParquetDataPageVersion.V2 })
        foreach (var encoding in Encodings)
        foreach (var unit in Enum.GetValues<TimeUnit>())
        foreach (var utc in new[] { false, true })
        {
            long[] boundaries = unit switch
            {
                TimeUnit.Millis => [-62_135_596_800_000, -1, 0, 1, 253_402_300_799_999],
                TimeUnit.Micros => [-62_135_596_800_000_000, -1, 0, 1, 253_402_300_799_999_999],
                _ => [long.MinValue, -101, -100, -99, -1, 0, 1, 99, 100, 101, long.MaxValue]
            };
            var raw = new long?[40_003];
            for (var i = 0; i < raw.Length; i++)
                raw[i] = optional && i % 7 == 0 ? null : boundaries[i % boundaries.Length];
            var (schema, bytes) = WriteRaw(raw, unit, utc, encoding, version, optional);
            using var source = new MemoryReadSource(bytes);
            using var reader = schema.CreateReader(source);
            var index = 0;
            foreach (var buffer in reader.RowGroups[0].Column<DateTime?>(0))
            foreach (var value in buffer.Values)
            {
                var expected = raw[index++];
                if (value.HasValue != expected.HasValue || value.HasValue &&
                    (value.Value.Ticks != ExpectedTicks(expected!.Value, unit) ||
                     value.Value.Kind != (utc ? DateTimeKind.Utc : DateTimeKind.Unspecified)))
                    throw new InvalidOperationException($"{version}/{encoding}/{unit}: DateTime mismatch at {index - 1}.");
            }
            if (index != raw.Length)
                throw new InvalidOperationException("Timestamp count differs.");
            if (!utc)
                continue;
            index = 0;
            foreach (var buffer in reader.RowGroups[0].Column<DateTimeOffset?>(0))
            foreach (var value in buffer.Values)
            {
                var expected = raw[index++];
                if (value.HasValue != expected.HasValue || value.HasValue &&
                    (value.Value.Ticks != ExpectedTicks(expected!.Value, unit) || value.Value.Offset != TimeSpan.Zero))
                    throw new InvalidOperationException($"{version}/{encoding}/{unit}: DateTimeOffset mismatch at {index - 1}.");
            }
            if (index != raw.Length)
                throw new InvalidOperationException("Timestamp count differs.");
        }
    }

    [Test]
    public void InvalidMillisAndMicrosAreCorruptAcrossBatches()
    {
        foreach (var version in new[] { ParquetDataPageVersion.V1, ParquetDataPageVersion.V2 })
        foreach (var encoding in Encodings)
        foreach (var unit in new[] { TimeUnit.Millis, TimeUnit.Micros })
        foreach (var utc in new[] { false, true })
        foreach (var invalid in unit == TimeUnit.Millis
                     ? new long[] { -62_135_596_800_001, 253_402_300_800_000, long.MinValue, long.MaxValue }
                     : new long[] { -62_135_596_800_000_001, 253_402_300_800_000_000, long.MinValue, long.MaxValue })
        foreach (var invalidIndex in new[] { 0, 32_769, 40_002 })
        {
            var raw = new long?[40_003];
            Array.Fill(raw, 0L);
            raw[invalidIndex] = invalid;
            var (schema, bytes) = WriteRaw(raw, unit, utc, encoding, version, optional: false);
            using var source = new MemoryReadSource(bytes);
            using var reader = schema.CreateReader(source);
            Assert.Throws<CorruptParquetException>(() =>
            {
                foreach (var buffer in reader.RowGroups[0].Column<DateTime>(0))
                    _ = buffer.Values.Length;
            });
            if (utc)
                Assert.Throws<CorruptParquetException>(() =>
                {
                    foreach (var buffer in reader.RowGroups[0].Column<DateTimeOffset>(0))
                        _ = buffer.Values.Length;
                });
        }
    }

    static long ExpectedTicks(long raw, TimeUnit unit)
        => checked(DateTime.UnixEpoch.Ticks + (long)(unit switch
        {
            TimeUnit.Millis => (decimal)raw * 10_000,
            TimeUnit.Micros => (decimal)raw * 10,
            _ => decimal.Truncate((decimal)raw / 100)
        }));

    static (ParquetSchema Schema, byte[] Bytes) WriteRaw(long?[] raw, TimeUnit unit,
        bool utc, EncodingKind encoding, ParquetDataPageVersion version, bool optional)
    {
        var options = new ColumnOptions(optional ? ParquetRepetition.Optional : ParquetRepetition.Required,
            ImmutableArray.Create(encoding));
        var timestamp = new LogicalType.Timestamp(unit, utc);
        var schema = new ParquetSchema([
            optional
                ? ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int64, options, timestamp)
                : ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int64, options, timestamp)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            DataPageVersion = version
        });
        if (optional)
        {
            var serialized = writer.CreateSerializedColumn<long?>(schema.LeafColumns[0]);
            serialized.Serialize(raw);
            writer.StartRowGroup().Write(serialized);
        }
        else
        {
            var serialized = writer.CreateSerializedColumn<long>(schema.LeafColumns[0]);
            serialized.Serialize(Array.ConvertAll(raw, value => value!.Value));
            writer.StartRowGroup().Write(serialized);
        }
        writer.CloseFile();
        return (schema, stream.ToArray());
    }
}
