using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Mutation;

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

    [Test]
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
        ClassicAssert.AreEqual(values, roundtripped);
    }

    [Test]
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
        ClassicAssert.AreEqual(values, roundtripped);
    }

    [Test]
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
        ClassicAssert.AreEqual(values, roundtripped);
    }

    // ──────────────── Boolean roundtrip ────────────────

    [Test]
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
        ClassicAssert.AreEqual(values, roundtripped);
    }

    // ──────────────── Float roundtrip ────────────────

    [Test]
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
        ClassicAssert.AreEqual(values, roundtripped);
    }

    [Test]
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
        ClassicAssert.AreEqual(values, roundtripped);
    }

    // ──────────────── Double roundtrip ────────────────

    [Test]
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
        ClassicAssert.AreEqual(values, roundtripped);
    }

    // ──────────────── Long roundtrip ────────────────

    [Test]
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
        ClassicAssert.AreEqual(values, roundtripped);
    }

    [Test]
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
        ClassicAssert.AreEqual(values, roundtripped);
    }

    // ──────────────── ByteArray roundtrip ────────────────

    [Test]
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
        ClassicAssert.AreEqual(values.Length, roundtripped.Length);
        for (var i = 0; i < values.Length; i++)
            ClassicAssert.AreEqual(values[i], roundtripped[i]);
    }

    [Test]
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
        ClassicAssert.AreEqual(values.Length, roundtripped.Length);
        for (var i = 0; i < values.Length; i++)
            ClassicAssert.AreEqual(values[i], roundtripped[i]);
    }

    // ──────────────── Multiple row groups ────────────────

    [Test]
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
        Assert.That(roundtripped, Is.EqualTo(new[] {1, 2, 3, 4, 5, 6}));
    }

    // ──────────────── Optional (nullable) columns ────────────────

    [Test]
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

        Assert.That(results, Is.EqualTo(new int?[] {1, null, 3, null, 5}));
    }

    // ──────────────── Empty row group ────────────────

    [Test]
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
        ClassicAssert.IsEmpty(roundtripped);
    }

    // ──────────────── Schema structure ────────────────

    [Test]
    public void Schema_SingleColumn_HasCorrectLeafPath()
    {
        var schema = new ParquetSchema([
            new Column("my_column", ParquetPhysicalType.Int32)
        ]);
        ClassicAssert.AreEqual(1, schema.Columns.Length);
        ClassicAssert.AreEqual("my_column", schema.Columns[0].Name);
        ClassicAssert.AreEqual(ParquetPhysicalType.Int32, schema.Columns[0].PhysicalType);
    }

    [Test]
    public void Schema_MultipleColumns_OrderPreserved()
    {
        var schema = new ParquetSchema([
            new Column("a", ParquetPhysicalType.Int32),
            new Column("b", ParquetPhysicalType.Float),
            new Column("c", ParquetPhysicalType.Boolean)
        ]);
        ClassicAssert.AreEqual(3, schema.Columns.Length);
        ClassicAssert.AreEqual("a", schema.Columns[0].Name);
        ClassicAssert.AreEqual("b", schema.Columns[1].Name);
        ClassicAssert.AreEqual("c", schema.Columns[2].Name);
    }

    [Test]
    public void Schema_Empty_HasNoColumns()
    {
        var schema = new ParquetSchema(ImmutableArray<Column>.Empty);
        ClassicAssert.IsEmpty(schema.Columns);
    }
}
