using System.Buffers.Binary;
using System.Collections.Immutable;
using Plank.Reading;
using Plank.Reading.Logical;
using Plank.Reading.Logical.Internal;
using Plank.Schema;
using Plank.Tests.Reading.ParquetTesting;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Reading;

internal sealed class Int96TimestampTests
{
    static byte[] Raw(int day, long nanos)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, nanos);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), day);
        return bytes;
    }

    static T[] Read<T>(byte[] file, ParquetSchema schema)
    {
        using var reader = schema.CreateReader(new MemoryReadSource(file));
        var values = new List<T>();
        foreach (var buffer in reader.RowGroups[0].Column<T>(0))
            values.AddRange(buffer.Values);
        return values.ToArray();
    }

    static byte[] Write(byte[][] values, EncodingKind encoding)
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int96,
            options: new ColumnOptions(encodings: ImmutableArray.Create(encoding)),
            pageStrategy: encoding == EncodingKind.RleDictionary ? ForceDictionaryPageStrategy.Shared : null)]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream))
        {
            var group = writer.StartRowGroup();
            var col = group.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
            col.Serialize(values);
            group.Write(col);
            writer.CloseFile();
        }
        return stream.ToArray();
    }

    [Test]
    public void BoundariesAndEncodings()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int96)]);
        var values = new[] { Raw(1721426, 0), Raw(5373484, 86_399_999_999_999),
            Raw(2440588, 123), Raw(2440588, 86_400_000_000_000) };
        DateTime[] expected = [DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
            DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), DateTime.UnixEpoch.AddTicks(1),
            DateTime.UnixEpoch.AddDays(1)];
        foreach (var encoding in new[] { EncodingKind.Plain, EncodingKind.RleDictionary })
        {
            var file = Write(values, encoding);
            var decoded = Read<DateTime>(file, schema);
            if (!decoded.SequenceEqual(expected) || decoded.Any(x => x.Kind != DateTimeKind.Utc))
                throw new Exception("DateTime mismatch");
            if (!Read<DateTimeOffset>(file, schema).SequenceEqual(expected.Select(x => new DateTimeOffset(x))))
                throw new Exception("DateTimeOffset mismatch");
            foreach (var (day, nanos) in new[] { (1721425, 123L), (5373485, 456L) })
            {
                var raw = Raw(day, nanos);
                var error = Assert.Throws<CorruptParquetException>(() => Read<DateTime>(Write([raw], encoding), schema));
                var expectedMessage = $"INT96 timestamp (Julian day {day}, nanoseconds {nanos}) is outside the range representable by a date and time.";
                if (error.Message != expectedMessage) throw new Exception($"Unexpected error: {error.Message}");
            }
        }
    }

    [Test]
    public void ParquetSharpPlainAndDictionary()
    {
        foreach (var optional in new[] { false, true })
        foreach (var dictionary in new[] { false, true })
        {
            using var stream = new MemoryStream();
            using (var builder = new ParquetSharp.WriterPropertiesBuilder())
            {
                if (dictionary) builder.EnableDictionary(); else builder.DisableDictionary();
                using var properties = builder.Build();
                ParquetSharp.Column column = optional
                    ? new ParquetSharp.Column<ParquetSharp.Int96?>("value")
                    : new ParquetSharp.Column<ParquetSharp.Int96>("value");
                using var writer = new ParquetSharp.ParquetFileWriter(stream, [column], null, properties, null, true);
                using (var group = writer.AppendRowGroup())
                using (var physical = group.NextColumn())
                {
                    var typed = (ParquetSharp.ColumnWriter<ParquetSharp.Int96>)physical;
                    ParquetSharp.Int96[] values = [new(123, 0, 2440588), new(0, 0, 2440589), new(123, 0, 2440588)];
                    if (optional) typed.WriteBatch(4, [1, 0, 1, 1], [], values);
                    else typed.WriteBatch(values);
                }
                writer.Close();
            }
            var schema = new ParquetSchema([optional
                ? ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int96)
                : ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int96)]);
            var file = stream.ToArray();
            DateTime?[] expected = optional
                ? [DateTime.UnixEpoch.AddTicks(1), null, DateTime.UnixEpoch.AddDays(1), DateTime.UnixEpoch.AddTicks(1)]
                : [DateTime.UnixEpoch.AddTicks(1), DateTime.UnixEpoch.AddDays(1), DateTime.UnixEpoch.AddTicks(1)];
            if (!Read<DateTime?>(file, schema).SequenceEqual(expected)) throw new Exception("ParquetSharp mismatch");
            if (!optional && !Read<DateTime>(file, schema).SequenceEqual(expected.Select(x => x!.Value)))
                throw new Exception("ParquetSharp required mismatch");
            if (!optional && !Read<DateTimeOffset>(file, schema).SequenceEqual(expected.Select(x => new DateTimeOffset(x!.Value))))
                throw new Exception("ParquetSharp required offset mismatch");
            if (!Read<DateTimeOffset?>(file, schema).SequenceEqual(expected.Select(x => x.HasValue ? new DateTimeOffset?(new DateTimeOffset(x.Value)) : null)))
                throw new Exception("ParquetSharp offset mismatch");
        }
    }

    [Test]
    public void OptionalValuesAndGeneratedRows()
    {
        foreach (var encoding in new[] { EncodingKind.Plain, EncodingKind.RleDictionary })
        {
            var options = new ColumnOptions(encodings: ImmutableArray.Create(encoding));
            var strategy = encoding == EncodingKind.RleDictionary ? ForceDictionaryPageStrategy.Shared : null;
            var schema = new ParquetSchema([
                ColumnDefinition.RequiredLeaf("Date", ParquetPhysicalType.Int96, options, pageStrategy: strategy),
                ColumnDefinition.OptionalLeaf("Maybe", ParquetPhysicalType.Int96, options, pageStrategy: strategy),
                ColumnDefinition.RequiredLeaf("Offset", ParquetPhysicalType.Int96, options, pageStrategy: strategy),
                ColumnDefinition.OptionalLeaf("Descriptors", ParquetPhysicalType.ByteArray,
                    logicalType: new LogicalType.String()),
                ColumnDefinition.RequiredLeaf("Value", ParquetPhysicalType.Int32)]);
            using var stream = new MemoryStream();
            using (var writer = schema.CreateWriter(stream))
            {
                var group = writer.StartRowGroup();
                var date = group.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
                date.Serialize([Raw(2440588, 123), Raw(2440589, 0)]); group.Write(date);
                var maybe = group.CreateSerializedColumn<byte[]>(schema.LeafColumns[1]);
                maybe.Serialize([Raw(2440588, 123), null!]); group.Write(maybe);
                var offset = group.CreateSerializedColumn<byte[]>(schema.LeafColumns[2]);
                offset.Serialize([Raw(2440588, 123), Raw(2440589, 0)]); group.Write(offset);
                var text = group.CreateSerializedColumn<byte[]>(schema.LeafColumns[3]);
                text.Serialize(["hi"u8.ToArray(), null!]); group.Write(text);
                var number = group.CreateSerializedColumn<int>(schema.LeafColumns[4]);
                number.Serialize([42, 43]); group.Write(number);
                writer.CloseFile();
            }
            var file = stream.ToArray();
            using (var logical = schema.CreateReader(new MemoryReadSource(file)))
            {
                var values = new List<DateTime?>();
                foreach (var batch in logical.RowGroups[0].Column<DateTime?>(1)) values.AddRange(batch.Values);
                if (!values.SequenceEqual(new DateTime?[] { DateTime.UnixEpoch.AddTicks(1), null }))
                    throw new Exception("Optional timestamp mismatch");
            }
            var error = Assert.Throws<InvalidOperationException>(() => Int96Row.CreateRowReader(new MemoryStream(file)));
            if (!error.Message.Contains("has physical type Int96, expected Int64", StringComparison.Ordinal))
                throw new Exception("Unexpected strict schema message", error);
            using var reader = Int96Row.CreateRowReader(new MemoryStream(file), schemaEvolution: new()
            { PhysicalTypes = SchemaTypeEvolutionBehavior.AllowCompatible });
            if (!reader.MoveNext() || reader.Current.Date != DateTime.UnixEpoch.AddTicks(1) ||
                reader.Current.Maybe != DateTime.UnixEpoch.AddTicks(1) ||
                reader.Current.Offset != DateTimeOffset.UnixEpoch.AddTicks(1) ||
                reader.Current.Descriptors != "hi" || reader.Current.Value != 42)
                throw new Exception("First generated row mismatch");
            if (!reader.MoveNext() || reader.Current.Date != DateTime.UnixEpoch.AddDays(1) ||
                reader.Current.Maybe != null || reader.Current.Descriptors != null || reader.Current.Value != 43)
                throw new Exception("Second generated row mismatch");
        }
    }

    [Test]
    public void NestedTimestampFailsClearly()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredGroup("Group",
            ColumnDefinition.RequiredLeaf("Date", ParquetPhysicalType.Int96))]);
        using var stream = new MemoryStream();
        using (var writer = schema.CreateWriter(stream))
        {
            var group = writer.StartRowGroup();
            var column = group.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
            column.Serialize([Raw(2440588, 0)]); group.Write(column); writer.CloseFile();
        }
        var error = Assert.Throws<NotSupportedException>(() => NestedInt96Row.CreateRowReader(
            new MemoryStream(stream.ToArray()), schemaEvolution: new()
            { PhysicalTypes = SchemaTypeEvolutionBehavior.AllowCompatible }));
        if (!error.Message.Contains("Nested INT96 timestamp", StringComparison.Ordinal))
            throw new Exception("Expected a nested INT96 diagnostic", error);
    }

    [Test]
    public void NullableBatchesCrossBoundaries()
    {
        foreach (var encoding in new[] { EncodingKind.Plain, EncodingKind.RleDictionary })
        foreach (var hasNulls in new[] { false, true })
        {
            var options = new ColumnOptions(encodings: ImmutableArray.Create(encoding));
            var schema = new ParquetSchema([ColumnDefinition.OptionalLeaf("value", ParquetPhysicalType.Int96,
                options, pageStrategy: encoding == EncodingKind.RleDictionary ? ForceDictionaryPageStrategy.Shared : null)]);
            var raw = new byte[40_000][];
            for (var i = 0; i < raw.Length; i++) raw[i] = hasNulls && i % 7 == 0 ? null! : Raw(2440588 + i % 2, 123);
            using var stream = new MemoryStream();
            using (var writer = schema.CreateWriter(stream))
            {
                var group = writer.StartRowGroup();
                var column = group.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
                column.Serialize(raw); group.Write(column); writer.CloseFile();
            }
            var file = stream.ToArray();
            var dates = Read<DateTime?>(file, schema);
            var offsets = Read<DateTimeOffset?>(file, schema);
            if (dates.Length != raw.Length || offsets.Length != raw.Length) throw new Exception("Incorrect batch size");
            for (var i = 0; i < raw.Length; i++)
            {
                DateTime? expected = raw[i] is null ? null : DateTime.UnixEpoch.AddDays(i % 2).AddTicks(1);
                if (dates[i] != expected || offsets[i] != (expected.HasValue ? new DateTimeOffset(expected.Value) : null))
                    throw new Exception($"Incorrect batch value at {i}");
            }
        }
    }

    [Test]
    public void TruncatedPlainAndDictionaryPayloadsAreCorrupt()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int96)]);
        var column = schema.LeafColumns[0].Column;
        var buffers = default(ColumnReadBuffers<DateTime>);
        var page = default(ColumnChunkReader.FixedWidthPageState);
        try
        {
            for (var length = 0; length < 12; length++)
            {
                var payload = new byte[length];
                var header = new PageHeader(PageHeaderType.DataPage, (uint)length, (uint)length, 1,
                    EncodingKind.Plain, HeaderLength: 1, RepetitionLevelsByteLength: 0,
                    DefinitionLevelsByteLength: 0, NullCount: 0, IsCompressed: false,
                    RepetitionLevelEncoding: EncodingKind.Rle, DefinitionLevelEncoding: EncodingKind.Rle, RowCount: 1);
                _ = Assert.Throws<CorruptParquetException>(() => ColumnChunkReader.TryStartFixedWidthPageBatches(
                    header, payload, column, 1, ref buffers, DefaultParquetBufferPool.Shared, ref page, out _));
                var dictionary = header with { Type = PageHeaderType.DictionaryPage };
                _ = Assert.Throws<CorruptParquetException>(() => ColumnChunkReader.TryDecodeDictionaryPageIntoNative(
                    dictionary, payload, column, ref buffers, DefaultParquetBufferPool.Shared));
            }
        }
        finally { buffers.Dispose(); }
    }

    [Test]
    public void TimestampOrderFixture()
    {
        var schema = new ParquetSchema([ColumnDefinition.RequiredLeaf("ts", ParquetPhysicalType.Int96)]);
        var actual = Read<DateTime>(ParquetTestingCorpus.ReadAllBytes("data/int96_timestamp_order.parquet"), schema);
        DateTime[] expected = [DateTime.UnixEpoch.AddDays(1).AddTicks(-1), DateTime.UnixEpoch.AddDays(1),
            new DateTime(1968, 5, 23, 0, 0, 0, DateTimeKind.Utc).AddTicks(1), DateTime.UnixEpoch.AddTicks(10)];
        if (!actual.SequenceEqual(expected)) throw new Exception("Fixture mismatch");
    }

    [Test]
    public void SparkOutOfRangeIsCorrupt()
    {
        var schema = new ParquetSchema([ColumnDefinition.OptionalLeaf("a", ParquetPhysicalType.Int96)]);
        var file = ParquetTestingCorpus.ReadAllBytes("data/int96_from_spark.parquet");
        _ = Assert.Throws<CorruptParquetException>(() => Read<DateTime?>(file, schema));
        var raw = new List<byte[]>();
        using (var reader = schema.CreateReader(new MemoryReadSource(file)))
        {
            foreach (var buffer in reader.RowGroups[0].Column<byte>(0))
                for (var i = 0; i < buffer.Count; i++)
                    raw.Add(buffer.IsNull(i) ? null! : buffer.GetValue(i).ToArray());
        }
        if (raw.Count != 6 || raw[4] is not null) throw new Exception("Unexpected Spark nulls");
        long[] micros = [1704141296123456, 1704070800000000, 253402225200000000, 1735599600000000];
        var required = new ParquetSchema([ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int96)]);
        var decoded = Read<DateTime>(Write(raw.Take(4).ToArray(), EncodingKind.Plain), required);
        if (!decoded.SequenceEqual(micros.Select(x => DateTime.UnixEpoch.AddTicks(x * 10))))
            throw new Exception("Spark values mismatch");
    }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class Int96Row
{
    public DateTime Date { get; init; }
    public DateTime? Maybe { get; init; }
    public DateTimeOffset Offset { get; init; }
    public string? Descriptors { get; init; }
    public int Value { get; init; }
}

[ParquetSchema(AllowAllocatingValues = true)]
internal sealed partial class NestedInt96Row
{
    public NestedInt96Group Group { get; init; } = new();
}

internal sealed class NestedInt96Group
{
    public DateTime Date { get; init; }
}
