using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;

namespace Plank.Tests.Mutation;

/// <summary>
/// Tests that kill surviving mutants at exact boundary conditions in Encoding.cs:
/// - Line 155: pageBytes + rowBytes > targetPageBytes (exact == case distinguishes > from >=)
/// - Line 135/100: WriteFixedRowsDataPages boundary (rowsPerPage exact multiples)
/// Verifies exact PAGE COUNT, not just value roundtrip.
/// </summary>
public class EncodingPageBoundaryTests
{
    static int CountDataPages<T>(SerializedColumn<T> col) where T : notnull
    {
        var count = 0;
        for (var i = 0; i < col.Pages.Count; i++)
            if (col.Pages[i].Kind == PageKind.DataV2)
                count++;
        return count;
    }

    static SerializedColumn<T> WriteColumnWithPageSizer<T>(Column col, T[] values, uint targetBytes)
        where T : notnull
    {
        var schema = new ParquetSchema([col]);
        var schemWithStrategy = schema with {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(col.Name, new TargetByteSizeStrategy(targetBytes))
        };
        using var ms = new MemoryStream();
        var writer = schemWithStrategy.CreateWriter(ms, new ParquetWriterOptions { Compression = CompressionKind.None });
        var c = writer.CreateSerializedColumn<T>(schemWithStrategy.Columns[0]);
        c.Serialize(values);
        writer.StartRowGroup().Write(c);
        writer.CloseFile();
        return c;
    }

    // ──────────────── line 155: pageBytes + rowBytes > targetPageBytes ────────────────

    [Test]
    public void String_ExactByteBoundary_BothFitInOnePage()
    {
        // "hello" = 4 + 5 = 9 bytes; targetPageBytes = 18
        // After first "hello": pageBytes=9; second "hello" adds 9 → 18 total
        // 18 > 18 is FALSE → both stay in one page
        // If mutated to >=: 18 >= 18 is TRUE → breaks → 2 pages
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(encodings: [EncodingKind.Plain]), new LogicalType.String());
        var c = WriteColumnWithPageSizer(col, new string[] { "hello", "hello" }, targetBytes: 18);
        ClassicAssert.AreEqual(1, CountDataPages(c));
    }

    [Test]
    public void String_OneByteBeyondBoundary_SplitsIntoTwoPages()
    {
        // "hello" = 9 bytes; two values total 18 bytes; targetPageBytes = 17
        // After first: pageBytes=9; second: 9+9=18 > 17 → break → 2 pages
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(encodings: [EncodingKind.Plain]), new LogicalType.String());
        var c = WriteColumnWithPageSizer(col, new string[] { "hello", "hello" }, targetBytes: 17);
        ClassicAssert.AreEqual(2, CountDataPages(c));
    }

    [Test]
    public void ByteArray_ExactByteBoundary_FitsInOnePage()
    {
        // byte[] [1,2,3] = 4 + 3 = 7 bytes; two values = 14; targetPageBytes = 14
        // 14 > 14 is FALSE → both in one page
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(encodings: [EncodingKind.Plain]));
        var vals = new byte[][] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } };
        var c = WriteColumnWithPageSizer(col, vals, targetBytes: 14);
        ClassicAssert.AreEqual(1, CountDataPages(c));
    }

    [Test]
    public void ByteArray_TinyTarget_EachValueOnOwnPage()
    {
        // targetPageBytes = 1 → each 7-byte value gets its own page
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(encodings: [EncodingKind.Plain]));
        var vals = new byte[][] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 }, new byte[] { 7 } };
        var c = WriteColumnWithPageSizer(col, vals, targetBytes: 1);
        ClassicAssert.AreEqual(3, CountDataPages(c));
    }

    [Test]
    public void ByteArray_FirstRowExceedsTarget_StillOnSamePage()
    {
        // When the first row on an empty page exceeds targetPageBytes, it still goes on that page
        // (the condition pageRowCount > 0 prevents breaking before the first row)
        var col = new Column("v", ParquetPhysicalType.ByteArray,
            new ColumnOptions(encodings: [EncodingKind.Plain]));
        var large = new byte[100];
        for (var i = 0; i < 100; i++) large[i] = (byte)i;
        // 100-byte array + 4 byte length = 104 bytes, targetPageBytes = 10
        var c = WriteColumnWithPageSizer(col, new byte[][] { large }, targetBytes: 10);
        // Single large value still gets a page (not skipped)
        ClassicAssert.AreEqual(1, CountDataPages(c));
        ClassicAssert.AreEqual(1u, c.RowCount);
    }

    // ──────────────── line 135: WriteFixedRowsDataPages exact multiples ────────────────

    [Test]
    public void Int32_FixedRows_ExactMultipleOfRowsPerPage_TwoPages()
    {
        // rowsPerPage = targetPageBytes / sizeof(int) = 16/4 = 4 rows per page
        // 8 values → exactly 2 pages of 4 rows each
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]));
        var c = WriteColumnWithPageSizer(col, Enumerable.Range(1, 8).ToArray(), targetBytes: 16);
        ClassicAssert.AreEqual(2, CountDataPages(c));
    }

    [Test]
    public void Int32_FixedRows_NonMultiple_RoundUpToExtraSmallerPage()
    {
        // 9 values with 4 per page → pages of [4, 4, 1]
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]));
        var c = WriteColumnWithPageSizer(col, Enumerable.Range(1, 9).ToArray(), targetBytes: 16);
        ClassicAssert.AreEqual(3, CountDataPages(c));
    }

    [Test]
    public void Int32_FixedRows_SinglePage_AllFit()
    {
        // 3 values × 4 bytes = 12 bytes; targetPageBytes = 100 → 25 rows per page; all fit
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]));
        var c = WriteColumnWithPageSizer(col, new int[] { 1, 2, 3 }, targetBytes: 100);
        ClassicAssert.AreEqual(1, CountDataPages(c));
    }

    // ──────────────── line 100: WriteFixedRowsDataPages uses Math.Max(1, ...) ────────────────

    [Test]
    public void Int32_TargetSmallerThanOneRow_StillGetsAtLeastOneRowPerPage()
    {
        // Even if targetPageBytes < sizeof(int)=4, we get Math.Max(1, ...) = 1 row per page
        var col = new Column("v", ParquetPhysicalType.Int32,
            new ColumnOptions(encodings: [EncodingKind.Plain]));
        var c = WriteColumnWithPageSizer(col, new int[] { 1, 2, 3 }, targetBytes: 1);
        // Math.Max(1, 1/4) = Math.Max(1, 0) = 1 → 3 pages (1 row each)
        ClassicAssert.AreEqual(3, CountDataPages(c));
    }

    sealed class TargetByteSizeStrategy : IPageStrategy
    {
        readonly uint _bytes;
        internal TargetByteSizeStrategy(uint bytes) => _bytes = bytes;
        public DictionaryMode GetDictionaryMode() => DictionaryMode.Disabled;
        public DictionarySortOrder GetDictionarySortOrder() => DictionarySortOrder.Unknown;
        public void SetDictionarySortOrder(DictionarySortOrder s) { }
        public bool ShouldDropDictionary(uint u, uint t, uint r) => false;
        public bool TryGetTargetDataPageSizeBytes(out uint sizeBytes) { sizeBytes = _bytes; return true; }
        public bool ShouldStartNewDataPage(uint t, uint w, uint p) => false;
    }
}
