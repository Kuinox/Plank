using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.StrykerTests;

public class WriterReaderRoundtripTests
{
    static byte[] WriteToBytes(ParquetSchema schema, Action<ParquetWriter> write)
    {
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        write(writer);
        writer.CloseFile();
        return ms.ToArray();
    }

    static T[] ReadColumnFromBytes<T>(byte[] data, ParquetSchema schema, int columnIndex = 0)
    {
        var source = new MemoryReadSource(data);
        using var reader = schema.CreateReader(source);
        var results = new List<T>();
        foreach (var token in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(source, token);
            foreach (var page in rg.Column<T>(schema.Columns[columnIndex]).Pages)
                foreach (var val in page.Values.Span)
                    results.Add(val);
        }
        return results.ToArray();
    }

    // ──────────────── Int32 roundtrip ────────────────

    [Fact]
    public void Int32_Plain_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.Plain]))
        ]);
        var values = new int[] { -1, 0, 1, int.MaxValue, int.MinValue, 42 };
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<int>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<int>(data, schema);
        Assert.Equal(values, roundtripped);
    }

    [Fact]
    public void Int32_DeltaBinaryPacked_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]))
        ]);
        var values = Enumerable.Range(-50, 300).ToArray();
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<int>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<int>(data, schema);
        Assert.Equal(values, roundtripped);
    }

    [Fact]
    public void Int32_RleDictionary_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.RleDictionary]))
        ]);
        var values = Enumerable.Repeat(new[] { 1, 2, 3 }, 100).SelectMany(x => x).ToArray();
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<int>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<int>(data, schema);
        Assert.Equal(values, roundtripped);
    }

    // ──────────────── Boolean roundtrip ────────────────

    [Fact]
    public void Boolean_Plain_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Boolean)
        ]);
        var values = new bool[] { true, false, true, true, false };
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<bool>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<bool>(data, schema);
        Assert.Equal(values, roundtripped);
    }

    // ──────────────── Float roundtrip ────────────────

    [Fact]
    public void Float_Plain_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Float)
        ]);
        var values = new float[] { -1.5f, 0f, 1.5f, float.MaxValue, float.MinValue };
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<float>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<float>(data, schema);
        Assert.Equal(values, roundtripped);
    }

    [Fact]
    public void Float_ByteStreamSplit_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Float,
                new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]))
        ]);
        var values = new float[] { 1.1f, 2.2f, 3.3f, -4.4f, 0f };
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<float>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<float>(data, schema);
        Assert.Equal(values, roundtripped);
    }

    // ──────────────── Double roundtrip ────────────────

    [Fact]
    public void Double_Plain_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Double)
        ]);
        var values = new double[] { -1.5, 0.0, 1.5, double.MaxValue, double.MinValue };
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<double>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<double>(data, schema);
        Assert.Equal(values, roundtripped);
    }

    // ──────────────── Long roundtrip ────────────────

    [Fact]
    public void Int64_Plain_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Int64)
        ]);
        var values = new long[] { long.MinValue, -1L, 0L, 1L, long.MaxValue };
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<long>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<long>(data, schema);
        Assert.Equal(values, roundtripped);
    }

    [Fact]
    public void Int64_DeltaBinaryPacked_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Int64,
                new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]))
        ]);
        var values = Enumerable.Range(0, 200).Select(i => (long)i * 1_000_000L).ToArray();
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<long>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<long>(data, schema);
        Assert.Equal(values, roundtripped);
    }

    // ──────────────── ByteArray roundtrip ────────────────

    [Fact]
    public void ByteArray_Plain_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.ByteArray)
        ]);
        var values = new byte[][] {
            new byte[] { 1, 2, 3 },
            new byte[] { },
            new byte[] { 255, 0, 127 }
        };
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<byte[]>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<byte[]>(data, schema);
        Assert.Equal(values.Length, roundtripped.Length);
        for (var i = 0; i < values.Length; i++)
            Assert.Equal(values[i], roundtripped[i]);
    }

    [Fact]
    public void ByteArray_DeltaLengthByteArray_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [EncodingKind.DeltaLengthByteArray]))
        ]);
        var values = new byte[][] {
            new byte[] { 1, 2, 3 },
            new byte[] { 4 },
            new byte[] { 5, 6 }
        };
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<byte[]>(schema.Columns[0]);
            col.Serialize(values);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<byte[]>(data, schema);
        Assert.Equal(values.Length, roundtripped.Length);
        for (var i = 0; i < values.Length; i++)
            Assert.Equal(values[i], roundtripped[i]);
    }

    // ──────────────── Multiple row groups ────────────────

    [Fact]
    public void TwoRowGroups_AllValuesPreserved()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Int32)
        ]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col1 = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col1.Serialize([1, 2, 3]);
        writer.StartRowGroup().Write(col1);

        var col2 = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col2.Serialize([4, 5, 6]);
        writer.StartRowGroup().Write(col2);

        writer.CloseFile();
        var data = ms.ToArray();

        var roundtripped = ReadColumnFromBytes<int>(data, schema);
        Assert.Equal([1, 2, 3, 4, 5, 6], roundtripped);
    }

    // ──────────────── Optional (nullable) columns ────────────────

    [Fact]
    public void OptionalInt32_Roundtrip()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
        col.Serialize([1, null, 3, null, 5]);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        var data = ms.ToArray();

        var source = new MemoryReadSource(data);
        using var reader = schema.CreateReader(source);
        var results = new List<int?>();
        foreach (var token in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(source, token);
            foreach (var page in rg.Column<int?>(schema.Columns[0]).Pages)
                foreach (var val in page.Values.Span)
                    results.Add(val);
        }

        Assert.Equal([1, null, 3, null, 5], results);
    }

    // ──────────────── Empty row group ────────────────

    [Fact]
    public void EmptyRowGroup_ReadsZeroValues()
    {
        var schema = new ParquetSchema([
            new Column("v", ParquetPhysicalType.Int32)
        ]);
        var data = WriteToBytes(schema, w =>
        {
            var col = w.CreateSerializedColumn<int>(schema.Columns[0]);
            col.Serialize([]);
            w.StartRowGroup().Write(col);
        });
        var roundtripped = ReadColumnFromBytes<int>(data, schema);
        Assert.Empty(roundtripped);
    }

    // ──────────────── Schema structure ────────────────

    [Fact]
    public void Schema_SingleColumn_HasCorrectLeafPath()
    {
        var schema = new ParquetSchema([
            new Column("my_column", ParquetPhysicalType.Int32)
        ]);
        Assert.Equal(1, schema.Columns.Length);
        Assert.Equal("my_column", schema.Columns[0].Name);
        Assert.Equal(ParquetPhysicalType.Int32, schema.Columns[0].PhysicalType);
    }

    [Fact]
    public void Schema_MultipleColumns_OrderPreserved()
    {
        var schema = new ParquetSchema([
            new Column("a", ParquetPhysicalType.Int32),
            new Column("b", ParquetPhysicalType.Float),
            new Column("c", ParquetPhysicalType.Boolean)
        ]);
        Assert.Equal(3, schema.Columns.Length);
        Assert.Equal("a", schema.Columns[0].Name);
        Assert.Equal("b", schema.Columns[1].Name);
        Assert.Equal("c", schema.Columns[2].Name);
    }

    [Fact]
    public void Schema_Empty_HasNoColumns()
    {
        var schema = new ParquetSchema(ImmutableArray<Column>.Empty);
        Assert.Empty(schema.Columns);
    }
}
