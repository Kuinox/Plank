using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.StrykerTests;

/// <summary>
/// Tests targeting NoCoverage in ColumnChunkReader.cs ExpandWithDefinitionLevels<T>:
/// - DateTimeOffset? (line 1460)
/// - TimeOnly? (line 1467) — if supported
/// - ReadOnlyMemory<byte>? (line 1474)
/// Also covers remaining NoCoverage in definition-level literal runs.
/// </summary>
public class ColumnChunkReaderExpandTests
{
    static byte[] WriteOptionalCol<T>(Column col, T?[] values, CompressionKind comp = CompressionKind.None)
        where T : struct
    {
        var schema = new ParquetSchema([col]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = comp });
        var c = writer.CreateSerializedColumn<T?>(schema.Columns[0]);
        c.Serialize(values);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        return ms.ToArray();
    }

    static T?[] ReadOptional<T>(byte[] data, Column col) where T : struct
    {
        var schema = new ParquetSchema([col]);
        var src = new MemoryReadSource(data);
        using var reader = schema.CreateReader(src);
        var results = new List<T?>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<T?>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        return results.ToArray();
    }

    // ──────────────── DateTimeOffset? (line 1460) ────────────────

    [Fact]
    public void DateTimeOffset_Optional_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true));
        var ts = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var values = new DateTimeOffset?[] { ts, null, DateTimeOffset.UnixEpoch, null };
        var data = WriteOptionalCol(col, values);
        var result = ReadOptional<DateTimeOffset>(data, col);
        Assert.Equal(4, result.Length);
        Assert.Equal(ts.UtcDateTime, result[0]?.UtcDateTime);
        Assert.Null(result[1]);
        Assert.Equal(DateTimeOffset.UnixEpoch.UtcDateTime, result[2]?.UtcDateTime);
        Assert.Null(result[3]);
    }

    [Fact]
    public void DateTimeOffset_Optional_AllNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true));
        var values = new DateTimeOffset?[] { null, null };
        var data = WriteOptionalCol(col, values);
        var result = ReadOptional<DateTimeOffset>(data, col);
        Assert.Equal(new DateTimeOffset?[] { null, null }, result);
    }

    [Fact]
    public void DateTimeOffset_Optional_SingleValue()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true));
        var ts = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var values = new DateTimeOffset?[] { null, ts, null };
        var data = WriteOptionalCol(col, values);
        var result = ReadOptional<DateTimeOffset>(data, col);
        Assert.Null(result[0]);
        Assert.Equal(ts.UtcDateTime, result[1]?.UtcDateTime);
        Assert.Null(result[2]);
    }

    // ──────────────── More DateOnly? tests (to improve line coverage) ────────────────

    [Fact]
    public void DateOnly_Optional_ManyValues()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional), new LogicalType.Date());
        // 16 alternating values (forces literal def-level groups)
        var values = Enumerable.Range(0, 16)
            .Select(i => i % 2 == 0 ? (DateOnly?)new DateOnly(2024, 1, i + 1) : null)
            .ToArray();
        var data = WriteOptionalCol(col, values);
        var result = ReadOptional<DateOnly>(data, col);
        Assert.Equal(16, result.Length);
        for (var i = 0; i < 16; i++)
        {
            if (i % 2 == 0)
            {
                Assert.NotNull(result[i]);
                Assert.Equal(new DateOnly(2024, 1, i + 1), result[i]);
            }
            else Assert.Null(result[i]);
        }
    }

    // ──────────────── More DateTime? tests ────────────────

    [Fact]
    public void DateTime_Optional_ManyValues()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true));
        var ts = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var values = Enumerable.Range(0, 10)
            .Select(i => i % 3 == 0 ? (DateTime?)ts.AddDays(i) : null)
            .ToArray();
        var data = WriteOptionalCol(col, values);
        var result = ReadOptional<DateTime>(data, col);
        Assert.Equal(10, result.Length);
        for (var i = 0; i < 10; i++)
        {
            if (i % 3 == 0) Assert.Equal(ts.AddDays(i), result[i]);
            else Assert.Null(result[i]);
        }
    }

    // ──────────────── Large optional column — exercises definition level literal runs ────────────────

    [Fact]
    public void OptionalInt32_50Values_InterleavedNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        // Alternating null/value forces definition level bit-packed literal groups
        var values = Enumerable.Range(0, 50)
            .Select(i => i % 2 == 0 ? (int?)i : null)
            .ToArray();
        var data = WriteOptionalCol(col, values);
        var result = ReadOptional<int>(data, col);
        Assert.Equal(50, result.Length);
        for (var i = 0; i < 50; i++)
            Assert.Equal(i % 2 == 0 ? (int?)i : null, result[i]);
    }

    [Fact]
    public void OptionalFloat_50Values_InterleavedNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Float,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = Enumerable.Range(0, 50)
            .Select(i => i % 2 == 0 ? (float?)((float)i * 0.5f) : null)
            .ToArray();
        var data = WriteOptionalCol(col, values);
        var result = ReadOptional<float>(data, col);
        Assert.Equal(50, result.Length);
        for (var i = 0; i < 50; i++)
            Assert.Equal(i % 2 == 0 ? (float?)((float)i * 0.5f) : null, result[i]);
    }

    // ──────────────── Compressed optional columns (exercises line 723) ────────────────

    [Fact]
    public void OptionalInt64_Snappy_InterleavedNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new long?[] { 100L, null, null, 200L, null, 300L };
        var data = WriteOptionalCol(col, values, CompressionKind.Snappy);
        var result = ReadOptional<long>(data, col);
        Assert.Equal(values, result);
    }

    // ──────────────── EncodeOptional with multiple fixed-size types ────────────────

    [Fact]
    public void OptionalBool_ManyInterleavedNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = Enumerable.Range(0, 32)
            .Select(i => i % 3 == 0 ? (bool?)(i % 6 == 0) : null)
            .ToArray();
        var data = WriteOptionalCol(col, values);
        var result = ReadOptional<bool>(data, col);
        Assert.Equal(32, result.Length);
        for (var i = 0; i < 32; i++)
            Assert.Equal(i % 3 == 0 ? (bool?)(i % 6 == 0) : null, result[i]);
    }
}
