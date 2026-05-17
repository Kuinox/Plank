using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.StrykerTests;

/// <summary>
/// Tests targeting NoCoverage areas in ColumnChunkReader.cs:
/// - ExpandWithDefinitionLevels for byte?, ushort?, uint?, ulong? (lines 1417-1439)
/// - Non-buffer ByteStreamSplit paths for optional types (lines 979, 987, 996, 1004)
/// - Decompression path in DecodeValues (line 722)
/// - Definition level literal runs (line 1355)
/// </summary>
public class ColumnChunkReaderOptionalTests
{
    static byte[] WriteOptional<T>(Column col, T?[] values, CompressionKind comp = CompressionKind.None)
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

    // ──────────────── Optional byte? (line 1417) ────────────────

    [Fact]
    public void OptionalByte_WithNulls_ExpandsCorrectly()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new byte?[] { 10, null, 200, null, 42 };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<byte>(data, col));
    }

    [Fact]
    public void OptionalByte_AllNulls_ReturnsAllNull()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new byte?[] { null, null, null };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<byte>(data, col));
    }

    [Fact]
    public void OptionalByte_NoNulls_ReturnsAll()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new byte?[] { 1, 2, 3, 255 };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<byte>(data, col));
    }

    // ──────────────── Optional ushort? (line 1424) ────────────────

    [Fact]
    public void OptionalUShort_WithNulls_ExpandsCorrectly()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new ushort?[] { 100, null, 65535, null, 0 };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<ushort>(data, col));
    }

    [Fact]
    public void OptionalUShort_AllNulls_ReturnsAllNull()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new ushort?[] { null, null };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<ushort>(data, col));
    }

    // ──────────────── Optional uint? (line 1431) ────────────────

    [Fact]
    public void OptionalUInt_WithNulls_ExpandsCorrectly()
    {
        var col = new Column("v", ParquetPhysicalType.Int32, null,
            new LogicalType.Int(bitWidth: 32, isSigned: false));
        var optCol = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Int(bitWidth: 32, isSigned: false));
        var values = new uint?[] { 1_000_000, null, 0, null, uint.MaxValue };
        var data = WriteOptional(optCol, values);
        Assert.Equal(values, ReadOptional<uint>(data, optCol));
    }

    // ──────────────── Optional ulong? (line 1433) ────────────────

    [Fact]
    public void OptionalULong_WithNulls_ExpandsCorrectly()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Int(bitWidth: 64, isSigned: false));
        var values = new ulong?[] { 1_000_000_000UL, null, 0UL, null };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<ulong>(data, col));
    }

    // ──────────────── Optional with compression (line 722) ────────────────

    [Fact]
    public void OptionalInt32_Snappy_Compressed_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new int?[] { 1, null, 3, null, 5 };
        var data = WriteOptional(col, values, CompressionKind.Snappy);
        Assert.Equal(values, ReadOptional<int>(data, col));
    }

    [Fact]
    public void OptionalFloat_Snappy_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Float,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new float?[] { 1.5f, null, 3.5f };
        var data = WriteOptional(col, values, CompressionKind.Snappy);
        Assert.Equal(values, ReadOptional<float>(data, col));
    }

    // ──────────────── ByteStreamSplit optional (non-buffer path, lines 979-1033) ────────────────

    [Fact]
    public void OptionalByteStreamSplit_Int32_WithNulls()
    {
        // Optional ByteStreamSplit requires null expansion → uses non-buffer DecodeValues path
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.ByteStreamSplit]));
        var values = new int?[] { 100, null, -200, null, 0 };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<int>(data, col));
    }

    [Fact]
    public void OptionalByteStreamSplit_Float_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Float,
            new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.ByteStreamSplit]));
        var values = new float?[] { 1.5f, null, -2.5f, null };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<float>(data, col));
    }

    [Fact]
    public void OptionalByteStreamSplit_Double_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Double,
            new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.ByteStreamSplit]));
        var values = new double?[] { 1.5, null, 3.14, null, -7.0 };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<double>(data, col));
    }

    [Fact]
    public void OptionalByteStreamSplit_Int64_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.ByteStreamSplit]));
        var values = new long?[] { 1_000_000L, null, -500L };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<long>(data, col));
    }

    // ──────────────── Definition level literal runs (line 1355) ────────────────
    // Interleaved null/non-null creates literal groups (not RLE runs) in def levels

    [Fact]
    public void OptionalInt32_InterleavedNulls_LiteralGroups()
    {
        // Alternating null/value forces literal group of definition levels
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? (int?)i : null).ToArray();
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<int>(data, col));
    }

    [Fact]
    public void OptionalBool_InterleavedNulls_LiteralGroups()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(ParquetRepetition.Optional));
        // 16 alternating null/bool forces literal definition level groups
        var values = Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? (bool?)(i % 4 == 0) : null).ToArray();
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<bool>(data, col));
    }

    [Fact]
    public void OptionalFloat_ManyInterleavedNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Float,
            new ColumnOptions(ParquetRepetition.Optional));
        // 8 null then 8 non-null — forces both literal and RLE runs
        var values = Enumerable.Range(0, 16)
            .Select(i => i < 8 ? null : (float?)((float)i * 0.5f))
            .ToArray();
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<float>(data, col));
    }

    // ──────────────── DateOnly? (line 1440-1446) ────────────────

    [Fact]
    public void OptionalDateOnly_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional), new LogicalType.Date());
        var values = new DateOnly?[] { new DateOnly(2024, 1, 1), null, new DateOnly(2000, 6, 15) };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<DateOnly>(data, col));
    }

    // ──────────────── DateTime? (line 1447-1453) ────────────────

    [Fact]
    public void OptionalDateTime_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional),
            new LogicalType.Timestamp(TimeUnit.Micros, IsAdjustedToUtc: true));
        var ts = new DateTime(2024, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var values = new DateTime?[] { ts, null, DateTime.UnixEpoch };
        var data = WriteOptional(col, values);
        Assert.Equal(values, ReadOptional<DateTime>(data, col));
    }

    // ──────────────── Additional compressed optional test ────────────────

    [Fact]
    public void OptionalDouble_Snappy_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Double,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new double?[] { 1.5, null, 3.14, null };
        var data = WriteOptional(col, values, CompressionKind.Snappy);
        Assert.Equal(values, ReadOptional<double>(data, col));
    }

    [Fact]
    public void OptionalBool_Snappy_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(ParquetRepetition.Optional));
        var values = new bool?[] { true, null, false, null, true };
        var data = WriteOptional(col, values, CompressionKind.Snappy);
        Assert.Equal(values, ReadOptional<bool>(data, col));
    }
}
