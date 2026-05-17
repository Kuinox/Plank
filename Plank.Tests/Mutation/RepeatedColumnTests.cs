using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;

namespace Plank.Tests.Mutation;

/// <summary>
/// Tests for repeated/list columns — covers NoCoverage in Encoding.cs:
/// - EncodeRepeatedRows for bool[]?, int?[], long?[], float?[], double?[] (lines 902-957)
/// - Also covers EncodeOptional paths for various types (line 607 dictionary)
/// </summary>
public class RepeatedColumnTests
{
    // NOTE: The reader does not yet support repeated/list columns for readback.
    // These tests verify that the WRITER encodes correctly without throwing.
    // The encoding covers NoCoverage paths in Encoding.cs EncodeRepeatedRows.

    static byte[] WriteListColumn<T>(ParquetSchema schema, T[] values) where T : notnull
    {
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<T>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        return ms.ToArray();
    }

    // ──────────────── bool[] list (line 895-900) ────────────────

    [Test]
    public void List_BoolArray_WritesWithoutThrowing()
    {
        var schema = new ParquetSchema([ColumnDef.List("v",
            ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Boolean))]);
        var data = WriteListColumn<bool[]>(schema,
            new bool[][] { [true, false, true], [], [false, false] });
        ClassicAssert.IsTrue(data.Length > 0);
    }

    // ──────────────── int[] list (line 910-921) ────────────────

    [Test]
    public void List_IntArray_WritesWithoutThrowing()
    {
        var schema = new ParquetSchema([ColumnDef.List("v",
            ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32))]);
        var data = WriteListColumn<int[]>(schema,
            new int[][] { [1, 2, 3], [], [4, 5] });
        ClassicAssert.IsTrue(data.Length > 0);
    }

    // ──────────────── int?[] list with nullable elements (line 916-921) ────────────────

    [Test]
    public void List_NullableIntArray_WritesWithoutThrowing()
    {
        var schema = new ParquetSchema([ColumnDef.List("v",
            ColumnDef.OptionalLeaf("element", ParquetPhysicalType.Int32))]);
        var data = WriteListColumn<int?[]>(schema,
            new int?[][] { [1, null, 3], [], [null, 5] });
        ClassicAssert.IsTrue(data.Length > 0);
    }

    // ──────────────── long[] list (line 923-935) ────────────────

    [Test]
    public void List_LongArray_WritesWithoutThrowing()
    {
        var schema = new ParquetSchema([ColumnDef.List("v",
            ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int64))]);
        var data = WriteListColumn<long[]>(schema,
            new long[][] { [100L, 200L], [300L] });
        ClassicAssert.IsTrue(data.Length > 0);
    }

    // ──────────────── float[] list (line 938-949) ────────────────

    [Test]
    public void List_FloatArray_WritesWithoutThrowing()
    {
        var schema = new ParquetSchema([ColumnDef.List("v",
            ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Float))]);
        var data = WriteListColumn<float[]>(schema,
            new float[][] { [1.5f, 2.5f], [3.5f] });
        ClassicAssert.IsTrue(data.Length > 0);
    }

    // ──────────────── float?[] list with nullable elements (line 944-949) ────────────────

    [Test]
    public void List_NullableFloatArray_WritesWithoutThrowing()
    {
        var schema = new ParquetSchema([ColumnDef.List("v",
            ColumnDef.OptionalLeaf("element", ParquetPhysicalType.Float))]);
        var data = WriteListColumn<float?[]>(schema,
            new float?[][] { [1.5f, null, 3.5f] });
        ClassicAssert.IsTrue(data.Length > 0);
    }

    // ──────────────── double?[] list (line 957+) ────────────────

    [Test]
    public void List_NullableDoubleArray_WritesWithoutThrowing()
    {
        var schema = new ParquetSchema([ColumnDef.List("v",
            ColumnDef.OptionalLeaf("element", ParquetPhysicalType.Double))]);
        var data = WriteListColumn<double?[]>(schema,
            new double?[][] { [1.5, null, 3.5], [null] });
        ClassicAssert.IsTrue(data.Length > 0);
    }

    // ──────────────── Optional dictionary (line 607 in Encoding.cs) ────────────────

    [Test]
    public void OptionalDictionary_LowCardinality_Roundtrip()
    {
        // Low cardinality optional → triggers dictionary encoding in EncodeOptional
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.RleDictionary]))]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
        col.Serialize([1, null, 1, 2, null, 2, 1, null]);  // 2 unique + nulls
        writer.StartRowGroup().Write(col);
        writer.CloseFile();

        var src = new MemoryReadSource(ms.ToArray());
        using var reader = schema.CreateReader(src);
        var results = new List<int?>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<int?>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        ClassicAssert.AreEqual(new int?[] { 1, null, 1, 2, null, 2, 1, null }, results.ToArray());
    }

    [Test]
    public void OptionalDictionary_Float_Roundtrip()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Float,
            new ColumnOptions(ParquetRepetition.Optional, [EncodingKind.RleDictionary]))]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<float?>(schema.Columns[0]);
        col.Serialize([1.5f, null, 1.5f, 2.5f, null]);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();

        var src = new MemoryReadSource(ms.ToArray());
        using var reader = schema.CreateReader(src);
        var results = new List<float?>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<float?>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        ClassicAssert.AreEqual(new float?[] { 1.5f, null, 1.5f, 2.5f, null }, results.ToArray());
    }
}
