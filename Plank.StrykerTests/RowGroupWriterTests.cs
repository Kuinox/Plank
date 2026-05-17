using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.StrykerTests;

/// <summary>
/// Tests for RowGroupWriter via the public writer API.
/// Targets surviving mutants in row count validation, column ordering, and null count tracking.
/// </summary>
public class RowGroupWriterTests
{
    static ParquetSchema Schema(params Column[] cols)
        => new(ImmutableArray.Create(cols));

    static byte[] WriteAndClose(ParquetSchema schema, Action<ParquetWriter, RowGroupWriter> write,
        CompressionKind compression = CompressionKind.None)
    {
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = compression });
        var rg = writer.StartRowGroup();
        write(writer, rg);
        writer.CloseFile();
        return ms.ToArray();
    }

    static T[] ReadAll<T>(byte[] data, ParquetSchema schema, int colIndex = 0)
    {
        var src = new MemoryReadSource(data);
        using var reader = schema.CreateReader(src);
        var results = new List<T>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<T>(schema.Columns[colIndex]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        return results.ToArray();
    }

    // ──────────────── Row count validation ────────────────

    [Fact]
    public void Write_TwoColumnsWithSameRowCount_Succeeds()
    {
        var schema = Schema(
            new Column("a", ParquetPhysicalType.Int32),
            new Column("b", ParquetPhysicalType.Int32));

        var data = WriteAndClose(schema, (w, rg) =>
        {
            var a = w.CreateSerializedColumn<int>(schema.Columns[0]);
            var b = w.CreateSerializedColumn<int>(schema.Columns[1]);
            a.Serialize([1, 2, 3]);
            b.Serialize([10, 20, 30]);
            rg.Write(a);
            rg.Write(b);
        });

        var aVals = ReadAll<int>(data, schema, 0);
        var bVals = ReadAll<int>(data, schema, 1);
        Assert.Equal([1, 2, 3], aVals);
        Assert.Equal([10, 20, 30], bVals);
    }

    [Fact]
    public void Write_RowCountMismatch_Throws()
    {
        var schema = Schema(
            new Column("a", ParquetPhysicalType.Int32),
            new Column("b", ParquetPhysicalType.Int32));

        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms);
        var rg = writer.StartRowGroup();
        var a = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        var b = writer.CreateSerializedColumn<int>(schema.Columns[1]);
        a.Serialize([1, 2, 3]);
        b.Serialize([10, 20]); // different count!
        rg.Write(a);
        Assert.Throws<InvalidOperationException>(() => rg.Write(b));
    }

    [Fact]
    public void Write_ZeroRows_Succeeds()
    {
        var schema = Schema(new Column("v", ParquetPhysicalType.Int32));
        var data = WriteAndClose(schema, (w, rg) =>
        {
            var c = w.CreateSerializedColumn<int>(schema.Columns[0]);
            c.Serialize([]);
            rg.Write(c);
        });
        var vals = ReadAll<int>(data, schema);
        Assert.Empty(vals);
    }

    [Fact]
    public void Write_SingleRow_Succeeds()
    {
        var schema = Schema(new Column("v", ParquetPhysicalType.Int32));
        var data = WriteAndClose(schema, (w, rg) =>
        {
            var c = w.CreateSerializedColumn<int>(schema.Columns[0]);
            c.Serialize([42]);
            rg.Write(c);
        });
        Assert.Equal([42], ReadAll<int>(data, schema));
    }

    // ──────────────── Column ordering ────────────────

    [Fact]
    public void Write_WrongColumnOrder_Throws()
    {
        var schema = Schema(
            new Column("a", ParquetPhysicalType.Int32),
            new Column("b", ParquetPhysicalType.Float));

        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms);
        var rg = writer.StartRowGroup();
        var a = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        var b = writer.CreateSerializedColumn<float>(schema.Columns[1]);
        a.Serialize([1]);
        b.Serialize([1.0f]);
        // Write b first — wrong order
        Assert.Throws<InvalidOperationException>(() => rg.Write(b));
    }

    // ──────────────── Null count tracking ────────────────

    [Fact]
    public void Write_OptionalColumn_NullCountCorrect()
    {
        var schema = Schema(
            new Column("v", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional)));

        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms);
        var rg = writer.StartRowGroup();
        var c = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
        c.Serialize([1, null, 3, null, null]);
        rg.Write(c);
        writer.CloseFile();

        // Read back and verify nulls are preserved
        var src = new MemoryReadSource(ms.ToArray());
        using var reader = schema.CreateReader(src);
        var results = new List<int?>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg2 = reader.OpenRowGroup(src, tok);
            foreach (var page in rg2.Column<int?>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        Assert.Equal([1, null, 3, null, null], results);
    }

    // ──────────────── Multiple row groups ────────────────

    [Fact]
    public void TwoRowGroups_BothPreserved()
    {
        var schema = Schema(new Column("v", ParquetPhysicalType.Int32));
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });

        var rg1 = writer.StartRowGroup();
        var c1 = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        c1.Serialize([1, 2, 3]);
        rg1.Write(c1);

        var rg2 = writer.StartRowGroup();
        var c2 = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        c2.Serialize([4, 5]);
        rg2.Write(c2);

        writer.CloseFile();

        Assert.Equal([1, 2, 3, 4, 5], ReadAll<int>(ms.ToArray(), schema));
    }

    // ──────────────── Compression paths ────────────────

    [Fact]
    public void Write_WithSnappyCompression_RoundTrips()
    {
        var schema = Schema(new Column("v", ParquetPhysicalType.Int32));
        var values = Enumerable.Range(0, 100).ToArray();
        var data = WriteAndClose(schema, (w, rg) =>
        {
            var c = w.CreateSerializedColumn<int>(schema.Columns[0]);
            c.Serialize(values);
            rg.Write(c);
        }, CompressionKind.Snappy);
        Assert.Equal(values, ReadAll<int>(data, schema));
    }

    // ──────────────── CreateSerializedColumn via RowGroupWriter ────────────────

    [Fact]
    public void CreateSerializedColumn_ViaRowGroupWriter_Works()
    {
        var schema = Schema(new Column("v", ParquetPhysicalType.Boolean));
        var data = WriteAndClose(schema, (_, rg) =>
        {
            var c = rg.CreateSerializedColumn<bool>(schema.Columns[0]);
            c.Serialize([true, false, true]);
            rg.Write(c);
        });
        Assert.Equal([true, false, true], ReadAll<bool>(data, schema));
    }

    // ──────────────── Write without prior Serialize ────────────────

    [Fact]
    public void Write_WithoutSerialize_Throws()
    {
        var schema = Schema(new Column("v", ParquetPhysicalType.Int32));
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms);
        var rg = writer.StartRowGroup();
        var c = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        // Don't call c.Serialize() — Write should throw
        Assert.Throws<InvalidOperationException>(() => rg.Write(c));
    }

    // ──────────────── Large dataset (exercises checked arithmetic in RowGroupWriter) ────────────────

    [Fact]
    public void Write_LargeDataset_RowCountMatchesWritten()
    {
        var schema = Schema(new Column("v", ParquetPhysicalType.Int32));
        var values = Enumerable.Range(0, 10_000).ToArray();
        var data = WriteAndClose(schema, (w, rg) =>
        {
            var c = w.CreateSerializedColumn<int>(schema.Columns[0]);
            c.Serialize(values);
            rg.Write(c);
        });
        var result = ReadAll<int>(data, schema);
        Assert.Equal(10_000, result.Length);
        Assert.Equal(values, result);
    }
}
