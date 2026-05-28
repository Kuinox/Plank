using System.Buffers.Binary;
using System.Collections.Immutable;
using DuckDB.NET.Data;
using ParquetSharp;
using Plank.Reading;
using Plank.Schema;
using Plank.Writing;
using Plank.Writing.PageStrategy;
using PlankColumn = Plank.Schema.Column;
using PlankLogicalType = Plank.Schema.LogicalType;
using PlankParquetSchema = Plank.Schema.ParquetSchema;

namespace Plank.Tests.Writer;

internal sealed class ColumnStatisticsTests
{
    [Test]
    public void FloatStatisticsIgnoreNaNs()
    {
        var column = new PlankColumn("value", ParquetPhysicalType.Float);
        var statistics = ColumnStatistics.Create(column,
            [float.NaN, 3.5f, 1.25f, float.NaN, 9.75f, 2f, 4f, 8f, 6f], 0);

        if (statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.Float)
            throw new InvalidOperationException($"Expected float statistics, got {statistics.ValueKind}.");
        if (BitConverter.Int32BitsToSingle((int)statistics.MinBits) != 1.25f)
            throw new InvalidOperationException("Float min statistic mismatch.");
        if (BitConverter.Int32BitsToSingle((int)statistics.MaxBits) != 9.75f)
            throw new InvalidOperationException("Float max statistic mismatch.");
    }

    [Test]
    public void FloatStatisticsOmitMinMaxWhenAllValuesAreNaN()
    {
        var column = new PlankColumn("value", ParquetPhysicalType.Float);
        var statistics = ColumnStatistics.Create(column, [float.NaN, float.NaN], 0);

        if (statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.None)
            throw new InvalidOperationException($"Expected no min/max statistics, got {statistics.ValueKind}.");
    }

    [Test]
    public void DoubleStatisticsIgnoreNaNs()
    {
        var column = new PlankColumn("value", ParquetPhysicalType.Double);
        var statistics = ColumnStatistics.Create(column,
            [double.NaN, 3.5d, 1.25d, double.NaN, 9.75d, 2d, 4d, 8d, 6d], 0);

        if (statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.Double)
            throw new InvalidOperationException($"Expected double statistics, got {statistics.ValueKind}.");
        if (BitConverter.Int64BitsToDouble(statistics.MinBits) != 1.25d)
            throw new InvalidOperationException("Double min statistic mismatch.");
        if (BitConverter.Int64BitsToDouble(statistics.MaxBits) != 9.75d)
            throw new InvalidOperationException("Double max statistic mismatch.");
    }

    [Test]
    public void DoubleStatisticsOmitMinMaxWhenAllValuesAreNaN()
    {
        var column = new PlankColumn("value", ParquetPhysicalType.Double);
        var statistics = ColumnStatistics.Create(column, [double.NaN, double.NaN], 0);

        if (statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.None)
            throw new InvalidOperationException($"Expected no min/max statistics, got {statistics.ValueKind}.");
    }

