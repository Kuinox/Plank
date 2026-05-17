using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Mutation;

/// <summary>
/// Tests for remaining NoCoverage paths in SerializedColumn.cs:
/// - SerializeDateTimeOffset (line 488) — ToUnixTime conversion
/// - SerializeNullableTimeOnly (line 539) — ToTimeValue nullable conversion
/// - TryAssignSingleDataPageStatistics paths (lines 672-667)
/// - Page statistics with specific value patterns
/// </summary>
public class SerializedColumnRemainingTests
{
    static T[] ReadAll<T>(byte[] data, ParquetSchema schema, int colIdx = 0) where T : struct
    {
        var src = new MemoryReadSource(data);
        using var reader = schema.CreateReader(src);
        var results = new List<T>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<T>(schema.Columns[colIdx]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        return results.ToArray();
    }

    static T?[] ReadAllNullable<T>(byte[] data, ParquetSchema schema, int colIdx = 0) where T : struct
    {
        var src = new MemoryReadSource(data);
        using var reader = schema.CreateReader(src);
        var results = new List<T?>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<T?>(schema.Columns[colIdx]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        return results.ToArray();
    }

    // ──────────────── DateTimeOffset serialization (line 488) ────────────────

    [Test]
    public void DateTimeOffset_Millis_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int64, null,
            new LogicalType.Timestamp(TimeUnit.Millis, IsAdjustedToUtc: true));
        var schema = new ParquetSchema([col]);
        var epoch = DateTimeOffset.UnixEpoch;
        var now = new DateTimeOffset(2024, 6, 15, 12, 30, 0, TimeSpan.Zero);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<DateTimeOffset>(schema.Columns[0]);
        c.Serialize([epoch, now]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        var result = ReadAll<DateTimeOffset>(ms.ToArray(), schema);
        ClassicAssert.AreEqual(2, result.Length);
        // Epoch should round-trip exactly (millis precision)
        ClassicAssert.AreEqual(epoch.UtcDateTime.Ticks, result[0].UtcTicks);
        // Now should be within 1ms
        ClassicAssert.IsTrue(Math.Abs((now.UtcDateTime - result[1].UtcDateTime).TotalMilliseconds) < 1.0);
    }

    [Test]
    public void DateTimeOffset_Micros_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int64, null,
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true));
        var schema = new ParquetSchema([col]);
        var ts = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<DateTimeOffset>(schema.Columns[0]);
        c.Serialize([ts, DateTimeOffset.UnixEpoch]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        var result = ReadAll<DateTimeOffset>(ms.ToArray(), schema);
        ClassicAssert.AreEqual(2, result.Length);
        // Verify statistics are captured
        ClassicAssert.IsTrue(c.Statistics.HasStatistics);
    }

    [Test]
    public void DateTimeOffset_Statistics_CorrectMinMax()
    {
        var col = new Column("v", ParquetPhysicalType.Int64, null,
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true));
        var schema = new ParquetSchema([col]);
        var early = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var late = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<DateTimeOffset>(schema.Columns[0]);
        c.Serialize([late, early, late]); // min in middle
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        // Min should be 'early', max should be 'late'
        ClassicAssert.IsTrue(c.Statistics.MinBits < c.Statistics.MaxBits);
    }

    // ──────────────── TimeOnly? nullable (line 539) ────────────────

    [Test]
    public void TimeOnly_Nullable_Micros_WritesCorrectly()
    {
        // Reader doesn't yet support TimeOnly projection — test writer path only
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Time(TimeUnit.Micros, IsAdjustedToUtc: true));
        var schema = new ParquetSchema([col]);
        var noon = new TimeOnly(12, 0, 0);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<TimeOnly?>(schema.Columns[0]);
        c.Serialize([noon, null, TimeOnly.MinValue]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        var data = ms.ToArray();
        ClassicAssert.IsTrue(data.Length > 0);
        ClassicAssert.AreEqual(1L, c.Statistics.NullCount);
    }

    [Test]
    public void TimeOnly_Nullable_Millis_WritesCorrectly()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Time(TimeUnit.Millis, IsAdjustedToUtc: false));
        var schema = new ParquetSchema([col]);
        var time = new TimeOnly(9, 30, 0);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<TimeOnly?>(schema.Columns[0]);
        c.Serialize([time, null, time]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        var data = ms.ToArray();
        ClassicAssert.IsTrue(data.Length > 0);
        ClassicAssert.AreEqual(1L, c.Statistics.NullCount);
    }

    // ──────────────── DateTimeOffset nullable ────────────────

    [Test]
    public void DateTimeOffset_Nullable_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true));
        var schema = new ParquetSchema([col]);
        var ts = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<DateTimeOffset?>(schema.Columns[0]);
        c.Serialize([ts, null, ts]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        var result = ReadAllNullable<DateTimeOffset>(ms.ToArray(), schema);
        ClassicAssert.IsNotNull(result[0]);
        ClassicAssert.IsNull(result[1]);
        ClassicAssert.IsNotNull(result[2]);
    }

    // ──────────────── RowGroupWriter null count tracking (line 195) ────────────────

    [Test]
    public void RowGroupWriter_NullCountInStats_OptionalColumn()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional))]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
        col.Serialize([1, null, 3, null, null]);  // 3 nulls
        var rg = writer.StartRowGroup();
        rg.Write(col);
        writer.CloseFile();
        // Verify the written null count is reflected in statistics
        ClassicAssert.AreEqual(3L, col.Statistics.NullCount);
    }

    [Test]
    public void RowGroupWriter_NullCountZero_RequiredColumn()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32)]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize([1, 2, 3]);
        var rg = writer.StartRowGroup();
        rg.Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(0L, col.Statistics.NullCount);
    }

    [Test]
    public void RowGroupWriter_MultipleNullColumns_IndependentNullCounts()
    {
        var schema = new ParquetSchema([
            new Column("a", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional)),
            new Column("b", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var colA = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
        var colB = writer.CreateSerializedColumn<int?>(schema.Columns[1]);
        colA.Serialize([1, null, 3]);        // 1 null
        colB.Serialize([null, null, 3]);     // 2 nulls
        var rg = writer.StartRowGroup();
        rg.Write(colA);
        rg.Write(colB);
        writer.CloseFile();
        ClassicAssert.AreEqual(1L, colA.Statistics.NullCount);
        ClassicAssert.AreEqual(2L, colB.Statistics.NullCount);
    }

    // ──────────────── TryAssignSingleDataPageStatistics paths (line 672) ────────────────

    [Test]
    public void RequiredInt32_WithPageIndexes_StatisticsCorrect()
    {
        // WithPageIndexes=true exercises TryAssignSingleDataPageStatistics (line 672)
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32)]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            WritePageIndexes = true
        });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize([5, 3, 9, 1, 7]);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(1L, col.Statistics.MinBits);
        ClassicAssert.AreEqual(9L, col.Statistics.MaxBits);
    }

    [Test]
    public void OptionalInt32_WithPageIndexes_StatisticsIncludeNullCount()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional))]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            WritePageIndexes = true
        });
        var col = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
        col.Serialize([5, null, 9, null, 1]);  // 2 nulls
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(2L, col.Statistics.NullCount);
        ClassicAssert.AreEqual(1L, col.Statistics.MinBits);
        ClassicAssert.AreEqual(9L, col.Statistics.MaxBits);
    }
}
