using System.Collections.Immutable;
using Plank.Schema;
using Plank.Writing;
using PlankColumn = Plank.Schema.Column;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.Writer;

internal sealed class PageSizeTests
{
    [Test]
    public void RequiredFixedWidthColumnSplitsByTargetPageSize()
    {
        var schema = new PlankParquetSchema([
            new PlankColumn("id", ParquetPhysicalType.Int32)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 8
        });
        var idColumn = writer.CreateSerializedColumn<int>(schema.Columns[0]);

        idColumn.Serialize([1, 2, 3, 4, 5]);

        AssertDataPageRows(idColumn.Pages, [2, 2, 1]);
    }

    [Test]
    public void RequiredVariableWidthColumnSplitsByTargetPageSize()
    {
        var schema = new PlankParquetSchema([
            new PlankColumn("name", ParquetPhysicalType.ByteArray)
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 32
        });
        var nameColumn = writer.CreateSerializedColumn<string>(schema.Columns[0]);

        nameColumn.Serialize(["abcdefghij", "klmnopqrst", "uvwxyzabcd", "efghijklmn", "opqrstuvwx"]);

        AssertDataPageRows(nameColumn.Pages, [2, 2, 1]);
    }

    [Test]
    public void OptionalColumnSplitsByTargetPageSize()
    {
        var schema = new PlankParquetSchema([
            new PlankColumn("id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 9
        });
        var idColumn = writer.CreateSerializedColumn<int?>(schema.Columns[0]);

        idColumn.Serialize([1, 2, null, 3]);

        AssertDataPageRows(idColumn.Pages, [1, 2, 1]);
    }

    [Test]
    public void DictionaryColumnSplitsDataPagesByTargetPageSize()
    {
        var schema = new PlankParquetSchema([
            new PlankColumn("name", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: ImmutableArray.Create(EncodingKind.RleDictionary)))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 4
        });
        var nameColumn = writer.CreateSerializedColumn<string>(schema.Columns[0]);
        var values = new string[100];
        for (var i = 0; i < values.Length; i++)
            values[i] = i % 2 == 0 ? "a" : "b";

        nameColumn.Serialize(values);

        AssertDictionaryPageCount(nameColumn.Pages, 1);
        AssertDataPageCountGreaterThan(nameColumn.Pages, 1);
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
            if (page.RowCount != expectedRows[dataPageIndex])
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
}