    [Test]
    public void WriterEmitsColumnChunkStatistics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-statistics-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            new PlankColumn("id", ParquetPhysicalType.Int32),
            new PlankColumn("optional_id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional)),
            new PlankColumn("name", ParquetPhysicalType.ByteArray, null, new PlankLogicalType.String()),
            new PlankColumn("active", ParquetPhysicalType.Boolean)
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
                var idColumn = writer.CreateSerializedColumn<int>(schema.Columns[0]);
                var optionalIdColumn = writer.CreateSerializedColumn<int?>(schema.Columns[1]);
                var nameColumn = writer.CreateSerializedColumn<string>(schema.Columns[2]);
                var activeColumn = writer.CreateSerializedColumn<bool>(schema.Columns[3]);
                var rowGroup = writer.StartRowGroup();
                idColumn.Serialize([30, 10, 20]);
                optionalIdColumn.Serialize([3, null, 1]);
                nameColumn.Serialize(["beta", "alpha", "gamma"]);
                activeColumn.Serialize([true, false, true]);
                rowGroup.Write(idColumn);
                rowGroup.Write(optionalIdColumn);
                rowGroup.Write(nameColumn);
                rowGroup.Write(activeColumn);
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroupMetadata = reader.RowGroup(0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 0, min: 10, max: 30, nullCount: 0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 1, min: 1, max: 3, nullCount: 1);
            AssertHasMinMaxStatistics(rowGroupMetadata, columnIndex: 2, nullCount: 0);
            AssertBooleanStatistics(rowGroupMetadata, columnIndex: 3, min: false, max: true, nullCount: 0);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void WriterEmitsAllNullColumnChunkStatistics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-statistics-all-null-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            new PlankColumn("optional_id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ]);

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
                var optionalIdColumn = writer.CreateSerializedColumn<int?>(schema.Columns[0]);
                var rowGroup = writer.StartRowGroup();
                optionalIdColumn.Serialize([null, null, null]);
                rowGroup.Write(optionalIdColumn);
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroupMetadata = reader.RowGroup(0);
            AssertNoMinMaxStatistics(rowGroupMetadata, columnIndex: 0, nullCount: 3);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void WriterEmitsRepeatedColumnChunkStatistics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-statistics-repeated-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            ColumnDef.List("numbers", ColumnDef.RequiredLeaf("element", ParquetPhysicalType.Int32),
                repetition: ParquetRepetition.Required)
        ]);

