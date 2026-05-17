using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.StrykerTests;

/// <summary>
/// Tests targeting NoCoverage areas in ColumnChunkReader.cs:
/// - DecodePlainInt32 for byte/ushort/uint target types (lines 791-809)
/// - DecodePlainBoolean (line 776) — bitwise decoding
/// - ByteStreamSplit decoders for byte/ushort/uint/ulong (lines 979-1033)
/// - Optional columns with nulls (definition level decoding)
/// - Dictionary encoding with nullable columns
/// </summary>
public class ColumnChunkReaderTests
{
    static byte[] WriteAndClose<T>(Column col, T[] values, CompressionKind comp = CompressionKind.None)
    {
        var schema = new ParquetSchema([col]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = comp });
        var c = writer.CreateSerializedColumn<T>(schema.Columns[0]);
        c.Serialize(values);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        return ms.ToArray();
    }

    static T[] ReadAll<T>(byte[] data, Column col)
    {
        var schema = new ParquetSchema([col]);
        var src = new MemoryReadSource(data);
        using var reader = schema.CreateReader(src);
        var results = new List<T>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<T>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        return results.ToArray();
    }

    static T?[] ReadAllNullable<T>(byte[] data, Column col) where T : struct
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

    // ──────────────── DecodePlainInt32 for byte (line 791-795) ────────────────

    [Test]
    public void Read_Byte_Plain_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]));
        var values = new byte[] { 0, 1, 127, 128, 255, 42 };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<byte>(data, col));
    }

    [Test]
    public void Read_Byte_Plain_SingleValue()
    {
        var col = new Column("v", ParquetPhysicalType.Int32);
        var data = WriteAndClose(col, new byte[] { 200 });
        ClassicAssert.AreEqual(new byte[] { 200 }, ReadAll<byte>(data, col));
    }

    // ──────────────── DecodePlainInt32 for ushort (line 799-803) ────────────────

    [Test]
    public void Read_UShort_Plain_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]));
        var values = new ushort[] { 0, 1, 1000, 65535, 32768 };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<ushort>(data, col));
    }

    // ──────────────── DecodePlainInt32 for uint (line 807-810) ────────────────

    [Test]
    public void Read_UInt_Plain_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int32, null,
            new LogicalType.Int(bitWidth: 32, isSigned: false));
        var values = new uint[] { 0, 1, 1_000_000, uint.MaxValue };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<uint>(data, col));
    }

    // ──────────────── DecodePlainBoolean — bitwise decoding (line 776) ────────────────

    [Test]
    public void Read_Boolean_Plain_AllTrue()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean);
        var values = new bool[] { true, true, true, true, true, true, true, true };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<bool>(data, col));
    }

    [Test]
    public void Read_Boolean_Plain_MixedPattern()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean);
        // Tests that bit shifting (i & 7) and byte indexing (i >> 3) work correctly
        var values = new bool[] { true, false, true, false, true, false, true, false, true };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<bool>(data, col));
    }

    [Test]
    public void Read_Boolean_Plain_SingleTrue()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean);
        var data = WriteAndClose(col, new bool[] { true });
        Assert.That(ReadAll<bool>(data, col), Is.EqualTo(new[] {true}));
    }

    [Test]
    public void Read_Boolean_Plain_CrossByteBoundary()
    {
        // 9 values cross the byte boundary: bits 0-7 in byte 0, bit 8 in byte 1
        var col = new Column("v", ParquetPhysicalType.Boolean);
        var values = Enumerable.Range(0, 9).Select(i => i % 3 == 0).ToArray();
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<bool>(data, col));
    }

    // ──────────────── ByteStreamSplit for byte (lines 983-989) ────────────────

    [Test]
    public void Read_Byte_ByteStreamSplit_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var values = new byte[] { 10, 20, 30, 128, 255 };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<byte>(data, col));
    }

    // ──────────────── ByteStreamSplit for ushort (lines 991-996) ────────────────

    [Test]
    public void Read_UShort_ByteStreamSplit_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var values = new ushort[] { 0, 256, 1000, 65535 };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<ushort>(data, col));
    }

    // ──────────────── ByteStreamSplit for uint (lines 999-1004) ────────────────

    [Test]
    public void Read_UInt_ByteStreamSplit_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int32, null,
            new LogicalType.Int(bitWidth: 32, isSigned: false));
        var values = new uint[] { 0, 1_000_000, uint.MaxValue / 2, uint.MaxValue };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<uint>(data, col));
    }

    // ──────────────── ByteStreamSplit for ulong (lines 1026-1033) ────────────────

    [Test]
    public void Read_ULong_ByteStreamSplit_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int64, null,
            new LogicalType.Int(bitWidth: 64, isSigned: false));
        var values = new ulong[] { 0, 1_000_000, ulong.MaxValue / 2 };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<ulong>(data, col));
    }

    // ──────────────── Optional columns with nulls (definition level paths) ────────────────

    [Test]
    public void Read_OptionalInt32_WithNulls_Plain()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.Plain]));
        using var ms = new MemoryStream();
        var schema = new ParquetSchema([col]);
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
        c.Serialize([1, null, 3, null, 5]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        Assert.That(ReadAllNullable<int>(ms.ToArray(), col), Is.EqualTo(new int?[] {1, null, 3, null, 5}));
    }

    [Test]
    public void Read_OptionalFloat_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Float,
            new ColumnOptions(ParquetRepetition.Optional));
        using var ms = new MemoryStream();
        var schema = new ParquetSchema([col]);
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<float?>(schema.Columns[0]);
        c.Serialize([1.5f, null, 3.5f]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        Assert.That(ReadAllNullable<float>(ms.ToArray(), col), Is.EqualTo(new float?[] {1.5f, null, 3.5f}));
    }

    [Test]
    public void Read_OptionalDouble_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Double,
            new ColumnOptions(ParquetRepetition.Optional));
        using var ms = new MemoryStream();
        var schema = new ParquetSchema([col]);
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<double?>(schema.Columns[0]);
        c.Serialize([1.5, null, null, 4.0]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        Assert.That(ReadAllNullable<double>(ms.ToArray(), col), Is.EqualTo(new double?[] {1.5, null, null, 4.0}));
    }

    [Test]
    public void Read_OptionalBool_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(ParquetRepetition.Optional));
        using var ms = new MemoryStream();
        var schema = new ParquetSchema([col]);
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<bool?>(schema.Columns[0]);
        c.Serialize([true, null, false, null, true]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        Assert.That(ReadAllNullable<bool>(ms.ToArray(), col), Is.EqualTo(new bool?[] {true, null, false, null, true}));
    }

    [Test]
    public void Read_OptionalLong_WithNulls()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(ParquetRepetition.Optional));
        using var ms = new MemoryStream();
        var schema = new ParquetSchema([col]);
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<long?>(schema.Columns[0]);
        c.Serialize([100L, null, -200L]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        Assert.That(ReadAllNullable<long>(ms.ToArray(), col), Is.EqualTo(new long?[] {100L, null, -200L}));
    }

    // ──────────────── Dictionary encoding with nulls ────────────────

    [Test]
    public void Read_DictionaryEncoding_WithNulls_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.RleDictionary]));
        using var ms = new MemoryStream();
        var schema = new ParquetSchema([col]);
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
        // Low cardinality with nulls → forces dictionary encoding with null expansion
        c.Serialize([1, null, 1, 2, null, 2, 1]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        ClassicAssert.AreEqual(new int?[] { 1, null, 1, 2, null, 2, 1 }, ReadAllNullable<int>(ms.ToArray(), col));
    }

    // ──────────────── DeltaBinaryPacked reading (decoder path) ────────────────

    [Test]
    public void Read_DeltaBinaryPacked_Int32_NonMonotonic()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]));
        var values = new int[] { 100, 50, 200, 10, 300 }; // non-monotonic
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<int>(data, col));
    }

    [Test]
    public void Read_DeltaBinaryPacked_Int64_NonMonotonic()
    {
        var col = new Column("v", ParquetPhysicalType.Int64,
            new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]));
        var values = new long[] { 1000L, 500L, 2000L, 100L };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<long>(data, col));
    }

    // ──────────────── DeltaByteArray reading ────────────────

    [Test]
    public void Read_DeltaByteArray_ByteArray_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(encodings: [EncodingKind.DeltaByteArray]));
        var values = new byte[][] {
            [1, 2, 3],
            [1, 2, 4],
            [2, 0],
            [5, 6, 7, 8]
        };
        var data = WriteAndClose(col, values);
        var result = ReadAll<byte[]>(data, col);
        ClassicAssert.AreEqual(values.Length, result.Length);
        for (var i = 0; i < values.Length; i++)
            ClassicAssert.AreEqual(values[i], result[i]);
    }

    // ──────────────── DeltaLengthByteArray reading ────────────────

    [Test]
    public void Read_DeltaLengthByteArray_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(encodings: [EncodingKind.DeltaLengthByteArray]));
        var values = new byte[][] {
            [10, 20],
            [30, 40, 50],
            [60]
        };
        var data = WriteAndClose(col, values);
        var result = ReadAll<byte[]>(data, col);
        ClassicAssert.AreEqual(values.Length, result.Length);
        for (var i = 0; i < values.Length; i++)
            ClassicAssert.AreEqual(values[i], result[i]);
    }

    // ──────────────── ByteStreamSplit float and double (already in roundtrip but here targeted) ────────────────

    [Test]
    public void Read_ByteStreamSplit_Float_Specific()
    {
        var col = new Column("v", ParquetPhysicalType.Float,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var values = new float[] { 0.0f, 1.0f, -1.0f, float.MaxValue / 2 };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<float>(data, col));
    }

    [Test]
    public void Read_ByteStreamSplit_Double_Specific()
    {
        var col = new Column("v", ParquetPhysicalType.Double,
            new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]));
        var values = new double[] { 0.0, 1.0, -1.0, double.MaxValue / 2 };
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<double>(data, col));
    }

    // ──────────────── Reader Reset path ────────────────

    [Test]
    public void Read_ResetReader_SecondReadGivesSameData()
    {
        var col = new Column("v", ParquetPhysicalType.Int32);
        var values = new int[] { 1, 2, 3, 4, 5 };
        var data = WriteAndClose(col, values);
        var schema = new ParquetSchema([col]);
        var src = new MemoryReadSource(data);
        using var reader = schema.CreateReader(src);

        var first = new List<int>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<int>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    first.Add(v);
        }

        // Reset and read again
        reader.Reset(src);
        var second = new List<int>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<int>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    second.Add(v);
        }

        ClassicAssert.AreEqual(values, first.ToArray());
        ClassicAssert.AreEqual(values, second.ToArray());
    }

    // ──────────────── Read from Stream source (exercises FileReadSource/StreamReadSource paths) ────────────────

    [Test]
    public void Read_FromStream_SameAsMemory()
    {
        var col = new Column("v", ParquetPhysicalType.Int32);
        var values = new int[] { 10, 20, 30 };
        var data = WriteAndClose(col, values);
        var schema = new ParquetSchema([col]);

        // Read from MemoryStream (StreamReadSource path)
        using var ms = new MemoryStream(data);
        using var reader = schema.CreateReader(ms);
        var results = new List<int>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(ms, tok);
            foreach (var page in rg.Column<int>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        ClassicAssert.AreEqual(values, results.ToArray());
    }

    // ──────────────── Multiple encodings in one read session ────────────────

    [Test]
    public void Read_LargeDataset_DeltaBinaryPacked()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]));
        var values = Enumerable.Range(0, 500).ToArray();
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<int>(data, col));
    }

    [Test]
    public void Read_LargeDataset_RleDictionary()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]));
        // Low cardinality: 5 values repeated many times
        var values = Enumerable.Range(0, 500).Select(i => i % 5).ToArray();
        var data = WriteAndClose(col, values);
        ClassicAssert.AreEqual(values, ReadAll<int>(data, col));
    }
}
