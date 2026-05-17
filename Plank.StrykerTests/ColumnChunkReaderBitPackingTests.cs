using System.Collections.Immutable;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.StrykerTests;

/// <summary>
/// Tests targeting ColumnChunkReader.cs multi-byte bit-packing reads (lines 1578-1583):
/// - ReadBitPackedValue needs to read 2-4 bytes when values span byte boundaries
/// - Exercises with 3-bit (5-7 unique values) dictionary bit-packing
/// - Exercises with 9-bit and 10-bit dictionary indexes
/// Also targets line 94: needsNullExpansion for nullable reference types
/// and line 103: non-nullable type with actual nulls
/// </summary>
public class ColumnChunkReaderBitPackingTests
{
    static T[] WriteAndRead<T>(Column col, T[] values)
    {
        var schema = new ParquetSchema([col]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<T>(schema.Columns[0]);
        c.Serialize(values);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();

        var src = new MemoryReadSource(ms.ToArray());
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

    // ──────────────── 3-bit dictionary (5 unique values) ────────────────

    [Fact]
    public void Dictionary_3BitIndexes_MultiBytePacking()
    {
        // 5 unique values → GetBitWidthFromMaxValue(4) = 3 bits
        // 8+ values in literal group → index at position 2 spans bytes 0 and 1 (line 1579)
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]));
        var values = new int[] { 1, 2, 3, 4, 5, 1, 2, 3, 4, 5 }; // 5 unique, 10 total
        Assert.Equal(values, WriteAndRead(col, values));
    }

    [Fact]
    public void Dictionary_3BitIndexes_ByteAligned_7Values()
    {
        // 7 unique values → 3 bits
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]));
        var values = new int[] { 1, 2, 3, 4, 5, 6, 7, 1, 2, 3, 4, 5, 6, 7, 1 };
        Assert.Equal(values, WriteAndRead(col, values));
    }

    // ──────────────── 8-bit dictionary (256 unique values) ────────────────

    [Fact]
    public void Dictionary_8BitIndexes_ManyUniqueValues()
    {
        // 256 unique values → 8 bits (all byte-aligned)
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]));
        var values = Enumerable.Range(0, 256).ToArray(); // 256 unique → 8-bit indexes
        Assert.Equal(values, WriteAndRead(col, values));
    }

    // ──────────────── 9-bit dictionary (257-511 unique values) ────────────────

    [Fact]
    public void Dictionary_9BitIndexes_CrossByteBoundary()
    {
        // 300 unique values → GetBitWidthFromMaxValue(299) = 9 bits
        // Values at positions 7+ would cross byte boundaries
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]));
        // 300 distinct values
        var values = Enumerable.Range(0, 300).ToArray();
        Assert.Equal(values, WriteAndRead(col, values));
    }

    // ──────────────── Mixed: run + literal in same column ────────────────

    [Fact]
    public void DictBitPacking_RunThenLiterals_FullRoundTrip()
    {
        // 8 of value "A", then 8 distinct values — forces both RLE run and literal group
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]));
        var values = new int[] { 999, 999, 999, 999, 999, 999, 999, 999, 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert.Equal(values, WriteAndRead(col, values));
    }

    // ──────────────── Nullable reference type (string) null expansion (line 94) ────────────────

    [Fact]
    public void OptionalString_WithNulls_NullExpansion()
    {
        // Nullable reference type (string) with actual nulls exercises needsNullExpansion path
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(ParquetRepetition.Optional), new LogicalType.String());
        var schema = new ParquetSchema([col]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<string>(schema.Columns[0]);
        c.Serialize(["hello", null!, "world", null!, "test"]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();

        var src = new MemoryReadSource(ms.ToArray());
        using var reader = schema.CreateReader(src);
        var results = new List<string?>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<string>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        Assert.Equal(["hello", null, "world", null, "test"], results);
    }

    [Fact]
    public void OptionalString_WithoutNulls_NoExpansion()
    {
        // Nullable reference type (string) without nulls — exercises the non-null code path
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(ParquetRepetition.Optional), new LogicalType.String());
        var schema = new ParquetSchema([col]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<string>(schema.Columns[0]);
        c.Serialize(["a", "b", "c"]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();

        var src = new MemoryReadSource(ms.ToArray());
        using var reader = schema.CreateReader(src);
        var results = new List<string?>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<string>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        Assert.Equal(["a", "b", "c"], results);
    }

    // ──────────────── ByteArray (byte[]) with nulls ────────────────

    [Fact]
    public void OptionalByteArray_WithNulls_NullExpansion()
    {
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(ParquetRepetition.Optional));
        var schema = new ParquetSchema([col]);
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<byte[]>(schema.Columns[0]);
        c.Serialize([new byte[] { 1, 2 }, null!, new byte[] { 3 }]);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();

        var src = new MemoryReadSource(ms.ToArray());
        using var reader = schema.CreateReader(src);
        var results = new List<byte[]?>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<byte[]>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        Assert.Equal(3, results.Count);
        Assert.Equal(new byte[] { 1, 2 }, results[0]);
        Assert.Null(results[1]);
        Assert.Equal(new byte[] { 3 }, results[2]);
    }

    // ──────────────── Multi-page with specific page strategy ────────────────

    [Fact]
    public void Dictionary_MultiPage_10RowsPerPage_RoundTrip()
    {
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.RleDictionary]));
        var schema = new ParquetSchema([col]) {
            PageStrategiesByColumnName = System.Collections.Immutable.ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(col.Name, new FixedRowsPageStrategy(10))
        };
        // 5 unique values × 50 rows = 5 pages
        var values = Enumerable.Range(0, 50).Select(i => i % 5).ToArray();
        using var ms = new MemoryStream();
        var writer = schema.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<int>(schema.Columns[0]);
        c.Serialize(values);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();

        var src = new MemoryReadSource(ms.ToArray());
        var readSchema = new ParquetSchema([col]);
        using var reader = readSchema.CreateReader(src);
        var results = new List<int>();
        foreach (var tok in reader.EnumerateRowGroups())
        {
            using var rg = reader.OpenRowGroup(src, tok);
            foreach (var page in rg.Column<int>(schema.Columns[0]).Pages)
                foreach (var v in page.Values.Span)
                    results.Add(v);
        }
        Assert.Equal(values, results.ToArray());
    }

    sealed class FixedRowsPageStrategy : IPageStrategy
    {
        readonly int _rows;
        internal FixedRowsPageStrategy(int rows) => _rows = rows;
        public DictionaryMode GetDictionaryMode() => DictionaryMode.Maybe;
        public DictionarySortOrder GetDictionarySortOrder() => DictionarySortOrder.Unknown;
        public void SetDictionarySortOrder(DictionarySortOrder s) { }
        public bool ShouldDropDictionary(uint u, uint t, uint r) => false;
        public bool TryGetTargetDataPageSizeBytes(out uint s) { s = 0; return false; }
        public bool ShouldStartNewDataPage(uint t, uint w, uint p) => p >= (uint)_rows;
    }
}
