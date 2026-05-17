using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.StrykerTests;

/// <summary>
/// Tests that specifically exercise multi-page writing and page-splitting behavior
/// in Encoding.cs (lines 155, 230 — page boundary checks with surviving mutants).
/// </summary>
public class EncodingMultiPageTests
{
    static byte[] WriteWithPageStrategy(ParquetSchema schema, int[] values, IPageStrategy strategy)
    {
        using var ms = new MemoryStream();
        var schemWithStrategy = schema with
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(schema.Columns[0].Name, strategy)
        };
        var writer = schemWithStrategy.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int>(schemWithStrategy.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        return ms.ToArray();
    }

    static T[] ReadAll<T>(byte[] data, ParquetSchema schema)
    {
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

    // ──────────────── Fixed-rows page strategy ────────────────

    [Test]
    public void Int32_Plain_MultiPage_TwoRowsPerPage()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]))]);
        var values = new int[] { 1, 2, 3, 4, 5 };
        var data = WriteWithPageStrategy(schema, values, new FixedRowsStrategy(2));
        // Must round-trip correctly across multiple pages
        ClassicAssert.AreEqual(values, ReadAll<int>(data, schema));
    }

    [Test]
    public void Int32_Plain_MultiPage_OneRowPerPage()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]))]);
        var values = Enumerable.Range(0, 10).ToArray();
        var data = WriteWithPageStrategy(schema, values, new FixedRowsStrategy(1));
        ClassicAssert.AreEqual(values, ReadAll<int>(data, schema));
    }

    [Test]
    public void Int32_Plain_MultiPage_ExactBoundary()
    {
        // 6 rows, 2 per page → exactly 3 pages
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]))]);
        var values = new int[] { 10, 20, 30, 40, 50, 60 };
        var data = WriteWithPageStrategy(schema, values, new FixedRowsStrategy(2));
        ClassicAssert.AreEqual(values, ReadAll<int>(data, schema));
    }

    [Test]
    public void Int32_Plain_MultiPage_LastPageHasRemainder()
    {
        // 7 rows, 3 per page → pages of [3,3,1]
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]))]);
        var values = new int[] { 1, 2, 3, 4, 5, 6, 7 };
        var data = WriteWithPageStrategy(schema, values, new FixedRowsStrategy(3));
        ClassicAssert.AreEqual(values, ReadAll<int>(data, schema));
    }

    // ──────────────── Dictionary encoding with multi-page ────────────────

    [Test]
    public void Int32_Dictionary_MultiPage_LowCardinality()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]))]);
        // Low cardinality: 3 distinct values, 300 total → should use dictionary
        var values = Enumerable.Range(0, 300).Select(i => i % 3).ToArray();
        var data = WriteWithPageStrategy(schema, values, new FixedRowsStrategy(100));
        ClassicAssert.AreEqual(values, ReadAll<int>(data, schema));
    }

    [Test]
    public void Int32_Dictionary_SinglePage()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]))]);
        var values = Enumerable.Range(0, 50).Select(i => i % 5).ToArray();
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();
        ClassicAssert.AreEqual(values, ReadAll<int>(ms.ToArray(), schema));
    }

    // ──────────────── DeltaBinaryPacked multi-page ────────────────

    [Test]
    public void Int32_Delta_MultiPage_MonotonicValues()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.DeltaBinaryPacked]))]);
        var values = Enumerable.Range(0, 300).ToArray();
        var data = WriteWithPageStrategy(schema, values, new FixedRowsStrategy(100));
        ClassicAssert.AreEqual(values, ReadAll<int>(data, schema));
    }

    // ──────────────── Boolean RLE multi-page ────────────────

    [Test]
    public void Boolean_Rle_MultiPage()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Boolean,
            new ColumnOptions(encodings: [EncodingKind.Rle]))]);
        var pattern = new bool[] { true, false, true, false };
        var values = Enumerable.Range(0, 200).Select(i => pattern[i % 4]).ToArray();

        using var ms = new MemoryStream();
        var schemWithStrategy = schema with
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(schema.Columns[0].Name, new FixedRowsStrategy(50))
        };
        var writer = schemWithStrategy.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<bool>(schemWithStrategy.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();

        var result = ReadAll<bool>(ms.ToArray(), schema);
        ClassicAssert.AreEqual(values, result);
    }

    // ──────────────── Page index recording ────────────────

    [Test]
    public void Int32_WithPageIndexes_MultiPage_RoundTrips()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]))]);
        var values = Enumerable.Range(1, 100).ToArray();

        using var ms = new MemoryStream();
        var schemWithStrategy = schema with
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(schema.Columns[0].Name, new FixedRowsStrategy(10))
        };
        var writer = schemWithStrategy.CreateWriter(ms, new ParquetWriterOptions
        {
            Compression = CompressionKind.None,
            WritePageIndexes = true
        });
        var col = writer.CreateSerializedColumn<int>(schemWithStrategy.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();

        ClassicAssert.AreEqual(values, ReadAll<int>(ms.ToArray(), schema));
    }

    // ──────────────── Variable-width (ByteArray) multi-page ────────────────

    [Test]
    public void ByteArray_Plain_MultiPage_RoundTrips()
    {
        var schema = new ParquetSchema([new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(encodings: [EncodingKind.Plain]))]);
        var values = Enumerable.Range(0, 50)
            .Select(i => new byte[] { (byte)(i & 0xFF), (byte)((i >> 1) & 0xFF) })
            .ToArray();

        using var ms = new MemoryStream();
        var schemWithStrategy = schema with
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(schema.Columns[0].Name, new FixedRowsStrategy(10))
        };
        var writer = schemWithStrategy.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var col = writer.CreateSerializedColumn<byte[]>(schemWithStrategy.Columns[0]);
        col.Serialize(values);
        writer.StartRowGroup().Write(col);
        writer.CloseFile();

        var src = new MemoryReadSource(ms.ToArray());
        using var reader = schema.CreateReader(src);
        var results = new List<byte[]>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<byte[]>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        ClassicAssert.AreEqual(values.Length, results.Count);
        for (var i = 0; i < values.Length; i++)
            ClassicAssert.AreEqual(values[i], results[i]);
    }

    sealed class FixedRowsStrategy : IPageStrategy
    {
        readonly int _rowsPerPage;

        internal FixedRowsStrategy(int rowsPerPage) => _rowsPerPage = rowsPerPage;

        public DictionaryMode GetDictionaryMode() => DictionaryMode.Maybe;
        public DictionarySortOrder GetDictionarySortOrder() => DictionarySortOrder.Unknown;
        public void SetDictionarySortOrder(DictionarySortOrder sortOrder) { }
        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen) => false;
        public bool TryGetTargetDataPageSizeBytes(out uint sizeBytes) { sizeBytes = 0; return false; }
        public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
            => currentPageRowCount >= (uint)_rowsPerPage;
    }
}
