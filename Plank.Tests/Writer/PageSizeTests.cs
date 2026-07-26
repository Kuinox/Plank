using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.Writer;

internal sealed class PageSizeTests
{
    [Test]
    public void RequiredFixedWidthColumnSplitsByTargetPageSize()
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("id", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 8
        });
        var idColumn = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);

        idColumn.Serialize([1, 2, 3, 4, 5]);

        AssertDataPageRows(idColumn.Pages, [2, 2, 1]);
    }

    [Test]
    public void RequiredVariableWidthColumnSplitsByTargetPageSize()
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 32
        });
        var nameColumn = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);

        nameColumn.Serialize([
            "abcdefghij"u8.ToArray(),
            "klmnopqrst"u8.ToArray(),
            "uvwxyzabcd"u8.ToArray(),
            "efghijklmn"u8.ToArray(),
            "opqrstuvwx"u8.ToArray()
        ]);

        AssertDataPageRows(nameColumn.Pages, [2, 2, 1]);
    }

    [Test]
    public void OptionalColumnSplitsByTargetPageSize()
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 9
        });
        var idColumn = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);

        idColumn.Serialize([1, 2, null, 3]);

        AssertDataPageRows(idColumn.Pages, [1, 2, 1]);
    }

    [Test]
    public void DictionaryColumnSplitsDataPagesByTargetPageSize()
    {
        var schema = new PlankParquetSchema([
            Plank.Schema.ColumnDefinition.Leaf("name", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 4
        });
        var nameColumn = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        var values = new byte[100][];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 2 == 0 ? "a"u8.ToArray() : "b"u8.ToArray();

        nameColumn.Serialize(values);

        AssertDictionaryPageCount(nameColumn.Pages, 1);
        AssertDataPageCountGreaterThan(nameColumn.Pages, 1);
    }

    [Test]
    public void NestedLeafUsesStrategyFromDefinition()
    {
        var strategy = new FixedRowsPageStrategy(2);
        var schema = new PlankParquetSchema([
            ColumnDefinition.RequiredGroup("group",
                ColumnDefinition.RequiredLeaf("value", ParquetPhysicalType.Int32, pageStrategy: strategy))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream);
        var column = writer.CreateSerializedColumn<int>(schema.LeafColumns[0]);

        column.Serialize([1, 2, 3, 4, 5]);

        if (!ReferenceEquals(schema.LeafColumns[0].PageStrategy, strategy))
            throw new InvalidOperationException("The projected leaf did not retain its declared page strategy.");
        AssertDataPageRows(column.Pages, [2, 2, 1]);
    }

    static void AssertDataPageRows(PageList pages, ReadOnlySpan<int> expectedRows)
    {
        var dataPageIndex = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            ref var page = ref pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;

            if (dataPageIndex >= expectedRows.Length)
                throw new InvalidOperationException($"Unexpected data page {dataPageIndex}.");
            if (page.RowCount != (uint)expectedRows[dataPageIndex])
                throw new InvalidOperationException(
                    $"Page {dataPageIndex} row count mismatch. Expected {expectedRows[dataPageIndex]}, got {page.RowCount}.");
            dataPageIndex++;
        }

        if (dataPageIndex != expectedRows.Length)
            throw new InvalidOperationException(
                $"Data page count mismatch. Expected {expectedRows.Length}, got {dataPageIndex}.");
    }

    static void AssertDictionaryPageCount(PageList pages, int expectedCount)
    {
        var count = 0;
        for (var i = 0; i < pages.Count; i++)
            if (pages[i].Kind == PageKind.Dictionary)
                count++;

        if (count != expectedCount)
            throw new InvalidOperationException($"Dictionary page count mismatch. Expected {expectedCount}, got {count}.");
    }

    static void AssertDataPageCountGreaterThan(PageList pages, int minExclusive)
    {
        var count = 0;
        for (var i = 0; i < pages.Count; i++)
            if (pages[i].Kind == PageKind.DataV2)
                count++;

        if (count <= minExclusive)
            throw new InvalidOperationException($"Expected more than {minExclusive} data pages, got {count}.");
    }

    sealed class FixedRowsPageStrategy : IPageStrategy
    {
        readonly uint _rowsPerPage;

        internal FixedRowsPageStrategy(uint rowsPerPage)
            => _rowsPerPage = rowsPerPage;

        public DictionaryMode GetDictionaryMode()
            => DictionaryMode.Disabled;

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
            => currentPageRowCount >= _rowsPerPage;
    }
}