        int[][] rows =
        [
            [5, 7],
            [],
            [1, 9]
        ];

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
                var numbersColumn = writer.CreateSerializedColumn<int[]>(schema.Columns[0]);
                var rowGroup = writer.StartRowGroup();
                numbersColumn.Serialize(rows);
                rowGroup.Write(numbersColumn);
                writer.CloseFile();
            }

            using var reader = new ParquetFileReader(path);
            using var rowGroupMetadata = reader.RowGroup(0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 0, min: 1, max: 9, nullCount: 1);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void WriterEmitsPageIndexes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-page-index-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            new PlankColumn("id", ParquetPhysicalType.Int32),
            new PlankColumn("optional_id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ])
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .Add("id", new FixedRowsPageStrategy(2))
                .Add("optional_id", new FixedRowsPageStrategy(2))
        };

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions
                {
                    WritePageIndexes = true
                });
                var idColumn = writer.CreateSerializedColumn<int>(schema.Columns[0]);
                var optionalIdColumn = writer.CreateSerializedColumn<int?>(schema.Columns[1]);
                var rowGroup = writer.StartRowGroup();
                idColumn.Serialize([10, 20, 30, 40, 50]);
                optionalIdColumn.Serialize([1, null, 3, 4, null]);
                rowGroup.Write(idColumn);
                rowGroup.Write(optionalIdColumn);
                writer.CloseFile();
            }

            var columns = ReadFirstRowGroupColumns(path);
            var fileBytes = File.ReadAllBytes(path);
            AssertPageIndexMetadata(fileBytes, columns[0], expectedPageCount: 3);
            AssertPageIndexMetadata(fileBytes, columns[1], expectedPageCount: 3);

            using var reader = new ParquetFileReader(path);
            using var rowGroupMetadata = reader.RowGroup(0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 0, min: 10, max: 50, nullCount: 0);
            AssertInt32Statistics(rowGroupMetadata, columnIndex: 1, min: 1, max: 4, nullCount: 2);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void RequiredInt32PageStatisticsMatchPageBoundaries()
    {
        using var stream = new MemoryStream();
        var schema = new PlankParquetSchema([
            new PlankColumn("id", ParquetPhysicalType.Int32)
        ])
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .Add("id", new FixedRowsPageStrategy(2))
        };
        var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
        var idColumn = writer.CreateSerializedColumn<int>(schema.Columns[0]);

        idColumn.Serialize([10, 50, -5, 40, 0]);

        AssertDataPageInt32Statistics(idColumn.Pages, pageIndex: 0, min: 10, max: 50, nullCount: 0);
        AssertDataPageInt32Statistics(idColumn.Pages, pageIndex: 1, min: -5, max: 40, nullCount: 0);
        AssertDataPageInt32Statistics(idColumn.Pages, pageIndex: 2, min: 0, max: 0, nullCount: 0);
    }

    [Test]
    public void PageIndexesAreReadableByDuckDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plank-page-index-duckdb-{Guid.NewGuid():N}.parquet");
        var schema = new PlankParquetSchema([
            new PlankColumn("id", ParquetPhysicalType.Int32),
            new PlankColumn("optional_id", ParquetPhysicalType.Int32, new ColumnOptions(ParquetRepetition.Optional))
        ])
        {
            PageStrategiesByColumnName = ImmutableDictionary<string, IPageStrategy>.Empty
                .Add("id", new FixedRowsPageStrategy(2))
                .Add("optional_id", new FixedRowsPageStrategy(2))
        };

        try
        {
            using (var stream = File.Create(path))
            {
                var writer = schema.CreateWriter(stream, new ParquetWriterOptions());
                var idColumn = writer.CreateSerializedColumn<int>(schema.Columns[0]);
                var optionalIdColumn = writer.CreateSerializedColumn<int?>(schema.Columns[1]);
                var rowGroup = writer.StartRowGroup();
                idColumn.Serialize([10, 20, 30, 40, 50]);
                optionalIdColumn.Serialize([1, null, 3, 4, null]);
                rowGroup.Write(idColumn);
                rowGroup.Write(optionalIdColumn);
                writer.CloseFile();
            }

            var columns = ReadFirstRowGroupColumns(path);
            var fileBytes = File.ReadAllBytes(path);
            AssertPageIndexMetadata(fileBytes, columns[0], expectedPageCount: 3);
            AssertPageIndexMetadata(fileBytes, columns[1], expectedPageCount: 3);

            AssertDuckDbCanReadPageIndexedFile(path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    static void AssertInt32Statistics(ParquetSharp.RowGroupReader rowGroupMetadata, int columnIndex, int min, int max,
        long nullCount)
    {
        using var columnMetadata = rowGroupMetadata.MetaData.GetColumnChunkMetaData(columnIndex);
        using var statistics = columnMetadata.Statistics
            ?? throw new InvalidOperationException($"Column {columnIndex} did not write statistics.");
        if (!statistics.HasMinMax)
            throw new InvalidOperationException($"Column {columnIndex} statistics did not include min/max.");
        if (statistics.NullCount != nullCount)
            throw new InvalidOperationException(
                $"Column {columnIndex} null count mismatch. Expected {nullCount}, got {statistics.NullCount}.");
        if (statistics.MinUntyped is not int actualMin || actualMin != min)
            throw new InvalidOperationException(
                $"Column {columnIndex} min mismatch. Expected {min}, got {statistics.MinUntyped}.");
        if (statistics.MaxUntyped is not int actualMax || actualMax != max)
            throw new InvalidOperationException(
                $"Column {columnIndex} max mismatch. Expected {max}, got {statistics.MaxUntyped}.");
    }

    static void AssertHasMinMaxStatistics(ParquetSharp.RowGroupReader rowGroupMetadata, int columnIndex, long nullCount)
    {
        using var columnMetadata = rowGroupMetadata.MetaData.GetColumnChunkMetaData(columnIndex);
        using var statistics = columnMetadata.Statistics
            ?? throw new InvalidOperationException($"Column {columnIndex} did not write statistics.");
        if (!statistics.HasMinMax)
            throw new InvalidOperationException($"Column {columnIndex} statistics did not include min/max.");
        if (statistics.NullCount != nullCount)
            throw new InvalidOperationException(
                $"Column {columnIndex} null count mismatch. Expected {nullCount}, got {statistics.NullCount}.");
    }

    static void AssertBooleanStatistics(ParquetSharp.RowGroupReader rowGroupMetadata, int columnIndex, bool min, bool max,
        long nullCount)
    {
        using var columnMetadata = rowGroupMetadata.MetaData.GetColumnChunkMetaData(columnIndex);
        using var statistics = columnMetadata.Statistics
            ?? throw new InvalidOperationException($"Column {columnIndex} did not write statistics.");
        if (!statistics.HasMinMax)
            throw new InvalidOperationException($"Column {columnIndex} statistics did not include min/max.");
        if (statistics.NullCount != nullCount)
            throw new InvalidOperationException(
                $"Column {columnIndex} null count mismatch. Expected {nullCount}, got {statistics.NullCount}.");
        if (statistics.MinUntyped is not bool actualMin || actualMin != min)
            throw new InvalidOperationException(
                $"Column {columnIndex} min mismatch. Expected {min}, got {statistics.MinUntyped}.");
        if (statistics.MaxUntyped is not bool actualMax || actualMax != max)
            throw new InvalidOperationException(
                $"Column {columnIndex} max mismatch. Expected {max}, got {statistics.MaxUntyped}.");
    }

    static void AssertNoMinMaxStatistics(ParquetSharp.RowGroupReader rowGroupMetadata, int columnIndex, long nullCount)
    {
        using var columnMetadata = rowGroupMetadata.MetaData.GetColumnChunkMetaData(columnIndex);
        using var statistics = columnMetadata.Statistics
            ?? throw new InvalidOperationException($"Column {columnIndex} did not write statistics.");
        if (statistics.HasMinMax)
            throw new InvalidOperationException($"Column {columnIndex} statistics unexpectedly included min/max.");
        if (statistics.NullCount != nullCount)
            throw new InvalidOperationException(
                $"Column {columnIndex} null count mismatch. Expected {nullCount}, got {statistics.NullCount}.");
    }

    static void AssertDuckDbCanReadPageIndexedFile(string path)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                count(*)::BIGINT,
                sum(id)::BIGINT,
                min(id)::INTEGER,
                max(id)::INTEGER,
                count(optional_id)::BIGINT,
                sum(optional_id)::BIGINT
            FROM read_parquet('{EscapeDuckDbPath(path)}')
            WHERE id BETWEEN 20 AND 40
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("DuckDB returned no rows.");
        AssertDuckDbInt64(reader, ordinal: 0, expected: 3);
        AssertDuckDbInt64(reader, ordinal: 1, expected: 90);
        AssertDuckDbInt32(reader, ordinal: 2, expected: 20);
        AssertDuckDbInt32(reader, ordinal: 3, expected: 40);
        AssertDuckDbInt64(reader, ordinal: 4, expected: 2);
        AssertDuckDbInt64(reader, ordinal: 5, expected: 7);
        if (reader.Read())
            throw new InvalidOperationException("DuckDB returned more than one aggregate row.");
    }

    static void AssertDuckDbInt64(DuckDBDataReader reader, int ordinal, long expected)
    {
        var actual = reader.GetInt64(ordinal);
        if (actual != expected)
            throw new InvalidOperationException($"DuckDB column {ordinal} mismatch. Expected {expected}, got {actual}.");
    }

    static void AssertDuckDbInt32(DuckDBDataReader reader, int ordinal, int expected)
    {
        var actual = reader.GetInt32(ordinal);
        if (actual != expected)
            throw new InvalidOperationException($"DuckDB column {ordinal} mismatch. Expected {expected}, got {actual}.");
    }

    static void AssertDataPageInt32Statistics(PageList pages, int pageIndex, int min, int max, long nullCount)
    {
        var dataPageIndex = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            ref var page = ref pages[i];
            if (page.Kind != PageKind.DataV2)
                continue;
            if (dataPageIndex == pageIndex)
            {
                var statistics = page.Statistics;
                if (statistics.ValueKind != ColumnStatistics.ColumnStatisticsValueKind.Int32)
                    throw new InvalidOperationException(
                        $"Page {pageIndex} statistics kind mismatch. Expected Int32, got {statistics.ValueKind}.");
                if (statistics.MinBits != min)
                    throw new InvalidOperationException(
                        $"Page {pageIndex} min mismatch. Expected {min}, got {statistics.MinBits}.");
                if (statistics.MaxBits != max)
                    throw new InvalidOperationException(
                        $"Page {pageIndex} max mismatch. Expected {max}, got {statistics.MaxBits}.");
                if (statistics.NullCount != nullCount)
                    throw new InvalidOperationException(
                        $"Page {pageIndex} null count mismatch. Expected {nullCount}, got {statistics.NullCount}.");
                return;
            }

            dataPageIndex++;
        }

        throw new InvalidOperationException($"Data page {pageIndex} was not written.");
    }

    static string EscapeDuckDbPath(string path)
        => path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);

    static void AssertPageIndexMetadata(byte[] fileBytes, InternalColumnChunkMetadata column, int expectedPageCount)
    {
        if (column.ColumnIndexOffset <= 0)
            throw new InvalidOperationException("Column index offset was not written.");
        if (column.ColumnIndexLength <= 0)
            throw new InvalidOperationException("Column index length was not written.");
        if (column.OffsetIndexOffset <= 0)
            throw new InvalidOperationException("Offset index offset was not written.");
        if (column.OffsetIndexLength <= 0)
            throw new InvalidOperationException("Offset index length was not written.");
        if (column.ColumnIndexOffset < column.ChunkOffset)
            throw new InvalidOperationException("Column index was written before the column chunk.");
        if (column.OffsetIndexOffset <= column.ColumnIndexOffset)
            throw new InvalidOperationException("Offset index was not written after the column index.");
        var actualStatisticsCount = ReadColumnIndexPageCount(fileBytes, (long)column.ColumnIndexOffset, (int)column.ColumnIndexLength);
        if (actualStatisticsCount != expectedPageCount)
            throw new InvalidOperationException(
                $"Column index page count mismatch. Expected {expectedPageCount}, got {actualStatisticsCount}.");
        var actualPageCount = ReadOffsetIndexPageCount(fileBytes, (long)column.OffsetIndexOffset, (int)column.OffsetIndexLength);
        if (actualPageCount != expectedPageCount)
            throw new InvalidOperationException(
                $"Offset index page count mismatch. Expected {expectedPageCount}, got {actualPageCount}.");
    }

    static InternalColumnChunkMetadata[] ReadFirstRowGroupColumns(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = ParquetReader.Open(stream);
        foreach (var token in reader.EnumerateRowGroups())
        {
            using var rowGroup = reader.OpenRowGroup(token);
            return rowGroup.PreviousColumns;
        }

        throw new InvalidOperationException("Expected at least one row group.");
    }

    static int ReadColumnIndexPageCount(byte[] fileBytes, long offset, int length)
    {
        var reader = new CompactProtocolReader(fileBytes.AsSpan(checked((int)offset), length));
        var previousFieldId = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId != 1)
            {
                reader.Skip(type, inlineBool);
                continue;
            }

            var (count, elementType) = reader.ReadListHeader();
            if (elementType is not (CompactProtocolType.BooleanTrue or CompactProtocolType.BooleanFalse))
                throw new InvalidOperationException("Column index null_pages was not a bool list.");
            for (var i = 0U; i < count; i++)
                _ = reader.ReadBool(inlineBool: null);
            return checked((int)count);
        }

        throw new InvalidOperationException("Column index did not include null page flags.");
    }

    static int ReadOffsetIndexPageCount(byte[] fileBytes, long offset, int length)
    {
        var reader = new CompactProtocolReader(fileBytes.AsSpan(checked((int)offset), length));
        var previousFieldId = 0;
        while (reader.TryReadFieldHeader(ref previousFieldId, out var fieldId, out var type, out var inlineBool))
        {
            if (fieldId != 1)
            {
                reader.Skip(type, inlineBool);
                continue;
            }

            var (count, elementType) = reader.ReadListHeader();
            if (elementType != CompactProtocolType.Struct)
                throw new InvalidOperationException("Offset index page_locations was not a struct list.");
            return checked((int)count);
        }

        throw new InvalidOperationException("Offset index did not include page locations.");
    }

    sealed class FixedRowsPageStrategy : IPageStrategy
    {
        readonly int _rowsPerPage;

        internal FixedRowsPageStrategy(int rowsPerPage)
            => _rowsPerPage = rowsPerPage;

        public DictionaryMode GetDictionaryMode()
            => DictionaryMode.Disabled;

        public DictionarySortOrder GetDictionarySortOrder()
            => DictionarySortOrder.Unknown;

        public void SetDictionarySortOrder(DictionarySortOrder sortOrder)
        {
        }

        public bool ShouldDropDictionary(uint uniqueCount, uint totalRowCount, uint rowsSeen)
            => false;

        public bool ShouldStartNewDataPage(uint totalRowCount, uint rowsWritten, uint currentPageRowCount)
            => currentPageRowCount >= (uint)_rowsPerPage;
    }
}
