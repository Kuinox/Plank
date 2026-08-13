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
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void OptionalInt32ByteStreamSplitUsesFixedTargetPages(ParquetDataPageVersion dataPageVersion)
    {
        int?[][] valuePatterns =
        [
            [int.MinValue, -100, -1, 0, 1, 17, 100, int.MaxValue],
            [null, int.MinValue, -1, null, 0, 17, null, int.MaxValue],
            [null, null, null, null, null, null, null, null]
        ];
        uint[] targetPageBytes = [1, 4, 5, 6, 10, 1024 * 1024];

        foreach (var targetBytes in targetPageBytes)
        foreach (var values in valuePatterns)
            AssertOptionalInt32ByteStreamSplitPages(values, dataPageVersion, targetBytes);
    }

    [Test]
    [Arguments(ParquetDataPageVersion.V1)]
    [Arguments(ParquetDataPageVersion.V2)]
    public void OptionalInt32ByteStreamSplitPreservesCustomPageStrategy(ParquetDataPageVersion dataPageVersion)
    {
        var schema = new PlankParquetSchema([
            ColumnDefinition.OptionalLeaf("id", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]),
                pageStrategy: new FixedRowsPageStrategy(2))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = 1
        });
        var column = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);

        column.Serialize([1, null, 3, 4, null]);

        AssertDataPageRows(column.Pages, [2, 2, 1]);
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
    [Arguments(EncodingKind.DeltaByteArray)]
    [Arguments(EncodingKind.DeltaLengthByteArray)]
    public void DeltaByteArrayColumnsSplitByTargetPageSize(EncodingKind encoding)
    {
        var schema = new PlankParquetSchema([
            ColumnDefinition.RequiredLeaf("required", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [encoding])),
            ColumnDefinition.OptionalLeaf("optional", ParquetPhysicalType.ByteArray,
                new ColumnOptions(encodings: [encoding]))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            TargetDataPageSizeBytes = 32
        });
        var requiredColumn = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[0]);
        var optionalColumn = writer.CreateSerializedColumn<byte[]>(schema.LeafColumns[1]);
        byte[][] values =
        [
            "abcdefghij"u8.ToArray(),
            "klmnopqrst"u8.ToArray(),
            "uvwxyzabcd"u8.ToArray(),
            "efghijklmn"u8.ToArray(),
            "opqrstuvwx"u8.ToArray()
        ];

        requiredColumn.Serialize(values);
        optionalColumn.Serialize(values);

        AssertDataPageRows(requiredColumn.Pages, [2, 2, 1]);
        AssertDataPageRows(optionalColumn.Pages, [2, 2, 1]);
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

    static void AssertOptionalInt32ByteStreamSplitPages(int?[] values,
        ParquetDataPageVersion dataPageVersion, uint targetPageBytes)
    {
        var schema = new PlankParquetSchema([
            ColumnDefinition.OptionalLeaf("id", ParquetPhysicalType.Int32,
                new ColumnOptions(encodings: [EncodingKind.ByteStreamSplit]))
        ]);
        using var stream = new MemoryStream();
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions
        {
            DataPageVersion = dataPageVersion,
            TargetDataPageSizeBytes = targetPageBytes,
            WritePageIndexes = true
        });
        var column = writer.CreateSerializedColumn<int?>(schema.LeafColumns[0]);

        column.Serialize(values);

        var rowsPerPage = Math.Max(1, checked((int)targetPageBytes) / (sizeof(int) + 1));
        var rowOffset = 0;
        var pageCount = 0;
        for (var pageIndex = 0; pageIndex < column.Pages.Count; pageIndex++)
        {
            ref var page = ref column.Pages[pageIndex];
            if (page.Kind != PageKind.DataV2)
                continue;

            var expectedRows = Math.Min(rowsPerPage, values.Length - rowOffset);
            if (page.RowCount != (uint)expectedRows)
                throw new InvalidOperationException(
                    $"Target {targetPageBytes} page {pageCount} row count mismatch. Expected {expectedRows}, got {page.RowCount}.");
            var pageValues = values.AsSpan(rowOffset, expectedRows);
            var presentCount = 0;
            for (var valueIndex = 0; valueIndex < pageValues.Length; valueIndex++)
                if (pageValues[valueIndex].HasValue)
                    presentCount++;
            var expectedContentBytes = checked((int)page.DefinitionLevelsByteLength + presentCount * sizeof(int)
                + (dataPageVersion == ParquetDataPageVersion.V1 ? sizeof(uint) : 0));
            if (page.UncompressedContentSize != expectedContentBytes || page.Content.WrittenLength != expectedContentBytes)
                throw new InvalidOperationException(
                    $"Target {targetPageBytes} page {pageCount} encoded byte count mismatch. Expected {expectedContentBytes}, got {page.Content.WrittenLength}.");
            if (page.Encoding != EncodingKind.ByteStreamSplit)
                throw new InvalidOperationException(
                    $"Target {targetPageBytes} page {pageCount} encoding mismatch. Expected ByteStreamSplit, got {page.Encoding}.");
            AssertStatistics(page.Statistics,
                ColumnStatistics.CreateOptional(schema.LeafColumns[0].Column, pageValues),
                $"target {targetPageBytes} page {pageCount}");
            rowOffset += expectedRows;
            pageCount++;
        }

        if (rowOffset != values.Length)
            throw new InvalidOperationException(
                $"Target {targetPageBytes} pages covered {rowOffset} rows, but the column contains {values.Length}.");
        var expectedPageCount = (values.Length + rowsPerPage - 1) / rowsPerPage;
        if (pageCount != expectedPageCount)
            throw new InvalidOperationException(
                $"Target {targetPageBytes} page count mismatch. Expected {expectedPageCount}, got {pageCount}.");
        AssertStatistics(column.Statistics,
            ColumnStatistics.CreateOptional(schema.LeafColumns[0].Column, values),
            $"target {targetPageBytes} column");

        writer.StartRowGroup().Write(column);
        writer.CloseFile();
        var fileBytes = stream.ToArray();
        AssertPageIndexMetadata(fileBytes);
        using var readStream = new MemoryStream(fileBytes, writable: false);
        using var reader = new ParquetSharp.ParquetFileReader(readStream, leaveOpen: false);
        using var rowGroup = reader.RowGroup(0);
        using var logicalReader = rowGroup.Column(0).LogicalReader<int?>();
        var actual = logicalReader.ReadAll(values.Length);
        if (!actual.AsSpan().SequenceEqual(values))
            throw new InvalidOperationException($"Target {targetPageBytes} round-trip mismatch.");
    }

    static void AssertStatistics(ColumnStatistics actual, ColumnStatistics expected, string context)
    {
        if (actual.ValueKind != expected.ValueKind || actual.MinBits != expected.MinBits
            || actual.MaxBits != expected.MaxBits || actual.NullCount != expected.NullCount
            || actual.DistinctCount != expected.DistinctCount || actual.NanCount != expected.NanCount
            || actual.HasStatistics != expected.HasStatistics)
            throw new InvalidOperationException($"Statistics mismatch for {context}.");
    }

    static void AssertPageIndexMetadata(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes, writable: false);
        using var reader = new Plank.Reading.Logical.ParquetReader();
        reader.Reset(stream);
        foreach (var rowGroup in reader.RowGroups)
        {
            var column = rowGroup.PreviousColumns[0];
            if (column.ColumnIndexOffset == 0 || column.ColumnIndexLength == 0
                || column.OffsetIndexOffset == 0 || column.OffsetIndexLength == 0)
                throw new InvalidOperationException("Page index metadata was not written.");
            return;
        }

        throw new InvalidOperationException("Expected one row group.");
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
